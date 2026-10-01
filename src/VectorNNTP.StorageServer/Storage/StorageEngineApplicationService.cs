using VectorNNTP.NNTPD.Core;
using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Storage.Cache;
using VectorNNTP.StorageServer.Storage.Engine.Durable;
using Microsoft.Extensions.Options;

namespace VectorNNTP.StorageServer.Storage;

/// <summary>
/// Owns <see cref="FileArticleStorageEngine"/> Open + Recover + Dispose for StorageServer hosting.
/// </summary>
/// <remarks>
/// <para>
/// Completing <see cref="StartAsync"/> is the readiness boundary: the engine is open, recovered,
/// and the sole instance exposed to dependents. Does not run maintenance or VATP article serving.
/// </para>
/// <para>
/// Startup order: register this service before any storage-dependent application service.
/// Shutdown is reverse order via <see cref="ApplicationServiceManager"/>.
/// </para>
/// </remarks>
public sealed class StorageEngineApplicationService : IApplicationService, IAsyncDisposable
{
    private readonly StorageServerRuntimeOptions _runtime;
    private readonly IOptions<StorageServerOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<StorageEngineApplicationService> _logger;
    private FileArticleStorageEngine? _engine;
    private int _started;

    /// <summary>Initializes a new storage-engine hosting service.</summary>
    public StorageEngineApplicationService(
        StorageServerRuntimeOptions runtime,
        IOptions<StorageServerOptions> options,
        ILogger<StorageEngineApplicationService> logger,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _runtime = runtime;
        _options = options;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public string Name => "StorageEngine";

    /// <inheritdoc />
    public Task? Execution => null;

    /// <summary>
    /// Gets whether <see cref="StartAsync"/> completed successfully and the engine is published.
    /// </summary>
    public bool IsReady => Volatile.Read(ref _started) == 1 && _engine is not null;

    /// <summary>
    /// Invoked after open and before <see cref="FileArticleStorageEngine.RecoverAsync"/>.
    /// Tests only. Cleared before invoke. Production startup leaves this null.
    /// </summary>
    internal Action<FileArticleStorageEngine>? TestBeforeRecover { get; set; }

    /// <summary>
    /// Gets the open, recovered engine after successful <see cref="StartAsync"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when the engine is not ready.</exception>
    public FileArticleStorageEngine Engine =>
        _engine ?? throw new InvalidOperationException(
            "Storage engine is not ready. StorageEngineApplicationService.StartAsync has not completed successfully.");

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            return;
        }

        FileArticleStorageEngine? engine = null;
        try
        {
            var storage = _runtime.Storage;
            StorageEngineLogMessages.Opening(
                _logger,
                storage.ControlDir,
                storage.SegmentDir);

            var cacheOptions = _options.Value.Storage?.ArticleCache ?? new ArticleMemoryCacheOptions();
            var cache = new ArticleMemoryCache(cacheOptions);

            engine = FileArticleStorageEngine.Open(
                storage,
                _logger,
                _timeProvider,
                cache);

            var beforeRecover = TestBeforeRecover;
            TestBeforeRecover = null;
            beforeRecover?.Invoke(engine);

            StorageEngineLogMessages.RecoveryStarting(_logger, storage.ControlDir);
            await engine.RecoverAsync(cancellationToken).ConfigureAwait(false);

            _engine = engine;
            engine = null;
            StorageEngineLogMessages.Ready(_logger, storage.ControlDir, storage.SegmentDir);
        }
        catch (Exception ex)
        {
            StorageEngineLogMessages.StartupFailed(_logger, ex);
            if (engine is not null)
            {
                await engine.DisposeAsync().ConfigureAwait(false);
            }

            Interlocked.Exchange(ref _started, 0);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        await DisposeEngineAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => new(DisposeEngineAsync());

    private async Task DisposeEngineAsync()
    {
        var engine = Interlocked.Exchange(ref _engine, null);
        if (engine is null)
        {
            Interlocked.Exchange(ref _started, 0);
            return;
        }

        StorageEngineLogMessages.Stopping(_logger);
        try
        {
            await engine.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Exchange(ref _started, 0);
            StorageEngineLogMessages.Stopped(_logger);
        }
    }
}
