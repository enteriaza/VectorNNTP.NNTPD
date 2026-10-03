namespace VectorNNTP.Common.Articles
{
    /// <summary>
    /// Integer offset/length into an <see cref="ArticleRecord"/> ArtData buffer.
    /// </summary>
    public readonly struct ArticleByteRange : IEquatable<ArticleByteRange>
    {
        /// <summary>Gets a range that does not refer to any ArtData bytes.</summary>
        internal static ArticleByteRange Absent { get; } = new(-1, 0);

        /// <summary>
        /// Initializes a range.
        /// </summary>
        /// <param name="offset">Byte offset into ArtData, or -1 when absent.</param>
        /// <param name="length">Byte length of the field value.</param>
        internal ArticleByteRange(int offset, int length)
        {
            Offset = offset;
            Length = length;
        }

        /// <summary>Gets the byte offset into ArtData, or -1 when the field is absent.</summary>
        internal int Offset { get; }

        /// <summary>Gets the value length in bytes.</summary>
        internal int Length { get; }

        /// <summary>Gets a value indicating whether this range refers to ArtData.</summary>
        public bool IsPresent => Offset >= 0;

        /// <summary>
        /// Slices <paramref name="artData"/> for this range.
        /// </summary>
        /// <param name="artData">Canonical article bytes that own this range.</param>
        /// <returns>The field bytes, or empty when the field is absent or zero-length.</returns>
        internal ReadOnlySpan<byte> Slice(ReadOnlySpan<byte> artData)
        {
            if (!IsPresent || Length == 0)
            {
                return [];
            }

            return artData.Slice(Offset, Length);
        }

        /// <inheritdoc />
        public bool Equals(ArticleByteRange other) => Offset == other.Offset && Length == other.Length;

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is ArticleByteRange other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode() => HashCode.Combine(Offset, Length);

        /// <summary>Equality operator.</summary>
        public static bool operator ==(ArticleByteRange left, ArticleByteRange right) => left.Equals(right);

        /// <summary>Inequality operator.</summary>
        public static bool operator !=(ArticleByteRange left, ArticleByteRange right) => !left.Equals(right);
    }
}
