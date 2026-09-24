using System.Buffers;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Net.Sockets;

namespace VectorNNTP.StandaloneTxPoc;

internal sealed class WriteCounters
{
    public long Writes;
    public long Sync;
    public long Async;
}

internal static class TxModes
{
    public static PipeOptions CreateProductionTxPipeOptions() =>
        new(
            pool: null,
            readerScheduler: PipeScheduler.Inline,
            writerScheduler: PipeScheduler.Inline,
            pauseWriterThreshold: Constants.PauseWriterThreshold,
            resumeWriterThreshold: Constants.ResumeWriterThreshold,
            minimumSegmentSize: Constants.MinimumSegmentSize,
            useSynchronizationContext: false);

    public static async Task<RunResult> RunRawAsync(
        NetworkStream stream,
        ReadOnlyMemory<byte> chunk,
        long totalBytes,
        CancellationToken cancellationToken)
    {
        var counts = new WriteCounters();
        var sent = 0L;
        var started = Stopwatch.GetTimestamp();
        while (sent < totalBytes)
        {
            var remaining = totalBytes - sent;
            var slice = remaining >= chunk.Length ? chunk : chunk[..(int)remaining];
            await WriteCountedAsync(stream, slice, cancellationToken, counts).ConfigureAwait(false);
            sent += slice.Length;
        }

        return Finish(started, sent, counts);
    }

    public static async Task<RunResult> RunPipeAsync(
        NetworkStream stream,
        ReadOnlyMemory<byte> chunk,
        long totalBytes,
        CancellationToken cancellationToken)
    {
        var pipe = new Pipe(CreateProductionTxPipeOptions());
        var produce = ProduceAsync(pipe.Writer, chunk, totalBytes, cancellationToken);
        var consume = ConsumePipeAsync(pipe.Reader, stream, cancellationToken);
        var result = await consume.ConfigureAwait(false);
        await produce.ConfigureAwait(false);
        return result;
    }

    public static async Task<RunResult> RunProductionLoopAsync(
        NetworkStream stream,
        ReadOnlyMemory<byte> chunk,
        long totalBytes,
        CancellationToken cancellationToken)
    {
        var pipe = new Pipe(CreateProductionTxPipeOptions());
        var produce = ProduceAsync(pipe.Writer, chunk, totalBytes, cancellationToken);
        var consume = ProductionSendLoopAsync(pipe.Reader, stream, cancellationToken);
        var result = await consume.ConfigureAwait(false);
        await produce.ConfigureAwait(false);
        return result;
    }

    private static async Task ProduceAsync(
        PipeWriter writer,
        ReadOnlyMemory<byte> chunk,
        long totalBytes,
        CancellationToken cancellationToken)
    {
        var sent = 0L;
        while (sent < totalBytes)
        {
            var remaining = totalBytes - sent;
            var take = remaining >= chunk.Length ? chunk.Length : (int)remaining;
            var memory = writer.GetMemory(take);
            chunk.Span[..take].CopyTo(memory.Span);
            writer.Advance(take);
            sent += take;
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        await writer.CompleteAsync().ConfigureAwait(false);
    }

    private static async Task<RunResult> ConsumePipeAsync(
        PipeReader reader,
        NetworkStream stream,
        CancellationToken cancellationToken)
    {
        var counts = new WriteCounters();
        var sent = 0L;
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            var result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            foreach (var segment in result.Buffer)
            {
                if (segment.Length == 0)
                {
                    continue;
                }

                await WriteCountedAsync(stream, segment, cancellationToken, counts).ConfigureAwait(false);
                sent += segment.Length;
            }

            reader.AdvanceTo(result.Buffer.End);
            if (result.IsCompleted)
            {
                break;
            }
        }

        await reader.CompleteAsync().ConfigureAwait(false);
        return Finish(started, sent, counts);
    }

    /// <summary>
    /// Mirrors <c>NntpConnection.SendAsync</c> consume shape: ReadAsync, foreach segment,
    /// WriteAsync, AdvanceTo. No Channel, TCS, logging, or coalesce.
    /// </summary>
    private static async Task<RunResult> ProductionSendLoopAsync(
        PipeReader reader,
        NetworkStream stream,
        CancellationToken cancellationToken)
    {
        var counts = new WriteCounters();
        var sent = 0L;
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            ReadResult result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            ReadOnlySequence<byte> buffer = result.Buffer;
            foreach (ReadOnlyMemory<byte> segment in buffer)
            {
                if (segment.Length == 0)
                {
                    continue;
                }

                await WriteCountedAsync(stream, segment, cancellationToken, counts).ConfigureAwait(false);
                sent += segment.Length;
            }

            reader.AdvanceTo(buffer.End);
            if (result.IsCompleted)
            {
                break;
            }
        }

        await reader.CompleteAsync().ConfigureAwait(false);
        return Finish(started, sent, counts);
    }

    private static async ValueTask WriteCountedAsync(
        NetworkStream stream,
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken,
        WriteCounters counts)
    {
        counts.Writes++;
        var vt = stream.WriteAsync(buffer, cancellationToken);
        if (vt.IsCompletedSuccessfully)
        {
            counts.Sync++;
            await vt.ConfigureAwait(false);
            return;
        }

        counts.Async++;
        await vt.ConfigureAwait(false);
    }

    private static RunResult Finish(long started, long sent, WriteCounters counts)
    {
        var elapsed = Stopwatch.GetElapsedTime(started);
        var seconds = elapsed.TotalSeconds;
        var gbit = seconds > 0 ? sent * 8d / seconds / 1_000_000_000d : 0;
        var mbit = gbit * 1000d;
        var avg = counts.Writes > 0 ? sent / (double)counts.Writes : 0;
        return new RunResult(elapsed.TotalMilliseconds, gbit, mbit, counts.Writes, avg, counts.Sync, counts.Async, sent);
    }
}
