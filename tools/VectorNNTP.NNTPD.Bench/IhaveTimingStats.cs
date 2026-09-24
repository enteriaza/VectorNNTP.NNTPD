using System.Globalization;
using System.Text;

namespace VectorNNTP.NNTPD.Bench;

/// <summary>Percentile summary for sampled IHAVE client timings.</summary>
internal sealed class IhaveTimingStats
{
    public required string Name { get; init; }
    public int Count { get; init; }
    public double MeanUs { get; init; }
    public double P50Us { get; init; }
    public double P90Us { get; init; }
    public double P95Us { get; init; }
    public double P99Us { get; init; }
    public double MinUs { get; init; }
    public double MaxUs { get; init; }

    public static IhaveTimingStats From(string name, IReadOnlyList<IhaveTimingSample> samples, Func<IhaveTimingSample, long> selector)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(selector);
        if (samples.Count == 0)
        {
            throw new ArgumentException("Timing samples must not be empty.", nameof(samples));
        }

        var values = new double[samples.Count];
        double sum = 0;
        for (var i = 0; i < samples.Count; i++)
        {
            var value = selector(samples[i]);
            values[i] = value;
            sum += value;
        }

        Array.Sort(values);
        return new IhaveTimingStats
        {
            Name = name,
            Count = values.Length,
            MeanUs = sum / values.Length,
            P50Us = Percentile(values, 0.50),
            P90Us = Percentile(values, 0.90),
            P95Us = Percentile(values, 0.95),
            P99Us = Percentile(values, 0.99),
            MinUs = values[0],
            MaxUs = values[^1],
        };
    }

    internal static double Percentile(double[] sorted, double p)
    {
        if (sorted.Length == 1)
        {
            return sorted[0];
        }

        var position = p * (sorted.Length - 1);
        var lo = (int)position;
        var hi = Math.Min(lo + 1, sorted.Length - 1);
        var frac = position - lo;
        return sorted[lo] + ((sorted[hi] - sorted[lo]) * frac);
    }

    public string FormatRow() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{Name,-28} {MeanUs,10:F1} {P50Us,10:F1} {P90Us,10:F1} {P95Us,10:F1} {P99Us,10:F1}");
}

internal static class IhaveTimingReport
{
    public static string Format(
        BenchOptions options,
        IhavePreparedArticles articles,
        IReadOnlyList<IhaveTimingSample> samples,
        IhaveRunResult? throughput,
        string? artifactsDir)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(articles);
        ArgumentNullException.ThrowIfNull(samples);
        var sb = new StringBuilder();
        sb.AppendLine("IHAVE SERIALIZED COMMAND TIMING — DIAGNOSTIC");
        sb.AppendLine();
        sb.AppendLine("This is not the authoritative IHAVE throughput benchmark.");
        sb.AppendLine("Client-side Stopwatch.GetTimestamp phases only. Production IHAVE was not instrumented.");
        sb.AppendLine();
        sb.AppendLine($"Target:              {options.Host}:{options.PlainPort}");
        sb.AppendLine($"Connections:         {options.ConnectionsFilter ?? 1} (serialized)");
        sb.AppendLine($"Warmup:              {options.WarmupSeconds:F3}s (not sampled)");
        sb.AppendLine($"Samples:             {samples.Count:N0}");
        sb.AppendLine($"Corpus:              {articles.Root}");
        sb.AppendLine($"Catalog articles:    {articles.InventoryCount:N0}");
        sb.AppendLine($"Prepared articles:   {articles.PreparedCount:N0} ({articles.PreparedBytes:N0} wire bytes)");
        sb.AppendLine("HistoryDB:           production Peek + Remember (not isolated from 335 RTT)");
        sb.AppendLine("235 semantics:       queue admission succeeded; worker destuff is NOT on the 235 path");
        sb.AppendLine();
        sb.AppendLine("235 is written after HistoryDB Peek, 335, IHaveArticleReader, EnqueueAsync, and Remember.");
        sb.AppendLine("IhaveArticleInterpreter runs later in IncomingSpoolWriterService after 235.");
        sb.AppendLine();

        var avgBytes = samples.Average(static s => (double)s.ArticleBytes);
        sb.AppendLine($"Average article:     {avgBytes:N0} bytes (sampled wire, including terminator)");
        sb.AppendLine();

        var phases = new[]
        {
            IhaveTimingStats.From("command send", samples, static s => s.CommandSendUs),
            IhaveTimingStats.From("IHAVE sent → 335", samples, static s => s.IhaveSentTo335Us),
            IhaveTimingStats.From("335 → article sent", samples, static s => s.ArticleSendUs),
            IhaveTimingStats.From("article sent → 235", samples, static s => s.ArticleSentTo235Us),
            IhaveTimingStats.From("transaction (IHAVE→235)", samples, static s => s.TransactionUs),
        };

        sb.AppendLine("Phase timings (microseconds)");
        sb.AppendLine($"{"Phase",-28} {"Mean",10} {"P50",10} {"P90",10} {"P95",10} {"P99",10}");
        foreach (var phase in phases)
        {
            sb.AppendLine(phase.FormatRow());
        }

        sb.AppendLine();
        var article = phases[2];
        var xferSeconds = article.MeanUs / 1_000_000.0;
        var xferGbit = xferSeconds <= 0 ? 0 : avgBytes * 8.0 / xferSeconds / 1_000_000_000.0;
        sb.AppendLine($"Mean article transfer: {xferGbit:F3} Gbit/s (sampled article bytes / mean 335→article-sent)");
        sb.AppendLine();

        var txn = phases[^1];
        var impliedPerSec = txn.MeanUs <= 0 ? 0 : 1_000_000.0 / txn.MeanUs;
        var p50PerSec = txn.P50Us <= 0 ? 0 : 1_000_000.0 / txn.P50Us;
        sb.AppendLine($"Implied IHAVE/s from mean transaction: {impliedPerSec:F1}");
        sb.AppendLine($"Implied IHAVE/s from P50 transaction:  {p50PerSec:F1}");
        sb.AppendLine($"Implied µs from 777.3 IHAVE/s:         {1_000_000.0 / 777.3:F1} (published duration bench, elapsed includes 5s warmup)");
        sb.AppendLine($"Implied µs from 836.4 IHAVE/s:         {1_000_000.0 / 836.4:F1} (50183 / 60s measure window of run 1)");
        if (throughput is not null)
        {
            sb.AppendLine($"Wall IHAVE/s this timing run:          {throughput.ArticlesPerSec:F1} (includes warmup; not a throughput result)");
        }

        sb.AppendLine();
        sb.AppendLine("IHAVE sent → 335 includes: command parse/dispatch, HistoryDB Peek (local + Redis EXISTS),");
        sb.AppendLine("335 write, and one client↔server RTT. Redis EXISTS is not isolated without production probes.");
        sb.AppendLine("article sent → 235 includes: server receive/frame/own, queue EnqueueAsync, Remember, 235 write,");
        sb.AppendLine("and one RTT. Worker destuff is after 235 and is not in this interval.");
        if (artifactsDir is not null)
        {
            sb.AppendLine();
            sb.AppendLine($"Artifacts: {artifactsDir}");
        }

        return sb.ToString();
    }

    public static void WriteArtifacts(string articlesRoot, string report, IReadOnlyList<IhaveTimingSample> samples)
    {
        var dir = Path.Combine(IhaveReaderMeasure.FindArtifactsRoot(articlesRoot), "ihave-command-bench");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "timing.txt"), report, Encoding.UTF8);
        var csv = new StringBuilder();
        csv.AppendLine("article_bytes,command_send_us,ihave_sent_to_335_us,article_send_us,article_sent_to_235_us,transaction_us");
        foreach (var sample in samples)
        {
            csv.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{sample.ArticleBytes},{sample.CommandSendUs},{sample.IhaveSentTo335Us},{sample.ArticleSendUs},{sample.ArticleSentTo235Us},{sample.TransactionUs}"));
        }

        File.WriteAllText(Path.Combine(dir, "timing.csv"), csv.ToString(), Encoding.UTF8);
    }
}
