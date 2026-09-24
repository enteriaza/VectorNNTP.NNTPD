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
        sb.AppendLine($"Pipeline depth:      {options.PipelineDepth} (client 239-outstanding) / server window 16");
        sb.AppendLine($"Sender depth:        {result.SenderDepth} (concurrent Socket.SendAsync articles)");
        sb.AppendLine($"Samples:             {samples.Count:N0}");
        sb.AppendLine($"TAKETHIS/s (elapsed includes warmup): {result.ArticlesPerSec:N1}");
        if (result.MeasureElapsedSeconds > 0 && result.Sent > 0)
        {
            var measureRate = result.Sent / result.MeasureElapsedSeconds;
            var measureGbit = result.Sent * (double)result.ArticleBytes * 8.0 / result.MeasureElapsedSeconds / 1_000_000_000.0;
            sb.AppendLine($"TAKETHIS/s (measure window only):     {measureRate:N1}");
            sb.AppendLine($"Gbit/s (measure window only):         {measureGbit:F3}");
        }

        sb.AppendLine($"Max outstanding 239: {result.MaxOutstanding}");
        sb.AppendLine($"Max active sends:    {result.MaxActiveSends}");
        sb.AppendLine($"Max awaiting 239:    {result.MaxAwaiting239}");
        if (result.SendCalls > 0)
        {
            sb.AppendLine($"SendAsync calls:     {result.SendCalls:N0}");
            sb.AppendLine(
                $"Bytes/SendAsync:     avg {(double)result.SendBytesReturned / result.SendCalls:N0}  min {result.MinSendBytes:N0}  max {result.MaxSendBytes:N0}");
        }

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
        sb.AppendLine($"SendAsync calls/article: mean {samples.Average(static s => (double)s.SendCalls):F2}  max {samples.Max(static s => s.SendCalls)}");
        sb.AppendLine($"First SendAsync bytes:   mean {samples.Average(static s => (double)s.FirstSendBytes):N0}  min {samples.Min(static s => s.FirstSendBytes):N0}  max {samples.Max(static s => s.FirstSendBytes):N0}");
        sb.AppendLine($"Max active sends@start:  {samples.Max(static s => s.ActiveSendsAtStart)}");
        sb.AppendLine($"Max outstanding@send:    {samples.Max(static s => s.OutstandingAtSend)}");
        sb.AppendLine($"Max outstanding@239:     {samples.Max(static s => s.OutstandingAt239)}");
        sb.AppendLine($"Max awaiting-239@send:   {samples.Max(static s => s.Awaiting239AtSendStart)}");
        sb.AppendLine();
        sb.AppendLine("Canonical sender is serial: the next article SendAsync starts only after the previous");
        sb.AppendLine("article's bytes have been accepted by the socket. pipeline-depth limits 239-outstanding,");
        sb.AppendLine("not concurrent physical sends. Command and article are one vectored SendAsync pair.");
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
        csv.AppendLine("send_us,send_to_239_us,transaction_us,outstanding_send,outstanding_239,send_calls,first_send_bytes,min_send_bytes,max_send_bytes,total_send_bytes,active_sends_at_start,awaiting_239_at_send");
        foreach (var sample in samples)
        {
            csv.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{sample.CommandAndArticleSendUs},{sample.SendTo239Us},{sample.TransactionUs},{sample.OutstandingAtSend},{sample.OutstandingAt239},{sample.SendCalls},{sample.FirstSendBytes},{sample.MinSendBytes},{sample.MaxSendBytes},{sample.TotalSendBytes},{sample.ActiveSendsAtStart},{sample.Awaiting239AtSendStart}"));
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
