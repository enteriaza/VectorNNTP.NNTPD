using System.Buffers.Binary;
using Blake3;

namespace VectorNNTP.Common.Articles;

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
    public const int Length = 32;

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
    public static ArticleId FromMessageId(ReadOnlySpan<byte> messageIdValue)
    {
        var hash = Hasher.Hash(messageIdValue);
        return FromSpan(hash.AsSpan());
    }

    /// <summary>Creates an identity from an existing 32-byte BLAKE3 output.</summary>
    /// <param name="digest">Exactly <see cref="Length"/> digest bytes.</param>
    /// <returns>The identity value.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="digest"/> is not 32 bytes.</exception>
    public static ArticleId FromSpan(ReadOnlySpan<byte> digest)
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

    /// <summary>Copies the digest bytes into <paramref name="destination"/>.</summary>
    /// <param name="destination">Destination that must be at least <see cref="Length"/> bytes.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="destination"/> is too short.</exception>
    public void CopyTo(Span<byte> destination)
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
}
