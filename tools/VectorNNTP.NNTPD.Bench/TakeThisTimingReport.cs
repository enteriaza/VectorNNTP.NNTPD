using System.Globalization;
using System.Text;

namespace VectorNNTP.NNTPD.Bench;

internal static class TakeThisTimingReport
{
    public static string Format(
        BenchOptions options,
        IReadOnlyList<TakeThisClientTimingSample> samples,
        TakeThisRunResult result,
        int run,
        string? artifactsDir)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(result);
        var sb = new StringBuilder();
        sb.AppendLine($"TAKETHIS CLIENT TIMING — RUN {run} (DIAGNOSTIC)");
        sb.AppendLine();
        sb.AppendLine("Client-side Stopwatch.GetTimestamp. Server pipeline stages are in the");
        sb.AppendLine("VECTORNNTP_TAKETHIS_TIMING dump written on session shutdown.");
        sb.AppendLine();
        sb.AppendLine($"Target:              {options.Host}:{options.PlainPort}");
        sb.AppendLine($"Connections:         {result.Connections}");
        sb.AppendLine($"Warmup:              {options.WarmupSeconds:F3}s (not sampled)");
        sb.AppendLine($"Measure:             {options.MeasureSeconds:F3}s");
        sb.AppendLine($"Pipeline depth:      {options.PipelineDepth} (client) / server window 16");
        sb.AppendLine($"Samples:             {samples.Count:N0}");
        sb.AppendLine($"TAKETHIS/s:          {result.ArticlesPerSec:N1}");
        sb.AppendLine($"Max outstanding:     {result.MaxOutstanding}");
        sb.AppendLine();
        if (samples.Count == 0)
        {
            sb.AppendLine("No client samples.");
            return sb.ToString();
        }

        sb.AppendLine($"{"Phase",-32} {"Mean",10} {"P50",10} {"P95",10} {"P99",10} {"Max",10}");
        sb.AppendLine(Row("command+article send", samples, static s => s.CommandAndArticleSendUs));
        sb.AppendLine(Row("send complete → 239", samples, static s => s.SendTo239Us));
        sb.AppendLine(Row("transaction (send→239)", samples, static s => s.TransactionUs));
        sb.AppendLine();
        sb.AppendLine($"Max outstanding@send: {samples.Max(static s => s.OutstandingAtSend)}");
        sb.AppendLine($"Max outstanding@239:  {samples.Max(static s => s.OutstandingAt239)}");
        if (artifactsDir is not null)
        {
            sb.AppendLine();
            sb.AppendLine($"Artifacts: {artifactsDir}");
        }

        return sb.ToString();
    }

    public static void WriteArtifacts(
        string artifactsDir,
        int run,
        string report,
        IReadOnlyList<TakeThisClientTimingSample> samples)
    {
        Directory.CreateDirectory(artifactsDir);
        File.WriteAllText(Path.Combine(artifactsDir, $"client-run{run}.txt"), report, Encoding.UTF8);
        var csv = new StringBuilder();
        csv.AppendLine("send_us,send_to_239_us,transaction_us,outstanding_send,outstanding_239");
        foreach (var sample in samples)
        {
            csv.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{sample.CommandAndArticleSendUs},{sample.SendTo239Us},{sample.TransactionUs},{sample.OutstandingAtSend},{sample.OutstandingAt239}"));
        }

        File.WriteAllText(Path.Combine(artifactsDir, $"client-run{run}.csv"), csv.ToString(), Encoding.UTF8);
    }

    private static string Row(
        string name,
        IReadOnlyList<TakeThisClientTimingSample> samples,
        Func<TakeThisClientTimingSample, long> selector)
    {
        var values = new double[samples.Count];
        double sum = 0;
        for (var i = 0; i < samples.Count; i++)
        {
            var value = selector(samples[i]);
            values[i] = value;
            sum += value;
        }

        Array.Sort(values);
        var mean = sum / values.Length;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{name,-32} {mean,10:F1} {IhaveTimingStats.Percentile(values, 0.50),10:F1} {IhaveTimingStats.Percentile(values, 0.95),10:F1} {IhaveTimingStats.Percentile(values, 0.99),10:F1} {values[^1],10:F1}");
    }
}
