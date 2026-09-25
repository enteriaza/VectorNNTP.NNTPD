using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipelines;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VectorNNTP.NNTPD.Configuration;
using VectorNNTP.NNTPD.History;
using VectorNNTP.NNTPD.Session;

namespace VectorNNTP.NNTPD.Bench;

/// <summary>
/// CHECK session measure using the frozen production pipeline (depth 16) and the
/// validation-bench iteration counts.
/// </summary>
internal static class CheckSessionMeasure
{
    private static readonly byte[] CheckPrefix = "CHECK "u8.ToArray();
    private static readonly byte[] CrLf = "\r\n"u8.ToArray();
    private static readonly byte[] Quit = "QUIT\r\n"u8.ToArray();

    /// <summary>Production <c>CheckPipeline.Depth</c>.</summary>
    public const int ProductionDepth = 16;

    /// <summary>Validation bench command count when Redis delay is 0 ms.</summary>
    public const int ZeroDelayIterations = 2_000;

    /// <summary>Validation bench command count when Redis delay is 1, 2, or 5 ms.</summary>
    public const int DelayedIterations = 200;

    public static async Task<IReadOnlyList<CheckMeasureRow>> RunAllAsync()
    {
        var rows = new List<CheckMeasureRow>();
        await AddAsync(rows, "redis-hit", delayMs: 0, ZeroDelayIterations, localHit: 0, redisHit: 1, fail: false)
            .ConfigureAwait(false);
        await AddAsync(rows, "redis-hit", delayMs: 1, DelayedIterations, localHit: 0, redisHit: 1, fail: false)
            .ConfigureAwait(false);
        await AddAsync(rows, "redis-hit", delayMs: 2, DelayedIterations, localHit: 0, redisHit: 1, fail: false)
            .ConfigureAwait(false);
        await AddAsync(rows, "redis-hit", delayMs: 5, DelayedIterations, localHit: 0, redisHit: 1, fail: false)
            .ConfigureAwait(false);
        await AddAsync(rows, "redis-miss", delayMs: 1, DelayedIterations, localHit: 0, redisHit: 0, fail: false)
            .ConfigureAwait(false);
        await AddAsync(rows, "redis-miss", delayMs: 2, DelayedIterations, localHit: 0, redisHit: 0, fail: false)
            .ConfigureAwait(false);
        await AddAsync(rows, "redis-miss", delayMs: 5, DelayedIterations, localHit: 0, redisHit: 0, fail: false)
            .ConfigureAwait(false);
        await AddAsync(rows, "local-hit", delayMs: 0, ZeroDelayIterations, localHit: 1, redisHit: 0, fail: false)
            .ConfigureAwait(false);
        await AddAsync(rows, "local-hit", delayMs: 1, DelayedIterations, localHit: 1, redisHit: 0, fail: false)
            .ConfigureAwait(false);
        await AddAsync(rows, "local-hit", delayMs: 2, DelayedIterations, localHit: 1, redisHit: 0, fail: false)
            .ConfigureAwait(false);
        await AddAsync(rows, "local-hit", delayMs: 5, DelayedIterations, localHit: 1, redisHit: 0, fail: false)
            .ConfigureAwait(false);
        await AddAsync(rows, "cooldown", delayMs: 0, ZeroDelayIterations, localHit: 0, redisHit: 0, fail: true)
            .ConfigureAwait(false);
        return rows;
    }

    private static async Task AddAsync(
        List<CheckMeasureRow> rows,
        string workload,
        int delayMs,
        int iterations,
        double localHit,
        double redisHit,
        bool fail)
    {
        var row = await MeasureAsync(workload, delayMs, iterations, localHit, redisHit, fail).ConfigureAwait(false);
        rows.Add(row);
        Console.WriteLine(
            $"CHECK {workload} delay={delayMs}ms depth={ProductionDepth} checks={iterations}: " +
            $"{row.ChecksPerSecond:F0} CHECK/s, elapsed={row.Elapsed.TotalMilliseconds:F1} ms, " +
            $"peak-inflight={row.PeakInFlight}, exists={row.Exists}, {row.Responses}, ordered=True");
    }

    private static async Task<CheckMeasureRow> MeasureAsync(
        string workload,
        int delayMs,
        int iterations,
        double localHitFraction,
        double redisHitFraction,
        bool fail)
    {
        var redis = new CheckDelayedRedis { Delay = TimeSpan.FromMilliseconds(delayMs), FailExists = fail };
        var history = new HistoryDb(
            redis,
            Options.Create(new NntpdOptions { HistoryTime = TimeSpan.FromHours(2) }),
            NullLogger<HistoryDb>.Instance);
        var ids = new byte[iterations][];
        for (var i = 0; i < iterations; i++)
        {
            ids[i] = System.Text.Encoding.ASCII.GetBytes(
                string.Create(CultureInfo.InvariantCulture, $"<p-{workload}-16-{delayMs}-{i}@example.com>"));
            var unit = i / (double)iterations;
            if (unit < localHitFraction)
            {
                _ = await history.LookupAsync(ids[i]).ConfigureAwait(false);
            }
            else if (unit < localHitFraction + redisHitFraction)
            {
                redis.Seed(HistoryDigest.FromMessageId(ids[i]));
            }
        }

        redis.ExistsCount = 0;
        var input = new Pipe();
        var output = new Pipe();
        var connection = new CheckPipeConnection(input.Reader, output.Writer);
        var session = new NntpSession(
            connection,
            NullLogger<NntpSession>.Instance,
            historyDb: history);
        session.SetAuthorization(NntpAuthorization.TrustedTransitPeer);
        var run = session.RunAsync();
        _ = await ReadLineAsync(output.Reader).ConfigureAwait(false);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var started = Stopwatch.GetTimestamp();
        for (var i = 0; i < iterations; i++)
        {
            await input.Writer.WriteAsync(CheckPrefix).ConfigureAwait(false);
            await input.Writer.WriteAsync(ids[i]).ConfigureAwait(false);
            await input.Writer.WriteAsync(CrLf).ConfigureAwait(false);
        }

        await input.Writer.FlushAsync().ConfigureAwait(false);

        var c238 = 0;
        var c431 = 0;
        var c438 = 0;
        for (var i = 0; i < iterations; i++)
        {
            var line = await ReadLineAsync(output.Reader).ConfigureAwait(false);
            var expectedId = System.Text.Encoding.ASCII.GetString(ids[i]);
            if (!LineHasMessageId(line, expectedId))
            {
                throw new InvalidOperationException(
                    $"CHECK responses were not in send order at index {i}: expected {expectedId}, got '{line}'.");
            }

            if (line.StartsWith("238 ", StringComparison.Ordinal))
            {
                c238++;
            }
            else if (line.StartsWith("431 ", StringComparison.Ordinal))
            {
                c431++;
            }
            else if (line.StartsWith("438 ", StringComparison.Ordinal))
            {
                c438++;
            }
            else
            {
                throw new InvalidOperationException($"Unexpected CHECK response '{line}'.");
            }
        }

        var elapsed = Stopwatch.GetElapsedTime(started);
        var peak = ReadPeakOccupied(session);
        await input.Writer.WriteAsync(Quit).ConfigureAwait(false);
        await input.Writer.FlushAsync().ConfigureAwait(false);
        _ = await ReadLineAsync(output.Reader).ConfigureAwait(false);
        await input.Writer.CompleteAsync().ConfigureAwait(false);
        await run.ConfigureAwait(false);
        await connection.DisposeAsync().ConfigureAwait(false);

        if (peak > ProductionDepth)
        {
            throw new InvalidOperationException($"Peak in-flight {peak} exceeded CHECK depth {ProductionDepth}.");
        }

        var responses = FormatResponses(c238, c431, c438);
        return new CheckMeasureRow(
            workload,
            delayMs,
            iterations,
            elapsed,
            iterations / elapsed.TotalSeconds,
            peak,
            redis.ExistsCount,
            responses,
            c238,
            c431,
            c438);
    }

    private static string FormatResponses(int c238, int c431, int c438)
    {
        if (c438 > 0 && c238 == 0 && c431 == 0)
        {
            return $"438×{c438}";
        }

        if (c238 > 0 && c438 == 0 && c431 == 0)
        {
            return $"238×{c238}";
        }

        if (c431 > 0 && c238 == 0 && c438 == 0)
        {
            return $"431×{c431}";
        }

        return $"238×{c238} 431×{c431} 438×{c438}";
    }

    private static bool LineHasMessageId(string line, string messageId)
    {
        if (line.Length < 4 || line[3] != ' ')
        {
            return false;
        }

        var rest = line.AsSpan(4);
        if (!rest.StartsWith(messageId, StringComparison.Ordinal))
        {
            return false;
        }

        return rest.Length == messageId.Length || rest[messageId.Length] == ' ';
    }

    private static int ReadPeakOccupied(NntpSession session)
    {
        var pipeline = typeof(NntpSession)
            .GetProperty("Pipeline", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(session);
        if (pipeline is null)
        {
            return 0;
        }

        var peak = pipeline.GetType().GetProperty("PeakOccupied")?.GetValue(pipeline);
        return peak is int value ? value : 0;
    }

    private static async Task<string> ReadLineAsync(PipeReader reader)
    {
        while (true)
        {
            var result = await reader.ReadAsync().ConfigureAwait(false);
            var buffer = result.Buffer;
            if (TryReadLine(ref buffer, out var line))
            {
                reader.AdvanceTo(buffer.Start, buffer.Start);
                return line;
            }

            reader.AdvanceTo(buffer.Start, buffer.End);
            if (result.IsCompleted)
            {
                throw new EndOfStreamException();
            }
        }
    }

    private static bool TryReadLine(ref ReadOnlySequence<byte> buffer, out string line)
    {
        line = string.Empty;
        var span = buffer.IsSingleSegment ? buffer.FirstSpan : buffer.ToArray();
        var cr = span.IndexOf((byte)'\r');
        if (cr < 0 || cr + 1 >= span.Length || span[cr + 1] != (byte)'\n')
        {
            return false;
        }

        line = System.Text.Encoding.ASCII.GetString(span[..cr]);
        buffer = buffer.Slice(cr + 2);
        return true;
    }
}

internal readonly record struct CheckMeasureRow(
    string Workload,
    int DelayMs,
    int Checks,
    TimeSpan Elapsed,
    double ChecksPerSecond,
    int PeakInFlight,
    int Exists,
    string Responses,
    int Count238,
    int Count431,
    int Count438);
