using System.Buffers.Binary;
using System.Security.Cryptography;

namespace VectorNNTP.TcpSmoke.Protocol;

/// <summary>Version byte for this prototype. Peers reject any other value.</summary>
public static class ArticleProtocol
{
    /// <summary>The only version this tool speaks.</summary>
    public const byte Version = 1;

    /// <summary>Fixed header length. The payload, when present, follows immediately.</summary>
    public const int HeaderLength = 56;

    /// <summary>Largest payload a peer may declare. Larger declarations close the connection.</summary>
    public const int DefaultMaxArticleBytes = 8 * 1024 * 1024;

    /// <summary>SHA-256 of an empty payload.</summary>
    public static ReadOnlySpan<byte> EmptySha256 =>
    [
        0xE3, 0xB0, 0xC4, 0x42, 0x98, 0xFC, 0x1C, 0x14,
        0x9A, 0xFB, 0xF4, 0xC8, 0x99, 0x6F, 0xB9, 0x24,
        0x27, 0xAE, 0x41, 0xE4, 0x64, 0x9B, 0x93, 0x4C,
        0xA4, 0x95, 0x99, 0x1B, 0x78, 0x52, 0xB8, 0x55,
    ];
}

/// <summary>The three messages this prototype uses.</summary>
public enum ArticleOperation : byte
{
    /// <summary>Header followed by exactly <c>PayloadLength</c> article bytes.</summary>
    Put = 1,

    /// <summary>Request for a published article. The request payload length is zero. The response uses this same operation and is followed by the article bytes.</summary>
    Get = 2,

    /// <summary>Completion of a PUT. Payload length is zero. TCP has already delivered the bytes; this status is the receiver's contract.</summary>
    Result = 3,
}

/// <summary>Status in a header. Requests use <see cref="Ok"/>.</summary>
public enum ArticleStatus : ushort
{
    /// <summary>The receiver accepted the transfer under its configured contract.</summary>
    Ok = 0,

    /// <summary>GET named an article this process does not have.</summary>
    NotFound = 1,

    /// <summary>The declared length exceeds the configured maximum. The connection is closed because the stream can no longer be framed.</summary>
    TooLarge = 2,

    /// <summary>The version byte is not <see cref="ArticleProtocol.Version"/>.</summary>
    BadVersion = 3,

    /// <summary>The operation or the header shape is not valid for this prototype.</summary>
    BadOperation = 4,

    /// <summary>The payload SHA-256 did not match the header.</summary>
    HashMismatch = 5,

    /// <summary>A file for this article id already exists. It was not overwritten.</summary>
    Exists = 6,

    /// <summary>The server is at its connection limit. The connection is closed.</summary>
    Busy = 7,

    /// <summary>The peer closed the stream before a complete header and payload arrived.</summary>
    Incomplete = 8,

    /// <summary>A local read or write failed.</summary>
    IoError = 9,
}

/// <summary>One 56-byte header. Multi-byte integers are big-endian on the wire.</summary>
public readonly struct ArticleHeader
{
    /// <summary>Creates a header. <paramref name="sha256"/> must be 32 bytes.</summary>
    public ArticleHeader(
        ArticleOperation operation,
        ArticleStatus status,
        uint payloadLength,
        ulong articleId,
        ulong transferId,
        ReadOnlySpan<byte> sha256)
    {
        if (sha256.Length != 32)
        {
            throw new ArgumentException("SHA-256 must be 32 bytes.", nameof(sha256));
        }

        Operation = operation;
        Status = status;
        PayloadLength = payloadLength;
        ArticleId = articleId;
        TransferId = transferId;
        Sha256 = sha256.ToArray();
    }

    /// <summary>Always <see cref="ArticleProtocol.Version"/> for headers this process writes.</summary>
    public byte Version => ArticleProtocol.Version;

    /// <summary>PUT, GET, or RESULT.</summary>
    public ArticleOperation Operation { get; }

    /// <summary>Request headers carry <see cref="ArticleStatus.Ok"/>.</summary>
    public ArticleStatus Status { get; }

    /// <summary>Article bytes that follow this header. Zero for GET requests and for RESULT.</summary>
    public uint PayloadLength { get; }

    /// <summary>Caller-chosen article identity. File mode uses the lowercase hexadecimal form as the file name.</summary>
    public ulong ArticleId { get; }

    /// <summary>Caller-chosen id for this attempt. The server echoes it. It is not a connection id.</summary>
    public ulong TransferId { get; }

    /// <summary>SHA-256 of the payload. GET requests send 32 zero bytes. RESULT echoes the hash the receiver calculated, or zeros on failure.</summary>
    public byte[] Sha256 { get; }

    /// <summary>Writes the 56-byte header in network byte order.</summary>
    public void Write(Span<byte> destination)
    {
        if (destination.Length < ArticleProtocol.HeaderLength)
        {
            throw new ArgumentException("Header destination is shorter than 56 bytes.", nameof(destination));
        }

        destination[0] = ArticleProtocol.Version;
        destination[1] = (byte)Operation;
        BinaryPrimitives.WriteUInt16BigEndian(destination[2..], (ushort)Status);
        BinaryPrimitives.WriteUInt32BigEndian(destination[4..], PayloadLength);
        BinaryPrimitives.WriteUInt64BigEndian(destination[8..], ArticleId);
        BinaryPrimitives.WriteUInt64BigEndian(destination[16..], TransferId);
        Sha256.CopyTo(destination[24..]);
    }

    /// <summary>Reads a 56-byte header. Returns false when the version, operation, or shape is not valid.</summary>
    public static bool TryRead(ReadOnlySpan<byte> source, out ArticleHeader header, out ArticleStatus error)
    {
        header = default;
        error = ArticleStatus.Ok;
        if (source.Length < ArticleProtocol.HeaderLength)
        {
            error = ArticleStatus.Incomplete;
            return false;
        }

        if (source[0] != ArticleProtocol.Version)
        {
            error = ArticleStatus.BadVersion;
            return false;
        }

        var operationByte = source[1];
        if (operationByte is < (byte)ArticleOperation.Put or > (byte)ArticleOperation.Result)
        {
            error = ArticleStatus.BadOperation;
            return false;
        }

        var operation = (ArticleOperation)operationByte;
        var status = (ArticleStatus)BinaryPrimitives.ReadUInt16BigEndian(source[2..]);
        var length = BinaryPrimitives.ReadUInt32BigEndian(source[4..]);
        if (operation == ArticleOperation.Result && length != 0)
        {
            error = ArticleStatus.BadOperation;
            return false;
        }

        if (operation == ArticleOperation.Get && status == ArticleStatus.Ok && length != 0 && source[24..56].ContainsAnyExcept((byte)0) == false)
        {
            error = ArticleStatus.BadOperation;
            return false;
        }

        header = new ArticleHeader(
            operation,
            status,
            length,
            BinaryPrimitives.ReadUInt64BigEndian(source[8..]),
            BinaryPrimitives.ReadUInt64BigEndian(source[16..]),
            source[24..56]);
        return true;
    }
}
