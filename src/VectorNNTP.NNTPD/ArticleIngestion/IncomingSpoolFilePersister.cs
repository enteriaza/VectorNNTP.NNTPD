using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VectorNNTP.NNTPD.Configuration;

namespace VectorNNTP.NNTPD.ArticleIngestion;

/// <summary>
/// Persists accepted inbound articles under the configured incoming spool directory.
/// </summary>
public interface IIncomingArticlePersister
{
    /// <summary>Writes <paramref name="article"/> to durable incoming spool storage.</summary>
    Task PersistAsync(InboundArticle article, CancellationToken cancellationToken);
}

/// <summary>
/// Writes complete article payloads to <c>spool/incoming</c> using asynchronous file I/O.
/// </summary>
public sealed class IncomingSpoolFilePersister : IIncomingArticlePersister
{
    private readonly IOptions<NntpdOptions> _options;
    private readonly ILogger<IncomingSpoolFilePersister> _logger;

    /// <summary>Initializes a new instance of the <see cref="IncomingSpoolFilePersister"/> class.</summary>
    public IncomingSpoolFilePersister(
        IOptions<NntpdOptions> options,
        ILogger<IncomingSpoolFilePersister> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task PersistAsync(InboundArticle article, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(article);

        var directory = _options.Value.ArticleIngestion?.IncomingDirectory
            ?? ArticleIngestionOptions.DefaultIncomingDirectory;
        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, BuildFileName(article));
        var tempPath = path + ".tmp";

        try
        {
            await File.WriteAllBytesAsync(tempPath, article.Payload.ToArray(), cancellationToken)
                .ConfigureAwait(false);
            File.Move(tempPath, path, overwrite: true);
        }
        catch
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
                // Best-effort temp cleanup.
            }

            throw;
        }

        _logger.LogDebug(
            "Persisted incoming article {MessageId} ({Bytes} bytes) to {Path}.",
            article.MessageId,
            article.Payload.Length,
            path);
    }

    /// <summary>
    /// Builds a filesystem-safe unique name derived from the message-id and receive time.
    /// </summary>
    internal static string BuildFileName(InboundArticle article)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(article.MessageId)))
            .ToLowerInvariant();
        var ticks = article.ReceivedAtUtc.UtcTicks;
        return $"{hash}_{ticks:x}.article";
    }
}
