using System.Buffers.Binary;
using Blake3;

namespace VectorNNTP.Common.Articles
{
    /// <summary>
    /// Compact internal Vector article identity: 32-byte BLAKE3 of Message-ID header value bytes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Input is the exact Message-ID header <c>value</c> octets, including <c>&lt;</c> and <c>&gt;</c>.
    /// The <c>Message-ID:</c> field name is not hashed. Bytes are not trimmed, case-folded, or
    /// converted to a string. This does not replace the Message-ID stored in ArtData / overview.
    /// </para>
    /// <para>
    /// The 32-byte digest is the canonical Vector article identity. NNTPD
    /// <c>HistoryDigest</c> stores these same bytes; it does not hash independently.
    /// </para>
    /// </remarks>
    public readonly struct ArticleId : IEquatable<ArticleId>
    {
        /// <summary>BLAKE3 output length in bytes.</summary>
        internal const int Length = 32;

        private readonly ulong _w0;
        private readonly ulong _w1;
        private readonly ulong _w2;
        private readonly ulong _w3;

        private ArticleId(ulong w0, ulong w1, ulong w2, ulong w3)
        {
            _w0 = w0;
            _w1 = w1;
            _w2 = w2;
            _w3 = w3;
        }

        /// <summary>Computes ArtId from exact Message-ID header value bytes.</summary>
        /// <param name="messageIdValue">Message-ID value octets only, including angle brackets. Not the field name.</param>
        /// <returns>32-byte BLAKE3 digest as a value type.</returns>
        internal static ArticleId FromMessageId(ReadOnlySpan<byte> messageIdValue)
        {
            var hash = Hasher.Hash(messageIdValue);
            return FromSpan(hash.AsSpan());
        }

        /// <summary>Creates an identity from an existing 32-byte BLAKE3 output.</summary>
        /// <param name="digest">Exactly <see cref="Length"/> digest bytes.</param>
        /// <returns>The identity value.</returns>
        /// <exception cref="ArgumentException">Thrown when <paramref name="digest"/> is not 32 bytes.</exception>
        internal static ArticleId FromSpan(ReadOnlySpan<byte> digest)
        {
            if (digest.Length != Length)
            {
                throw new ArgumentException($"Article identity must be {Length} bytes.", nameof(digest));
            }

            return new ArticleId(
                BinaryPrimitives.ReadUInt64LittleEndian(digest),
                BinaryPrimitives.ReadUInt64LittleEndian(digest[8..]),
                BinaryPrimitives.ReadUInt64LittleEndian(digest[16..]),
                BinaryPrimitives.ReadUInt64LittleEndian(digest[24..]));
        }

        /// <summary>Lowercase hexadecimal encoding length (<see cref="Length"/> × 2).</summary>
        internal const int HexLength = Length * 2;

        /// <summary>
        /// Formats the digest as 64 lowercase hexadecimal characters (no separators).
        /// Used by RabbitMQ ArticleWork Success <c>articleId</c>.
        /// </summary>
        internal string ToLowerHexString()
        {
            Span<byte> digest = stackalloc byte[Length];
            CopyTo(digest);
            return Convert.ToHexString(digest).ToLowerInvariant();
        }

        /// <summary>
        /// Attempts to parse a 64-character lowercase hexadecimal digest into an <see cref="ArticleId"/>.
        /// </summary>
        /// <param name="hex">Exactly <see cref="HexLength"/> lowercase hex digits.</param>
        /// <param name="articleId">Parsed identity when the method returns <see langword="true"/>.</param>
        /// <returns><see langword="true"/> when <paramref name="hex"/> is valid lowercase hex of length 64.</returns>
        internal static bool TryParseLowerHex(ReadOnlySpan<char> hex, out ArticleId articleId)
        {
            articleId = default;
            if (hex.Length != HexLength)
            {
                return false;
            }

            for (var i = 0; i < hex.Length; i++)
            {
                var c = hex[i];
                if (c is (>= '0' and <= '9') or (>= 'a' and <= 'f'))
                {
                    continue;
                }

                return false;
            }

            Span<byte> digest = stackalloc byte[Length];
            for (var i = 0; i < Length; i++)
            {
                var hi = HexValue(hex[i * 2]);
                var lo = HexValue(hex[(i * 2) + 1]);
                if (hi < 0 || lo < 0)
                {
                    return false;
                }

                digest[i] = (byte)((hi << 4) | lo);
            }

            articleId = FromSpan(digest);
            return true;
        }

        /// <summary>Parses a 64-character lowercase hexadecimal digest.</summary>
        /// <exception cref="FormatException">Thrown when the input is not valid lowercase hex of length 64.</exception>
        internal static ArticleId ParseLowerHex(ReadOnlySpan<char> hex)
        {
            if (!TryParseLowerHex(hex, out var articleId))
            {
                throw new FormatException($"ArticleId hex must be exactly {HexLength} lowercase hexadecimal characters.");
            }

            return articleId;
        }

        /// <summary>Copies the 32-byte digest into <paramref name="destination"/>.</summary>
        internal void CopyTo(Span<byte> destination)
        {
            if (destination.Length < Length)
            {
                throw new ArgumentException("Destination is shorter than the article identity.", nameof(destination));
            }

            BinaryPrimitives.WriteUInt64LittleEndian(destination, _w0);
            BinaryPrimitives.WriteUInt64LittleEndian(destination[8..], _w1);
            BinaryPrimitives.WriteUInt64LittleEndian(destination[16..], _w2);
            BinaryPrimitives.WriteUInt64LittleEndian(destination[24..], _w3);
        }

        /// <inheritdoc />
        public bool Equals(ArticleId other) =>
            _w0 == other._w0 && _w1 == other._w1 && _w2 == other._w2 && _w3 == other._w3;

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is ArticleId other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode() => HashCode.Combine(_w0, _w1, _w2, _w3);

        /// <summary>Equality operator.</summary>
        public static bool operator ==(ArticleId left, ArticleId right) => left.Equals(right);

        /// <summary>Inequality operator.</summary>
        public static bool operator !=(ArticleId left, ArticleId right) => !left.Equals(right);

        private static int HexValue(char c) =>
            c switch
            {
                >= '0' and <= '9' => c - '0',
                >= 'a' and <= 'f' => c - 'a' + 10,
                _ => -1,
            };
    }
}
