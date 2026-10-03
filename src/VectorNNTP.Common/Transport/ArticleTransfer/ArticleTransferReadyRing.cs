namespace VectorNNTP.Common.Transport.ArticleTransfer
{
    /// <summary>
    /// Round-robin ready-ring for one-write-pump DATA scheduling fairness.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Intended writer semantics (socket pump not implemented in Phase 1):
    /// </para>
    /// <list type="bullet">
    /// <item>One write pump per connection.</item>
    /// <item>At most one DATA frame per stream per scheduling turn.</item>
    /// <item>
    /// Frame size = min(remaining article bytes, stream WINDOW credit, maxFramePayload).
    /// </item>
    /// <item>Zero-credit streams are skipped; other ready streams continue.</item>
    /// <item>One huge article cannot monopolize the socket while peers are ready.</item>
    /// </list>
    /// WINDOW for an unknown stream is a protocol error (<see cref="VatpErrorCode.UnknownStream"/>).
    /// </remarks>
    public sealed class ArticleTransferReadyRing
    {
        private readonly List<uint> _ring = [];
        private int _next;

        /// <summary>Gets the number of write-ready stream ids.</summary>
        public int Count => _ring.Count;

        /// <summary>Adds a stream to the ring tail when not already present.</summary>
        public void Enqueue(uint streamId)
        {
            if (streamId == VatpProtocol.ConnectionStreamId)
            {
                throw new ArgumentOutOfRangeException(nameof(streamId));
            }

            if (_ring.Contains(streamId))
            {
                return;
            }

            _ring.Add(streamId);
        }

        /// <summary>Removes a stream from the ring.</summary>
        public bool Remove(uint streamId)
        {
            var index = _ring.IndexOf(streamId);
            if (index < 0)
            {
                return false;
            }

            _ring.RemoveAt(index);
            if (_next > index)
            {
                _next--;
            }
            else if (_next >= _ring.Count)
            {
                _next = 0;
            }

            return true;
        }

        /// <summary>
        /// Takes the next ready stream id and advances the ring. Returns false when empty.
        /// </summary>
        public bool TryTakeNext(out uint streamId)
        {
            if (_ring.Count == 0)
            {
                streamId = 0;
                return false;
            }

            if (_next >= _ring.Count)
            {
                _next = 0;
            }

            streamId = _ring[_next];
            _next = (_next + 1) % _ring.Count;
            return true;
        }

        /// <summary>
        /// Computes the DATA payload length for one scheduling turn.
        /// </summary>
        public static int ComputeDataPayloadLength(int remainingBytes, long credit, uint maxFramePayload)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(remainingBytes);
            ArgumentOutOfRangeException.ThrowIfNegative(credit);
            if (maxFramePayload == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxFramePayload));
            }

            if (remainingBytes == 0 || credit == 0)
            {
                return 0;
            }

            var limited = remainingBytes;
            if (credit < limited)
            {
                limited = (int)credit;
            }

            if (maxFramePayload < (uint)limited)
            {
                limited = (int)maxFramePayload;
            }

            return limited;
        }

        /// <summary>Clears the ring.</summary>
        public void Clear()
        {
            _ring.Clear();
            _next = 0;
        }
    }
}
