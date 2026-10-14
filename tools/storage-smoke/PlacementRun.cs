using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTP.Common.Articles;
using VectorNNTP.NNTPD.Storage;

namespace VectorNNTP.StorageSmoke;

/// <summary>Runs one smoke or soak against a single explicit StorageServer target.</summary>
internal static class PlacementRun
{
    private const int FailureLogCap = 1000;

    internal static async Task<int> ExecuteAsync(SmokeCommand command, TextWriter stdout, TextWriter stderr)
    {
        if (ResultsLayout.EnsureFresh(command.ResultsDirectory, stderr) != 0)
        {
            return 2;
        }

        var body = PlacementArticle.CreateBody(command.BodyBytes);
        if (!PlacementArticle.TryCreate(body, "path-probe", out var probe, out var probeFailure)
            || !HasExactPreIngestionPath(probe))
        {
            stderr.WriteLine(
                probeFailure.Length == 0
                    ? "pre-ingestion Path was not news.usenet.ninja!peer.example. Refusing to place articles."
                    : $"article construction failed ({probeFailure}). Refusing to place articles.");
            return 2;
        }

        var started = DateTime.UtcNow;
        var deadline = command.Mode == SmokeMode.Soak ? started.AddSeconds(command.Seconds) : DateTime.MaxValue;
        var runId = Guid.NewGuid().ToString("n");
        var csvPath = Path.Combine(command.ResultsDirectory, ResultsLayout.ClientCsv);
        var runPath = Path.Combine(command.ResultsDirectory, ResultsLayout.RunMetadata);
        var stopPath = Path.Combine(command.ResultsDirectory, ResultsLayout.StopRequest);
        var failurePath = Path.Combine(command.ResultsDirectory, ResultsLayout.FailuresLog);
        var failures = new FailureLog();
        var stop = 0;
        var counters = new Counters();

        await File.WriteAllTextAsync(
            runPath,
            BuildPlan(command, runId, started, deadline),
            Encoding.UTF8).ConfigureAwait(false);
        await File.WriteAllTextAsync(csvPath, ResultsLayout.ClientCsvHeader + "\n", Encoding.UTF8).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            Path.Combine(command.ResultsDirectory, ResultsLayout.ClientPid),
            Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
            Encoding.UTF8).ConfigureAwait(false);

        var modeName = command.Mode == SmokeMode.Smoke ? "smoke" : "soak";
        stdout.WriteLine(
            $"{modeName} run={runId} host={command.Host} port={command.Port} sessions={command.Sessions} outstandingStores={command.OutstandingStores} bodyBytes={command.BodyBytes} preIngestionPath={SmokeLimits.PreIngestionPath} results={command.ResultsDirectory}");
        if (command.Mode == SmokeMode.Soak)
        {
            stdout.WriteLine($"soak seconds={command.Seconds} until={deadline:O}");
        }
        else
        {
            stdout.WriteLine($"smoke articles={command.Articles}");
        }

        stdout.WriteLine("accepted counts VATP queue admission, not durable spool publication or SQLite indexing.");
        await WriteSampleAsync(csvPath, counters).ConfigureAwait(false);

        var cancelCount = 0;
        void OnCancel(object? sender, ConsoleCancelEventArgs args)
        {
            if (Interlocked.Increment(ref cancelCount) == 1)
            {
                args.Cancel = true;
                Volatile.Write(ref stop, 1);
            }
        }

        Console.CancelKeyPress += OnCancel;
        using var samples = new CancellationTokenSource();
        var reporter = ReportAsync(csvPath, counters, samples.Token, () => Volatile.Read(ref stop) != 0 || File.Exists(stopPath));
        var reason = "completed";
        try
        {
            var workers = new Task[command.Sessions];
            for (var session = 0; session < command.Sessions; session++)
            {
                var sessionId = session;
                workers[session] = Task.Run(() => RunSessionAsync(
                    command,
                    sessionId,
                    runId,
                    body,
                    deadline,
                    stopPath,
                    () => Volatile.Read(ref stop) != 0,
                    counters,
                    failurePath,
                    failures));
            }

            await Task.WhenAll(workers).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            reason = "exception:" + ex.GetType().Name;
        }
        finally
        {
            await samples.CancelAsync().ConfigureAwait(false);
            try
            {
                await reporter.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            Console.CancelKeyPress -= OnCancel;
            await WriteSampleAsync(csvPath, counters).ConfigureAwait(false);
        }

        if (!reason.StartsWith("exception:", StringComparison.Ordinal))
        {
            if (Volatile.Read(ref stop) != 0)
            {
                reason = "cancelled";
            }
            else if (File.Exists(stopPath))
            {
                reason = "stop.request";
            }
            else if (command.Mode == SmokeMode.Smoke && Volatile.Read(ref counters.Offered) < command.Articles)
            {
                reason = "incomplete";
            }
            else if (command.Mode == SmokeMode.Soak && DateTime.UtcNow < deadline)
            {
                reason = "incomplete";
            }
        }

        var offered = Volatile.Read(ref counters.Offered);
        var accepted = Volatile.Read(ref counters.Accepted);
        var duplicate = Volatile.Read(ref counters.Duplicate);
        var rejected = Volatile.Read(ref counters.Rejected);
        var failed = Volatile.Read(ref counters.Failed);
        var ambiguous = Volatile.Read(ref counters.Ambiguous);
        var cancelled = Volatile.Read(ref counters.Cancelled);
        var acceptedBytes = Volatile.Read(ref counters.AcceptedBytes);
        var elapsed = DateTime.UtcNow - started;
        await File.AppendAllTextAsync(
            runPath,
            BuildOutcome(reason, offered, accepted, duplicate, rejected, failed, ambiguous, cancelled, acceptedBytes, elapsed),
            Encoding.UTF8).ConfigureAwait(false);

        stdout.WriteLine(
            $"done status={reason} offered={offered} vatpAccepted={accepted} duplicate={duplicate} rejected={rejected} failed={failed} ambiguous={ambiguous} cancelled={cancelled} acceptedBytes={acceptedBytes} elapsedSeconds={elapsed.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture)}");
        stdout.WriteLine($"client.csv is cumulative. vatpAccepted is not spool publication. file={csvPath}");

        if (!string.Equals(reason, "completed", StringComparison.Ordinal))
        {
            return 1;
        }

        if (command.Mode == SmokeMode.Smoke
            && (accepted != command.Articles || duplicate != 0 || rejected != 0 || failed != 0 || ambiguous != 0 || cancelled != 0))
        {
            return 1;
        }

        return 0;
    }

    private static async Task ReportAsync(
        string csvPath,
        Counters counters,
        CancellationToken cancellationToken,
        Func<bool> stop)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(SmokeLimits.SampleIntervalSeconds));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await WriteSampleAsync(csvPath, counters).ConfigureAwait(false);
                if (stop())
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task RunSessionAsync(
        SmokeCommand command,
        int sessionId,
        string runId,
        byte[] body,
        DateTime deadline,
        string stopPath,
        Func<bool> cancelled,
        Counters counters,
        string failurePath,
        FailureLog failures)
    {
        await using var client = new ArticlePlacementClient(
            NullLogger<ArticlePlacementClient>.Instance,
            command.OutstandingStores);
        var target = new StorageServerFleetEntry(1, command.Host, 1, 0, 1, DateTimeOffset.UtcNow, command.Port);
        var sequence = 0;
        while (!cancelled() && !File.Exists(stopPath) && DateTime.UtcNow < deadline)
        {
            var ticket = Interlocked.Increment(ref counters.Offered);
            if (command.Mode == SmokeMode.Smoke && ticket > command.Articles)
            {
                Interlocked.Decrement(ref counters.Offered);
                return;
            }

            var token = $"{(command.Mode == SmokeMode.Smoke ? "smoke" : "soak")}-{runId}-{sessionId.ToString(CultureInfo.InvariantCulture)}-{sequence.ToString(CultureInfo.InvariantCulture)}";
            sequence++;
            if (!PlacementArticle.TryCreate(body, token, out var record, out var failure))
            {
                Interlocked.Increment(ref counters.Failed);
                await NoteFailureAsync(failurePath, failures, sessionId, "construction", failure).ConfigureAwait(false);
                continue;
            }

            ArticlePlacementResult result;
            try
            {
                result = await client.PlaceAsync(record, target, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Interlocked.Increment(ref counters.Failed);
                await NoteFailureAsync(failurePath, failures, sessionId, "exception", ex.GetType().Name).ConfigureAwait(false);
                continue;
            }

            switch (result.Kind)
            {
                case ArticlePlacementKind.Accepted:
                    Interlocked.Increment(ref counters.Accepted);
                    Interlocked.Add(ref counters.AcceptedBytes, record.ArtSize);
                    break;
                case ArticlePlacementKind.Duplicate:
                    Interlocked.Increment(ref counters.Duplicate);
                    break;
                case ArticlePlacementKind.RejectedCapacity:
                case ArticlePlacementKind.RejectedPressure:
                case ArticlePlacementKind.RejectedInvalid:
                case ArticlePlacementKind.Conflict:
                    Interlocked.Increment(ref counters.Rejected);
                    await NoteFailureAsync(failurePath, failures, sessionId, result.Kind.ToString(), result.Failure).ConfigureAwait(false);
                    break;
                case ArticlePlacementKind.AcknowledgementNotObserved:
                    Interlocked.Increment(ref counters.Ambiguous);
                    await NoteFailureAsync(failurePath, failures, sessionId, result.Kind.ToString(), result.Failure).ConfigureAwait(false);
                    break;
                case ArticlePlacementKind.Cancelled:
                    Interlocked.Increment(ref counters.Cancelled);
                    break;
                default:
                    Interlocked.Increment(ref counters.Failed);
                    await NoteFailureAsync(failurePath, failures, sessionId, result.Kind.ToString(), result.Failure).ConfigureAwait(false);
                    break;
            }
        }
    }

    private static Task NoteFailureAsync(
        string failurePath,
        FailureLog failures,
        int sessionId,
        string kind,
        string? detail)
    {
        var line = string.Create(
            CultureInfo.InvariantCulture,
            $"{DateTime.UtcNow:O},{sessionId},{kind},{detail}\n");
        lock (failures.Gate)
        {
            if (failures.Lines >= FailureLogCap)
            {
                return Task.CompletedTask;
            }

            failures.Lines++;
            var truncated = failures.Lines == FailureLogCap;
            File.AppendAllText(failurePath, line, Encoding.UTF8);
            if (truncated)
            {
                File.AppendAllText(
                    failurePath,
                    "truncated after 1000 lines; client.csv counters continue\n",
                    Encoding.UTF8);
            }
        }

        return Task.CompletedTask;
    }

    private static async Task WriteSampleAsync(string csvPath, Counters counters)
    {
        var line = string.Create(
            CultureInfo.InvariantCulture,
            $"{DateTime.UtcNow:O},{Volatile.Read(ref counters.Offered)},{Volatile.Read(ref counters.Accepted)},{Volatile.Read(ref counters.Duplicate)},{Volatile.Read(ref counters.Rejected)},{Volatile.Read(ref counters.Failed)},{Volatile.Read(ref counters.Ambiguous)},{Volatile.Read(ref counters.Cancelled)},{Volatile.Read(ref counters.AcceptedBytes)}\n");
        await File.AppendAllTextAsync(csvPath, line, Encoding.UTF8).ConfigureAwait(false);
    }

    private static bool HasExactPreIngestionPath(ArticleRecord record)
    {
        var text = Encoding.ASCII.GetString(record.ArtData.Span);
        var headerEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        if (headerEnd < 0)
        {
            return false;
        }

        var header = text[..headerEnd];
        return header.Contains("Path: " + SmokeLimits.PreIngestionPath + "\r\n", StringComparison.Ordinal)
            && !header.Contains("nntpd01.usenet.ninja", StringComparison.Ordinal)
            && !header.Contains("cache01.usenet.ninja", StringComparison.Ordinal);
    }

    private static string BuildPlan(SmokeCommand command, string runId, DateTime started, DateTime deadline)
    {
        var until = command.Mode == SmokeMode.Soak ? deadline.ToString("O", CultureInfo.InvariantCulture) : string.Empty;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"""
            runId={runId}
            mode={(command.Mode == SmokeMode.Smoke ? "smoke" : "soak")}
            startedUtc={started:O}
            untilUtc={until}
            articles={(command.Mode == SmokeMode.Smoke ? command.Articles : 0)}
            seconds={(command.Mode == SmokeMode.Soak ? command.Seconds : 0)}
            sessions={command.Sessions}
            outstandingStoresPerSession={command.OutstandingStores}
            bodyBytes={command.BodyBytes}
            host={command.Host}
            port={command.Port}
            preIngestionPath={SmokeLimits.PreIngestionPath}
            sourcePath={SmokeLimits.SourcePath}
            vatpAcceptedMeans=in-memory queue admission, not durable spool publication and not SQLite indexing
            clientCsv=cumulative counters; timestamps UTC; interval rate is the difference of two rows divided by their timestamp gap
            sampleIntervalSeconds={SmokeLimits.SampleIntervalSeconds}

            """);
    }

    private static string BuildOutcome(
        string reason,
        int offered,
        int accepted,
        int duplicate,
        int rejected,
        int failed,
        int ambiguous,
        int cancelled,
        long acceptedBytes,
        TimeSpan elapsed)
    {
        var status = string.Equals(reason, "completed", StringComparison.Ordinal) ? "completed" : "incomplete";
        return string.Create(
            CultureInfo.InvariantCulture,
            $"""
            status={status}
            reason={reason}
            endedUtc={DateTime.UtcNow:O}
            elapsedSeconds={elapsed.TotalSeconds:F3}
            offered={offered}
            vatpAccepted={accepted}
            duplicate={duplicate}
            rejected={rejected}
            failed={failed}
            ambiguous={ambiguous}
            cancelled={cancelled}
            acceptedBytes={acceptedBytes}

            """);
    }

    private sealed class Counters
    {
        internal int Offered;
        internal int Accepted;
        internal int Duplicate;
        internal int Rejected;
        internal int Failed;
        internal int Ambiguous;
        internal int Cancelled;
        internal long AcceptedBytes;
    }

    private sealed class FailureLog
    {
        internal readonly object Gate = new();
        internal int Lines;
    }
}
