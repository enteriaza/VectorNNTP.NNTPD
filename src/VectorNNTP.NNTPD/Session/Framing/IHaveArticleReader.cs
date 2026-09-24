using System.Buffers;
using System.Diagnostics;
using System.IO.Pipelines;

namespace VectorNNTP.NNTPD.Session.Framing;

/// <summary>
/// IHAVE article receive: locate <c>CRLF . CRLF</c> and copy stuffed wire octets into one owned buffer.
/// </summary>
/// <remarks>
/// IHAVE is not pipelined (RFC 3977 §6.3.2). This reader does not destuff, classify, or build
/// <see cref="VectorNNTP.NNTPD.ArticleIngestion.Article"/>. The queued payload is the exact
/// received article bytes with leading-dot stuffing preserved and the terminator omitted.
/// Pipe sequences are not retained after <c>AdvanceTo</c>. Interpretation happens downstream
/// in <see cref="VectorNNTP.NNTPD.ArticleIngestion.IhaveArticleInterpreter"/>.
/// </remarks>
public static class IHaveArticleReader
{
    /// <summary>Initial owned-buffer capacity when the incoming article size is unknown.</summary>
    public const int InitialCapacity = 64 * 1024;

    /// <summary>
    /// Reads one IHAVE article from <paramref name="reader"/> as owned NNTP wire bytes.
    /// </summary>
    public static async ValueTask<IHaveArticleReadResult> ReadAsync(
        PipeReader reader,
        int maxArticleBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxArticleBytes, 1);
        var session = new ReadSession(maxArticleBytes);
        return await session.RunAsync(reader, cancellationToken).ConfigureAwait(false);
    }

    private sealed class ReadSession(int maxArticleBytes)
    {
        private readonly OwnedWireBuffer _article = new(InitialCapacity);
        private int _pipeReads;
        private bool _exceeded;

        public async ValueTask<IHaveArticleReadResult> RunAsync(
            PipeReader reader,
            CancellationToken cancellationToken)
        {
            var started = Stopwatch.GetTimestamp();
            while (true)
            {
                var result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                _pipeReads++;
                var unread = result.Buffer;
                var examined = unread.End;
                var atStart = _article.Written == 0;

                if (NntpDelimiterSearch.TryFindArticleTerminator(
                        unread,
                        atStart,
                        out var payloadBytes,
                        out var consumedBytes))
                {
                    if (payloadBytes > 0)
                    {
                        _article.Append(unread.Slice(0, payloadBytes), maxArticleBytes, ref _exceeded);
                    }

                    unread = unread.Slice(consumedBytes);
                    reader.AdvanceTo(unread.Start, unread.Start);
                    return Finish(
                        _exceeded ? NntpMultilineReadStatus.TooLarge : NntpMultilineReadStatus.Completed,
                        started);
                }

                var hold = (int)Math.Min(NntpDelimiterSearch.ArticleTerminatorLookbehind, unread.Length);
                var copyBytes = (int)unread.Length - hold;
                if (copyBytes > 0)
                {
                    _article.Append(unread.Slice(0, copyBytes), maxArticleBytes, ref _exceeded);
                    unread = unread.Slice(copyBytes);
                }

                reader.AdvanceTo(unread.Start, examined);
                if (result.IsCompleted)
                {
                    return Finish(NntpMultilineReadStatus.Incomplete, started);
                }
            }
        }

        private IHaveArticleReadResult Finish(NntpMultilineReadStatus status, long started)
        {
            var elapsed = Stopwatch.GetElapsedTime(started);
            if (status != NntpMultilineReadStatus.Completed)
            {
                return new IHaveArticleReadResult(
                    status,
                    ReadOnlyMemory<byte>.Empty,
                    new IHaveReceiveMetrics(_pipeReads, 0, elapsed));
            }

            var payload = _article.Take();
            return new IHaveArticleReadResult(
                status,
                payload,
                new IHaveReceiveMetrics(_pipeReads, payload.Length, elapsed));
        }
    }

    private sealed class OwnedWireBuffer
    {
        private byte[] _buffer;
        private int _written;

        public OwnedWireBuffer(int initialCapacity)
        {
            _buffer = new byte[initialCapacity];
        }

        public int Written => _written;

        public void Append(ReadOnlySequence<byte> bytes, int maxArticleBytes, ref bool exceeded)
        {
            if (exceeded || bytes.IsEmpty)
            {
                return;
            }

            var needed = checked((int)bytes.Length);
            if (_written + needed > maxArticleBytes)
            {
                exceeded = true;
                return;
            }

            Ensure(_written + needed);
            bytes.CopyTo(_buffer.AsSpan(_written, needed));
            _written += needed;
        }

        public ReadOnlyMemory<byte> Take()
        {
            var buffer = _buffer;
            var length = _written;
            _buffer = [];
            _written = 0;
            return buffer.AsMemory(0, length);
        }

        private void Ensure(int needed)
        {
            if (_buffer.Length >= needed)
            {
                return;
            }

            var next = _buffer.Length < 256 ? 256 : _buffer.Length * 2;
            if (next < needed)
            {
                next = needed;
            }

            var grown = new byte[next];
            if (_written > 0)
            {
                _buffer.AsSpan(0, _written).CopyTo(grown);
            }

            _buffer = grown;
        }
    }
}
