using System.Text;

namespace VectorNNTP.NNTPD.Bench;

/// <summary>
/// Forensic IHAVE Pipe-reader report (not the IHAVE command benchmark).
/// </summary>
internal static class IhaveReaderReport
{
    public static string Format(IhaveCorpusRun run)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# IHAVE real-corpus Pipe benchmark");
        sb.AppendLine();
        sb.AppendLine($"Corpus: `{run.Inventory.Root}`");
        sb.AppendLine($"Files used: complete corpus (`used-files.txt`, {run.Inventory.Count} articles).");
        sb.AppendLine("Files are destuffed stored articles. The harness restuffs leading dots and appends `\\r\\n.\\r\\nDATE\\r\\n`.");
        sb.AppendLine("One iteration of every article per mode. No synthetic 256 KiB replacements.");
        sb.AppendLine();
        sb.AppendLine("## Corpus");
        sb.AppendLine();
        sb.AppendLine("| Metric | Value |");
        sb.AppendLine("|---|---:|");
        sb.AppendLine($"| articles | {run.Stats.Articles} |");
        sb.AppendLine($"| total bytes | {run.Stats.TotalBytes} |");
        sb.AppendLine($"| average | {run.Stats.Average:F1} |");
        sb.AppendLine($"| median / p50 | {run.Stats.Median:F1} |");
        sb.AppendLine($"| p90 | {run.Stats.P90:F1} |");
        sb.AppendLine($"| p95 | {run.Stats.P95:F1} |");
        sb.AppendLine($"| p99 | {run.Stats.P99:F1} |");
        sb.AppendLine($"| min | {run.Stats.Min} |");
        sb.AppendLine($"| max | {run.Stats.Max} |");
        sb.AppendLine();
        sb.AppendLine("### Types");
        sb.AppendLine();
        sb.AppendLine("| Type | Count | Percent |");
        sb.AppendLine("|---|---:|---:|");
        foreach (var (name, count) in run.Stats.Types)
        {
            var pct = run.Stats.Articles == 0 ? 0 : 100.0 * (int)count / run.Stats.Articles;
            sb.AppendLine($"| {name} | {count} | {pct:F2}% |");
        }

        sb.AppendLine();
        sb.AppendLine($"Content-Length headers: {run.Stats.ContentLengthCount}. yEnc `size=`: {run.Stats.YencSizeCount}. `Bytes:` headers: {run.Stats.BytesHeaderCount}.");
        sb.AppendLine();
        sb.AppendLine("### Size buckets");
        sb.AppendLine();
        sb.AppendLine("| Bucket | Count | Percent | Total bytes | Average |");
        sb.AppendLine("|---|---:|---:|---:|---:|");
        foreach (var bucket in run.Stats.SizeBuckets)
        {
            sb.AppendLine(
                $"| {bucket.Name} | {bucket.Count} | {bucket.Percent:F2}% | {bucket.TotalBytes} | {bucket.Average:F0} |");
        }

        sb.AppendLine();
        sb.AppendLine("## IHAVE — Prebuffered");
        sb.AppendLine();
        AppendMeasureTable(sb, run.Rows.Where(static r => r.Reader == "IHAVE" && r.Mode == ArrivalMode.Prebuffered));
        sb.AppendLine();
        sb.AppendLine("## IHAVE — Streaming");
        sb.AppendLine();
        AppendMeasureTable(sb, run.Rows.Where(static r => r.Reader == "IHAVE" && r.Mode == ArrivalMode.Streaming));
        return sb.ToString();
    }

    private static void AppendMeasureTable(StringBuilder sb, IEnumerable<IhaveMeasureRow> rows)
    {
        sb.AppendLine("| chunk | articles | avg size | ReadAsync/art | AdvanceTo/art | lines/art | bulk ops/art | bulk bytes/art | B/read | MB/s | art/s | leftover fail |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var row in rows)
        {
            var chunk = row.Mode == ArrivalMode.Prebuffered ? "prebuffered" : $"{row.ChunkBytes / 1024} KiB";
            sb.AppendLine(
                $"| {chunk} | {row.Articles} | {row.AverageSize:F0} | {row.ReadAsyncPerArticle:F2} | {row.AdvanceToPerArticle:F2} | {row.LinesPerArticle:F1} | {row.BulkOperationsPerArticle:F2} | {row.BulkBytesPerArticle:F0} | {row.BytesPerReadAsync:F0} | {row.MegabytesPerSecond:F1} | {row.ArticlesPerSecond:F1} | {row.LeftoverFailures} |");
        }
    }
}
