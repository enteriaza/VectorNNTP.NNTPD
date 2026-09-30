using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Articles.Parsing;
using VectorNNTP.StorageServer.Storage;
using VectorNNTP.StorageServer.Storage.Engine;

namespace VectorNNTP.StorageServer.Listener;

/// <summary>
/// Opens a VATP transfer from the published storage engine.
/// </summary>
/// <remarks>
/// Calls <see cref="IArticleStorageEngine.TryRead"/> only. Does not read segments, the
/// cache, the journal, or the index directly. Stored ArtData is already CanonicalV1.
/// Re-parsing with the organizational tracker as the local identity does not prepend
/// another Path hop when that tracker is already present, so a matching factory result
/// keeps the stored ArticleId, ArtHash, and ArtSize. A rewrite is rejected and is not sent.
/// </remarks>
public sealed class StorageArticleOpenBoundary : IStorageArticleOpenBoundary
{
    private readonly StorageEngineApplicationService _engine;
    private readonly NntpArticleParser _parser;

    /// <summary>Initializes the boundary against the hosted storage engine.</summary>
    /// <param name="engine">Engine owner. OPEN is rejected until <see cref="StorageEngineApplicationService.IsReady"/>.</param>
    public StorageArticleOpenBoundary(StorageEngineApplicationService engine)
        : this(engine, new NntpArticleParser(ArticlePathCanonicalizer.OrganizationalTrackerHost))
    {
    }

    /// <summary>Initializes the boundary with an explicit parser. Tests use this to force a canonical mismatch.</summary>
    /// <param name="engine">Engine owner.</param>
    /// <param name="parser">Canonical factory parser. Production uses the organizational tracker identity.</param>
    internal StorageArticleOpenBoundary(StorageEngineApplicationService engine, NntpArticleParser parser)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(parser);
        _engine = engine;
        _parser = parser;
    }

    /// <inheritdoc />
    public StorageArticleOpenResult TryOpen(Guid requestId, in ArticleId articleId)
    {
        _ = requestId;
        if (!_engine.IsReady)
        {
            return StorageArticleOpenResult.Rejected("storage-not-ready");
        }

        ArticleReadResult read;
        bool found;
        try
        {
            found = _engine.Engine.TryRead(articleId, out read);
        }
        catch (ObjectDisposedException)
        {
            throw;
        }
        catch (InvalidOperationException) when (!_engine.IsReady)
        {
            return StorageArticleOpenResult.Rejected("storage-not-ready");
        }

        if (!found || read.ArtData.Length != read.Metadata.ArtSize)
        {
            return StorageArticleOpenResult.Rejected("article-unavailable");
        }

        var created = ArticleRecordFactory.TryCreate(_parser, read.ArtData);
        if (!created.IsAccepted
            || created.Record.ParseStatus != ArticleParseStatus.CanonicalV1
            || created.Record.ArtId != read.Metadata.ArtId
            || created.Record.ArtHash != read.Metadata.ArtHash
            || created.Record.ArtSize != read.Metadata.ArtSize
            || created.Record.ArtSize != read.ArtData.Length
            || !created.Record.ArtData.Span.SequenceEqual(read.ArtData.Span))
        {
            return StorageArticleOpenResult.Rejected("canonical-mismatch");
        }

        return StorageArticleOpenResult.Opened(created.Record, created.SelectedDateHeaderName);
    }
}
