using System.Buffers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VectorNNTP.Common.Articles;
using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Ingress;
using VectorNNTP.StorageServer.Storage.ArticleIndex;
using VectorNNTP.StorageServer.Storage.SegmentPools;

namespace VectorNNTP.StorageServer.Storage.SpoolPersistence;

/// <summary>
/// Rewrites Path, publishes the rewritten bytes to a selected spool, and then indexes the article.
/// A successful VATP enqueue has already happened. This processor does not change that.
/// The index insert runs only after a stored or duplicate publish and waits for the SQLite commit.
/// </summary>
internal sealed class SpoolArticlePersistenceProcessor : IAcceptedArticleProcessor
{
    private readonly AcceptedArticlePathProcessor _path;
    private readonly SegmentPoolCatalog _catalog;
    private readonly BlockDeviceMonitorService _monitor;
    private readonly SpoolWriteGate _gate;
    private readonly SpoolArticleIdentityGate _identity;
    private readonly SpoolHealth _health;
    private readonly SpoolPressure _pressure;
    private readonly SpoolArticlePublisher _publisher;
    private readonly SpoolPersistenceCounters _counters;
    private readonly IOptions<StorageServerOptions> _options;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly IArticleIndex? _index;
    private IReadOnlyList<SegmentPoolDevice>? _rootSource;
    private string[] _roots = [];

    /// <summary>Initializes the host processor.</summary>
    /// <param name="runtime">Published server FQDN used as the Path hop.</param>
    /// <param name="catalog">Validated spool roots.</param>
    /// <param name="monitor">Device rates and free-space snapshots.</param>
    /// <param name="gate">Per-spool in-flight cap.</param>
    /// <param name="identity">Serializes publication of one article id across spools.</param>
    /// <param name="health">Publish failure state.</param>
    /// <param name="pressure">Signal the worker control loop reads.</param>
    /// <param name="publisher">Temp-file publisher.</param>
    /// <param name="counters">Persistence counters.</param>
    /// <param name="options">Critical percent and per-spool limit.</param>
    /// <param name="pathLogger">Path-rewrite failure logger.</param>
    /// <param name="logger">Publish logger.</param>
    /// <param name="time">Clock for sample freshness and the single index receipt timestamp.</param>
    /// <param name="articleIndex">Durable article index updated after a successful spool publish.</param>
    public SpoolArticlePersistenceProcessor(
        StorageServerRuntimeOptionsSource runtime,
        SegmentPoolCatalog catalog,
        BlockDeviceMonitorService monitor,
        SpoolWriteGate gate,
        SpoolArticleIdentityGate identity,
        SpoolHealth health,
        SpoolPressure pressure,
        SpoolArticlePublisher publisher,
        SpoolPersistenceCounters counters,
        IOptions<StorageServerOptions> options,
        ILogger<AcceptedArticlePathProcessor> pathLogger,
        ILogger<SpoolArticlePersistenceProcessor> logger,
        TimeProvider time,
        IArticleIndex articleIndex)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(health);
        ArgumentNullException.ThrowIfNull(pressure);
        ArgumentNullException.ThrowIfNull(publisher);
        ArgumentNullException.ThrowIfNull(counters);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(pathLogger);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(articleIndex);
        _path = new AcceptedArticlePathProcessor(runtime, pathLogger);
        _catalog = catalog;
        _monitor = monitor;
        _gate = gate;
        _identity = identity;
        _health = health;
        _pressure = pressure;
        _publisher = publisher;
        _counters = counters;
        _options = options;
        _logger = logger;
        _time = time;
        _index = articleIndex;
    }

    /// <summary>Initializes a processor with an explicit Path identity for tests.</summary>
    /// <param name="pathIdentity">Server FQDN prepended to Path.</param>
    /// <param name="catalog">Validated spool roots.</param>
    /// <param name="monitor">Device rates and free-space snapshots.</param>
    /// <param name="gate">Per-spool in-flight cap.</param>
    /// <param name="identity">Serializes publication of one article id across spools.</param>
    /// <param name="health">Publish failure state.</param>
    /// <param name="pressure">Signal the worker control loop reads.</param>
    /// <param name="publisher">Temp-file publisher.</param>
    /// <param name="counters">Persistence counters.</param>
    /// <param name="options">Critical percent and per-spool limit.</param>
    /// <param name="logger">Failure logger.</param>
    /// <param name="time">Clock for sample freshness and the single index receipt timestamp.</param>
    /// <param name="articleIndex">Null skips indexing. Production does not use this constructor.</param>
    internal SpoolArticlePersistenceProcessor(
        string pathIdentity,
        SegmentPoolCatalog catalog,
        BlockDeviceMonitorService monitor,
        SpoolWriteGate gate,
        SpoolArticleIdentityGate identity,
        SpoolHealth health,
        SpoolPressure pressure,
        SpoolArticlePublisher publisher,
        SpoolPersistenceCounters counters,
        StorageServerOptions options,
        ILogger logger,
        TimeProvider time,
        IArticleIndex? articleIndex = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pathIdentity);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(health);
        ArgumentNullException.ThrowIfNull(pressure);
        ArgumentNullException.ThrowIfNull(publisher);
        ArgumentNullException.ThrowIfNull(counters);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(time);
        _path = new AcceptedArticlePathProcessor(pathIdentity, logger);
        _catalog = catalog;
        _monitor = monitor;
        _gate = gate;
        _identity = identity;
        _health = health;
        _pressure = pressure;
        _publisher = publisher;
        _counters = counters;
        _options = Options.Create(options);
        _logger = logger;
        _time = time;
        _index = articleIndex;
    }

    /// <inheritdoc />
    public void EnsureReady() => _path.EnsureReady();

    /// <inheritdoc />
    public async ValueTask<AcceptedArticleProcessResult> ProcessAsync(
        AcceptedArticle article,
        CancellationToken cancellationToken)
    {
        var reserved = false;
        if (_index is not null)
        {
            var hold = await _index.ReserveIngestionAsync(article.ArtId, cancellationToken).ConfigureAwait(false);
            if (hold == ArticleIngestionHold.AlreadyPresent)
            {
                _counters.NoteDuplicate();
                return AcceptedArticleProcessResult.Completed;
            }

            reserved = true;
        }

        try
        {
            var outcome = ProcessCore(article, cancellationToken);
            if (!outcome.Index || _index is null)
            {
                return outcome.Result;
            }

            var receivedAt = _time.GetUtcNow().ToUnixTimeMilliseconds();
            try
            {
                var indexed = await _index.InsertStoredAsync(article.ArtId, outcome.Spool, receivedAt, cancellationToken)
                    .ConfigureAwait(false);
                return indexed.Committed ? outcome.Result : AcceptedArticleProcessResult.Failed;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                ArticleIndexLogMessages.IndexFailedAfterStore(_logger, ex, outcome.Spool);
                return AcceptedArticleProcessResult.Failed;
            }
        }
        finally
        {
            if (reserved)
            {
                _index!.ReleaseIngestion(article.ArtId);
            }
        }
    }

    /// <summary>
    /// Publishes one article. Indexing is reported separately so this method can keep using spans.
    /// </summary>
    /// <param name="article">Dequeued article.</param>
    /// <param name="cancellationToken">Cancels the publish.</param>
    /// <returns>The worker result and, when a final file is in place, the spool that should be indexed.</returns>
    private PersistenceOutcome ProcessCore(AcceptedArticle article, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        byte[]? rented = null;
        try
        {
            if (!_path.TryRewriteRented(article, out rented, out var length) || rented is null)
            {
                return PersistenceOutcome.Done(AcceptedArticleProcessResult.Failed);
            }

            var rewritten = rented.AsMemory(0, length);
            if (rewritten.Length != rewritten.Span.Length)
            {
                return PersistenceOutcome.Done(AcceptedArticleProcessResult.Failed);
            }

            using var lease = _identity.Acquire(article.ArtId, cancellationToken);
            var roots = SpoolRoots();
            if (_publisher.TryClassifyExisting(roots, article.ArtId, rewritten.Span, out var spoolRoot, out var existing))
            {
                return PersistenceOutcome.Indexable(Apply(article, spoolRoot, rewritten.Length, existing), spoolRoot, existing.Kind);
            }

            var persistence = _options.Value.SpoolPersistence ?? new SpoolPersistenceOptions();
            var candidates = ArrayPool<SpoolCandidate>.Shared.Rent(Math.Max(roots.Length, 1));
            List<string>? blocked = null;
            try
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var written = SpoolCandidateBuilder.Fill(
                        _catalog,
                        _monitor.Observations,
                        _gate,
                        _health,
                        persistence.MaxInFlightPerSpool,
                        _time,
                        candidates);
                    var observed = candidates.AsSpan(0, written);
                    if (blocked is not null)
                    {
                        for (var i = 0; i < observed.Length; i++)
                        {
                            if (blocked.Contains(observed[i].SpoolRoot))
                            {
                                observed[i] = observed[i] with { AcceptsWrites = false };
                            }
                        }
                    }

                    if (!_gate.TryReserve(observed, rewritten.Length, out var chosen))
                    {
                        _pressure.SetSaturated(true);
                        _counters.NoteDeferred();
                        if (_logger.IsEnabled(LogLevel.Warning))
                        {
                            SpoolPersistenceLogMessages.Deferred(_logger, article.ArtId.ToLowerHexString());
                        }

                        return PersistenceOutcome.Done(AcceptedArticleProcessResult.WaitingForCapacity);
                    }

                    _pressure.SetSaturated(false);
                    try
                    {
                        var published = _publisher.Publish(chosen.SpoolRoot, article.ArtId, rewritten.Span, cancellationToken);
                        var result = Apply(article, chosen.SpoolRoot, rewritten.Length, published);
                        if (result != AcceptedArticleProcessResult.WaitingForCapacity)
                        {
                            return PersistenceOutcome.Indexable(result, chosen.SpoolRoot, published.Kind);
                        }

                        blocked ??= new List<string>(roots.Length);
                        blocked.Add(chosen.SpoolRoot);
                    }
                    finally
                    {
                        _gate.Exit(chosen.SpoolRoot);
                    }
                }
            }
            finally
            {
                ArrayPool<SpoolCandidate>.Shared.Return(candidates);
            }
        }
        finally
        {
            if (rented is not null)
            {
                AcceptedArticlePathProcessor.ReturnRewriteBuffer(rented);
            }
        }
    }

    /// <summary>Validated spool roots in catalog order. Reused while the catalog array is unchanged.</summary>
    /// <returns>The roots a same-id publish must scan before writing a second copy.</returns>
    private string[] SpoolRoots()
    {
        var devices = _catalog.Devices;
        var cached = Volatile.Read(ref _roots);
        if (ReferenceEquals(Volatile.Read(ref _rootSource), devices))
        {
            return cached;
        }

        var count = 0;
        foreach (var device in devices)
        {
            count += device.PoolPaths.Count;
        }

        var roots = new string[count];
        var index = 0;
        foreach (var device in devices)
        {
            foreach (var path in device.PoolPaths)
            {
                roots[index++] = path;
            }
        }

        Volatile.Write(ref _roots, roots);
        Volatile.Write(ref _rootSource, devices);
        return roots;
    }

    /// <summary>Maps a publish result onto health, counters, and the worker result.</summary>
    /// <param name="article">Dequeued article.</param>
    /// <param name="spool">Spool that was attempted.</param>
    /// <param name="byteCount">Length of the rewritten article that was offered to the publisher.</param>
    /// <param name="published">Publisher outcome.</param>
    /// <returns>Completed, failed, or waiting so the worker can try another spool.</returns>
    private AcceptedArticleProcessResult Apply(
        AcceptedArticle article,
        string spool,
        int byteCount,
        SpoolPublishResult published)
    {
        switch (published.Kind)
        {
            case SpoolPublishKind.Stored:
                _health.Success(spool);
                _counters.NoteStored(byteCount);
                _monitor.NoteCommittedBytes(spool, byteCount);
                SpoolPersistenceLogMessages.Stored(_logger, spool, byteCount);
                return AcceptedArticleProcessResult.Completed;
            case SpoolPublishKind.Duplicate:
                _health.Success(spool);
                _counters.NoteDuplicate();
                SpoolPersistenceLogMessages.Duplicate(_logger, spool);
                return AcceptedArticleProcessResult.Completed;
            case SpoolPublishKind.Conflict:
                _counters.NoteConflict();
                SpoolPersistenceLogMessages.Conflict(_logger, article.ArtId.ToLowerHexString(), published.FinalPath);
                return AcceptedArticleProcessResult.Failed;
            case SpoolPublishKind.DiskFull:
                _health.Failure(spool, close: true);
                _counters.NoteDiskFull();
                SpoolPersistenceLogMessages.PublishFailed(
                    _logger,
                    article.ArtId.ToLowerHexString(),
                    spool,
                    published.Kind.ToString(),
                    published.Detail ?? string.Empty);
                return AcceptedArticleProcessResult.WaitingForCapacity;
            case SpoolPublishKind.AccessDenied:
                _health.Failure(spool, close: false);
                _counters.NoteAccessDenied();
                SpoolPersistenceLogMessages.PublishFailed(
                    _logger,
                    article.ArtId.ToLowerHexString(),
                    spool,
                    published.Kind.ToString(),
                    published.Detail ?? string.Empty);
                return AcceptedArticleProcessResult.WaitingForCapacity;
            default:
                _health.Failure(spool, close: false);
                _counters.NoteIoFailure();
                SpoolPersistenceLogMessages.PublishFailed(
                    _logger,
                    article.ArtId.ToLowerHexString(),
                    spool,
                    published.Kind.ToString(),
                    published.Detail ?? string.Empty);
                return AcceptedArticleProcessResult.WaitingForCapacity;
        }
    }
}

/// <summary>Publish result plus whether the caller should wait for an index commit.</summary>
/// <param name="Result">Worker result.</param>
/// <param name="Index">True only after a stored or duplicate final file is in place.</param>
/// <param name="Spool">Spool root to record. Empty when <paramref name="Index"/> is false.</param>
internal readonly record struct PersistenceOutcome(AcceptedArticleProcessResult Result, bool Index, string Spool)
{
    /// <summary>Builds an outcome that does not touch the article index.</summary>
    /// <param name="result">Worker result.</param>
    /// <returns>An outcome with indexing disabled.</returns>
    internal static PersistenceOutcome Done(AcceptedArticleProcessResult result) => new(result, false, string.Empty);

    /// <summary>
    /// Builds an outcome that indexes only a stored or duplicate completion.
    /// Conflicts and retryable failures are not indexed.
    /// </summary>
    /// <param name="result">Worker result from <c>Apply</c>.</param>
    /// <param name="spool">Spool root that holds or rejected the file.</param>
    /// <param name="kind">Publish kind.</param>
    /// <returns>The outcome. <see cref="Index"/> is true only for a completed store or duplicate.</returns>
    internal static PersistenceOutcome Indexable(AcceptedArticleProcessResult result, string spool, SpoolPublishKind kind) =>
        new(
            result,
            result == AcceptedArticleProcessResult.Completed
                && kind is SpoolPublishKind.Stored or SpoolPublishKind.Duplicate,
            spool);
}
