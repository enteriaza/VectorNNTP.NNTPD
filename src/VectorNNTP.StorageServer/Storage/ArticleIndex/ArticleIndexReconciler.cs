using Microsoft.Extensions.Logging;
using VectorNNTP.Common.Articles;
using VectorNNTP.StorageServer.Storage.SpoolShards;

namespace VectorNNTP.StorageServer.Storage.ArticleIndex;

/// <summary>
/// Repairs the crash window between a spool rename and a committed index row.
/// </summary>
/// <remarks>
/// The walk reads file names and last-write times. It does not read article bytes.
/// A missing index row is inserted through the writer. A row whose file is gone is counted
/// and left in place. This is not eviction. Names that are not 64 lowercase hex digits,
/// including temporary publish files, are ignored. The service awaits this walk before it
/// reports ready, so ingestion does not overlap it. A second start skips hashes already published.
/// </remarks>
internal static class ArticleIndexReconciler
{
    /// <summary>Walks validated spool roots and then probes committed rows.</summary>
    /// <param name="spools">Open spool databases. Empty skips the file walk.</param>
    /// <param name="presence">Membership set. A hash already published on another spool is still inserted here and kept.</param>
    /// <param name="index">Index that commits missing rows into the spool that holds the file.</param>
    /// <param name="counters">Reconciliation counters.</param>
    /// <param name="logger">Summary logger.</param>
    /// <param name="time">Clock for the summary.</param>
    /// <param name="cancellationToken">Stops the walk. Rows already queued can still commit.</param>
    /// <returns>The counts observed before completion or cancellation.</returns>
    internal static async Task<ArticleIndexReconciliationReport> RunAsync(
        IReadOnlyList<ArticleIndexOpenSpool> spools,
        ArticlePresenceIndex presence,
        IArticleIndex index,
        ArticleIndexCounters counters,
        ILogger logger,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(spools);
        ArgumentNullException.ThrowIfNull(presence);
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(counters);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(time);
        var started = time.GetTimestamp();
        long files = 0;
        long missing = 0;
        long present = 0;
        long probed = 0;
        long stale = 0;
        long fromClock = 0;
        long crossSpool = 0;
        foreach (var spool in spools)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var root = spool.Binding.SpoolDirectory;
            ArticlePayloadProbe.RequireReadable(root);

            foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = Path.GetFileName(path.AsSpan());
                if (name.Length != ArticleId.HexLength || !ArticleId.TryParseLowerHex(name, out var articleId))
                {
                    continue;
                }

                files++;
                if (spool.Database.TryRead(articleId, out _, counters: null))
                {
                    present++;
                    continue;
                }

                if (presence.Contains(articleId))
                {
                    crossSpool++;
                }

                var (unix, usedClock) = ReceiptUnixMilliseconds(path, time);
                if (usedClock)
                {
                    fromClock++;
                }

                await index.InsertStoredAsync(articleId, root, unix, cancellationToken).ConfigureAwait(false);
                missing++;
            }

            spool.Database.ForEachHash(
                articleId =>
                {
                    probed++;
                    if (!FileStillPresent(root, articleId))
                    {
                        stale++;
                    }
                },
                cancellationToken);
        }

        counters.NoteReconciliation(missing, stale);
        var elapsed = (long)time.GetElapsedTime(started).TotalMilliseconds;
        var report = new ArticleIndexReconciliationReport(files, missing, present, probed, stale, fromClock, crossSpool);
        ArticleIndexLogMessages.Reconciled(
            logger,
            report.FilesSeen,
            report.MissingQueued,
            report.AlreadyPresent,
            report.RowsProbed,
            report.StaleRows,
            report.ReceiptTimeFromClock,
            report.CrossSpoolDuplicates,
            elapsed);
        return report;
    }

    /// <summary>
    /// Chooses a recovery receipt time. A usable file last-write time is kept.
    /// A missing or pre-epoch time uses the reconciliation clock and is never stored as zero.
    /// Either value is a recovery approximation, not the original in-memory receive time.
    /// </summary>
    /// <param name="path">Final article file.</param>
    /// <param name="time">Clock used when the file time cannot be used.</param>
    /// <returns>The millisecond timestamp and whether the clock supplied it.</returns>
    private static (long UnixMilliseconds, bool FromClock) ReceiptUnixMilliseconds(string path, TimeProvider time)
    {
        DateTime written;
        try
        {
            written = File.GetLastWriteTimeUtc(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return (ClockUnixMilliseconds(time), true);
        }

        if (written <= DateTime.UnixEpoch)
        {
            return (ClockUnixMilliseconds(time), true);
        }

        return (new DateTimeOffset(written).ToUnixTimeMilliseconds(), false);
    }

    /// <summary>Reads the reconciliation clock as UTC Unix milliseconds.</summary>
    /// <param name="time">Clock.</param>
    /// <returns>A non-negative timestamp. A clock at the epoch yields zero because that is the clock, not a missing value.</returns>
    private static long ClockUnixMilliseconds(TimeProvider time)
    {
        var unix = time.GetUtcNow().ToUnixTimeMilliseconds();
        return unix < 0 ? 0 : unix;
    }

    /// <summary>Returns whether the final article file is still at the indexed spool.</summary>
    /// <param name="spoolDirectory">Stored spool root.</param>
    /// <param name="articleId">Canonical article identity.</param>
    /// <returns><see langword="false"/> when the path cannot be formed or the file is absent. An access error is counted as absent and does not fail startup.</returns>
    private static bool FileStillPresent(string spoolDirectory, ArticleId articleId)
    {
        try
        {
            return File.Exists(SpoolArticlePath.CombineArticle(spoolDirectory, articleId));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }
}
