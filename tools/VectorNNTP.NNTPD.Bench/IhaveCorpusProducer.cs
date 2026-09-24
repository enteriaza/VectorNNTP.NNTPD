using System.IO.Pipelines;

namespace VectorNNTP.NNTPD.Bench;

/// <summary>
/// Simulated transport writer: corpus bytes are flushed to the input Pipe in fixed chunks.
/// </summary>
internal static class IhaveCorpusProducer
{
    public static PipeOptions PrebufferedOptions { get; } = new(
        pauseWriterThreshold: 8 * 1024 * 1024,
        resumeWriterThreshold: 2 * 1024 * 1024,
        useSynchronizationContext: false);

    public static PipeOptions StreamingOptions(int chunkBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(chunkBytes, 1);
        var pause = Math.Max(chunkBytes * 2, 2);
        return new PipeOptions(
            pauseWriterThreshold: pause,
            resumeWriterThreshold: Math.Max(1, chunkBytes),
            minimumSegmentSize: chunkBytes,
            useSynchronizationContext: false);
    }

    public static async Task WriteAllAsync(PipeWriter writer, ReadOnlyMemory<byte> payload)
    {
        ArgumentNullException.ThrowIfNull(writer);
        await writer.WriteAsync(payload).ConfigureAwait(false);
        await writer.FlushAsync().ConfigureAwait(false);
    }

    public static async Task WriteChunkedAsync(PipeWriter writer, ReadOnlyMemory<byte> payload, int chunkBytes)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentOutOfRangeException.ThrowIfLessThan(chunkBytes, 1);
        var offset = 0;
        while (offset < payload.Length)
        {
            var n = Math.Min(chunkBytes, payload.Length - offset);
            await writer.WriteAsync(payload.Slice(offset, n)).ConfigureAwait(false);
            await writer.FlushAsync().ConfigureAwait(false);
            offset += n;
        }
    }
}
