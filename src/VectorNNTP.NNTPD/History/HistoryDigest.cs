using System.Buffers.Binary;
using Blake3;

namespace VectorNNTP.NNTPD.History;

/// <summary>32-byte BLAKE3 digest of a Message-ID's protocol bytes.</summary>
public readonly struct HistoryDigest : IEquatable<HistoryDigest>
{
    /// <summary>BLAKE3 output length in bytes.</summary>
    public const int Length = 32;

    private readonly ulong _w0;
    private readonly ulong _w1;
    private readonly ulong _w2;
    private readonly ulong _w3;

    private HistoryDigest(ulong w0, ulong w1, ulong w2, ulong w3)
    {
        _w0 = w0;
        _w1 = w1;
        _w2 = w2;
        _w3 = w3;
    }

    /// <summary>Computes the HistoryDB digest from Message-ID wire octets.</summary>
    public static HistoryDigest FromMessageId(ReadOnlySpan<byte> messageId)
    {
        var hash = Hasher.Hash(messageId);
        return FromSpan(hash.AsSpan());
    }

    /// <summary>Creates a digest from an existing 32-byte BLAKE3 output.</summary>
    public static HistoryDigest FromSpan(ReadOnlySpan<byte> digest)
    {
        if (digest.Length != Length)
        {
            throw new ArgumentException($"History digest must be {Length} bytes.", nameof(digest));
        }

        return new HistoryDigest(
            BinaryPrimitives.ReadUInt64LittleEndian(digest),
            BinaryPrimitives.ReadUInt64LittleEndian(digest[8..]),
            BinaryPrimitives.ReadUInt64LittleEndian(digest[16..]),
            BinaryPrimitives.ReadUInt64LittleEndian(digest[24..]));
    }

    /// <summary>Copies the digest bytes into <paramref name="destination"/>.</summary>
    public void CopyTo(Span<byte> destination)
    {
        if (destination.Length < Length)
        {
            throw new ArgumentException("Destination is shorter than the digest.", nameof(destination));
        }

        BinaryPrimitives.WriteUInt64LittleEndian(destination, _w0);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[8..], _w1);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[16..], _w2);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[24..], _w3);
    }

    /// <inheritdoc />
    public bool Equals(HistoryDigest other) =>
        _w0 == other._w0 && _w1 == other._w1 && _w2 == other._w2 && _w3 == other._w3;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is HistoryDigest other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(_w0, _w1, _w2, _w3);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(HistoryDigest left, HistoryDigest right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(HistoryDigest left, HistoryDigest right) => !left.Equals(right);
}
