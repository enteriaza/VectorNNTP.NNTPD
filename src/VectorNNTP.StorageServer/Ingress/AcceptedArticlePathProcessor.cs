using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Articles.Parsing;
using VectorNNTP.StorageServer.Configuration;

namespace VectorNNTP.StorageServer.Ingress;

/// <summary>
/// Applies <see cref="ArticlePathMode.Traverse"/> and drops the rewritten article.
/// </summary>
/// <remarks>
/// Nothing is written to disk, RabbitMQ, or the database. The rewritten buffer is not retained
/// after <see cref="TryProcess"/> returns. An optional observer exists so tests can see the
/// bytes immediately before that drop.
/// </remarks>
internal sealed class AcceptedArticlePathProcessor : IAcceptedArticleProcessor
{
    private readonly StorageServerRuntimeOptionsSource? _runtimeSource;
    private readonly string? _pathIdentity;
    private readonly ILogger _logger;
    private readonly Action<ReadOnlyMemory<byte>>? _rewritten;
    private NntpArticleParser? _parser;

    /// <summary>Initializes the host processor. The FQDN is read in <see cref="EnsureReady"/>.</summary>
    /// <param name="runtimeSource">Published runtime options.</param>
    /// <param name="logger">Failure logger. Article bodies are not written.</param>
    public AcceptedArticlePathProcessor(
        StorageServerRuntimeOptionsSource runtimeSource,
        ILogger<AcceptedArticlePathProcessor> logger)
    {
        ArgumentNullException.ThrowIfNull(runtimeSource);
        ArgumentNullException.ThrowIfNull(logger);
        _runtimeSource = runtimeSource;
        _logger = logger;
    }

    /// <summary>Initializes a processor with a known Path identity.</summary>
    /// <param name="pathIdentity">Server FQDN prepended by <see cref="ArticlePathMode.Traverse"/>.</param>
    /// <param name="logger">Failure logger.</param>
    /// <param name="rewritten">
    /// Called with the rewritten article bytes before they are dropped. Production does not set this.
    /// </param>
    internal AcceptedArticlePathProcessor(
        string pathIdentity,
        ILogger logger,
        Action<ReadOnlyMemory<byte>>? rewritten = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pathIdentity);
        ArgumentNullException.ThrowIfNull(logger);
        _pathIdentity = pathIdentity.Trim();
        _logger = logger;
        _rewritten = rewritten;
        _parser = new NntpArticleParser(_pathIdentity);
    }

    /// <inheritdoc />
    public void EnsureReady()
    {
        if (_parser is not null)
        {
            return;
        }

        var identity = _pathIdentity ?? _runtimeSource!.Current.Fqdn;
        if (string.IsNullOrWhiteSpace(identity))
        {
            throw new InvalidOperationException("StorageServer Path identity has not been published.");
        }

        _parser = new NntpArticleParser(identity.Trim());
    }

    /// <inheritdoc />
    public bool TryProcess(AcceptedArticle article)
    {
        EnsureReady();
        var created = ArticleRecordFactory.TryCreate(_parser!, article.ArtData, ArticlePathMode.Traverse);
        if (!created.IsAccepted)
        {
            AcceptedArticleWorkerLogMessages.PathRejected(
                _logger,
                article.ArtId.ToLowerHexString(),
                created.ParseFailure.ToString(),
                created.MaterializeFailure.ToString());
            return false;
        }

        if (created.Record.ArtId != article.ArtId)
        {
            AcceptedArticleWorkerLogMessages.PathIdentityChanged(
                _logger,
                article.ArtId.ToLowerHexString());
            return false;
        }

        _rewritten?.Invoke(created.Record.ArtData);
        return true;
    }
}
