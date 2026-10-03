namespace VectorNNTP.Common.Transport.ArticleTransfer
{
    /// <summary>
    /// Per-stream WINDOW credit accounting.
    /// </summary>
    /// <remarks>
    /// Credit cannot underflow. Adds saturate at a configured maximum.
    /// Zero credit pauses only that stream; other streams remain eligible.
    /// </remarks>
    internal struct ArticleTransferWindow
    {
        private long _credit;
        private readonly long _maxCredit;

        /// <summary>Initializes credit to <paramref name="initialCredit"/> capped by <paramref name="maxCredit"/>.</summary>
        internal ArticleTransferWindow(long initialCredit, long maxCredit)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(initialCredit);
            ArgumentOutOfRangeException.ThrowIfLessThan(maxCredit, 1);
            if (initialCredit > maxCredit)
            {
                throw new ArgumentOutOfRangeException(nameof(initialCredit));
            }

            _credit = initialCredit;
            _maxCredit = maxCredit;
        }

        /// <summary>Gets the current available credit in bytes.</summary>
        internal readonly long Credit => _credit;

        /// <summary>Gets a value indicating whether any credit remains.</summary>
        internal readonly bool HasCredit => _credit > 0;

        /// <summary>
        /// Attempts to consume <paramref name="bytes"/> of credit.
        /// </summary>
        /// <returns><see langword="false"/> when insufficient credit (credit unchanged).</returns>
        internal bool TryConsume(long bytes)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(bytes);
            if (bytes > _credit)
            {
                return false;
            }

            _credit -= bytes;
            return true;
        }

        /// <summary>
        /// Adds WINDOW credit, saturating at the configured maximum.
        /// </summary>
        /// <returns>The actual amount added (may be less than <paramref name="bytes"/> when saturated).</returns>
        internal long Add(long bytes)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(bytes);
            if (bytes == 0)
            {
                return 0;
            }

            var room = _maxCredit - _credit;
            if (room <= 0)
            {
                return 0;
            }

            var add = bytes > room ? room : bytes;
            _credit += add;
            return add;
        }
    }
}
