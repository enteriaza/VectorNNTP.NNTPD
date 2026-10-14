using System.Buffers.Binary;
using System.Text;

namespace VectorNNTP.TftpSmoke.Protocol;

/// <summary>RFC 1350 opcodes. Values are the numeric codes carried in network byte order.</summary>
public static class TftpOpcode
{
    public const ushort ReadRequest = 1;

    public const ushort WriteRequest = 2;

    public const ushort Data = 3;

    public const ushort Acknowledgement = 4;

    public const ushort Error = 5;

    public const ushort OptionAcknowledgement = 6;
}

/// <summary>RFC 1350 error codes plus RFC 2347 error code 8.</summary>
public static class TftpErrorCode
{
    public const ushort NotDefined = 0;

    public const ushort FileNotFound = 1;

    public const ushort AccessViolation = 2;

    public const ushort DiskFull = 3;

    public const ushort IllegalOperation = 4;

    public const ushort UnknownTransferId = 5;

    public const ushort FileExists = 6;

    public const ushort NoSuchUser = 7;

    public const ushort OptionNegotiationFailed = 8;
}

/// <summary>Numeric limits from RFC 1350, RFC 2348, and RFC 7440.</summary>
public static class TftpLimits
{
    public const int DefaultBlockSize = 512;

    public const int MinBlockSize = 8;

    public const int MaxBlockSize = 65464;

    public const int DefaultWindowSize = 1;

    public const int MinWindowSize = 1;

    public const int MaxWindowSize = 65535;

    public const int DataHeaderLength = 4;

    public const int MaxOptions = 16;

    public const int MaxRequestLength = 2048;

    public const string OctetMode = "octet";

    public const string BlockSizeOption = "blksize";

    public const string WindowSizeOption = "windowsize";

    /// <summary>
    /// UDP payload that fits in a 1500-byte Ethernet frame after a 20-byte IPv4 header,
    /// an 8-byte UDP header, and the 4-byte TFTP DATA header, with a small margin.
    /// Larger negotiated blocks are legal and may be fragmented by the network.
    /// </summary>
    public const int EthernetSafeBlockSize = 1428;
}

/// <summary>One RFC 2347 option name and ASCII value.</summary>
/// <param name="Name">Option name. Comparison is case-insensitive.</param>
/// <param name="Value">ASCII option value, without the terminating NUL.</param>
public readonly record struct TftpOption(string Name, string Value);

/// <summary>
/// Decoded TFTP packet. <see cref="Data"/> aliases the source buffer and is valid only
/// until that buffer is overwritten or reused.
/// </summary>
public readonly ref struct TftpView
{
    public ushort Opcode { get; init; }

    public ushort Block { get; init; }

    public ReadOnlySpan<byte> Data { get; init; }

    public ushort ErrorCode { get; init; }

    public string ErrorMessage { get; init; }

    public string FileName { get; init; }

    public string Mode { get; init; }

    public IReadOnlyList<TftpOption> Options { get; init; }
}

/// <summary>Encodes and decodes the RFC 1350 and RFC 2347 packet layouts.</summary>
public static class TftpCodec
{
    /// <summary>Writes an RRQ or WRQ. Returns the number of bytes written.</summary>
    public static int EncodeRequest(Span<byte> destination, ushort opcode, string fileName, string mode, ReadOnlySpan<TftpOption> options)
    {
        if (opcode is not (TftpOpcode.ReadRequest or TftpOpcode.WriteRequest))
        {
            throw new ArgumentOutOfRangeException(nameof(opcode));
        }

        var offset = WriteOpcode(destination, opcode);
        offset += WriteAsciiZ(destination[offset..], fileName);
        offset += WriteAsciiZ(destination[offset..], mode);
        for (var i = 0; i < options.Length; i++)
        {
            offset += WriteAsciiZ(destination[offset..], options[i].Name);
            offset += WriteAsciiZ(destination[offset..], options[i].Value);
        }

        return offset;
    }

    /// <summary>Writes an OACK for the accepted options only.</summary>
    public static int EncodeOack(Span<byte> destination, ReadOnlySpan<TftpOption> options)
    {
        var offset = WriteOpcode(destination, TftpOpcode.OptionAcknowledgement);
        for (var i = 0; i < options.Length; i++)
        {
            offset += WriteAsciiZ(destination[offset..], options[i].Name);
            offset += WriteAsciiZ(destination[offset..], options[i].Value);
        }

        return offset;
    }

    /// <summary>Writes a DATA header and copies <paramref name="data"/> after it.</summary>
    public static int EncodeData(Span<byte> destination, ushort block, ReadOnlySpan<byte> data)
    {
        if (destination.Length < TftpLimits.DataHeaderLength + data.Length)
        {
            throw new ArgumentException("Destination is smaller than the DATA packet.", nameof(destination));
        }

        BinaryPrimitives.WriteUInt16BigEndian(destination, TftpOpcode.Data);
        BinaryPrimitives.WriteUInt16BigEndian(destination[2..], block);
        data.CopyTo(destination[TftpLimits.DataHeaderLength..]);
        return TftpLimits.DataHeaderLength + data.Length;
    }

    /// <summary>Writes a 4-byte ACK.</summary>
    public static int EncodeAck(Span<byte> destination, ushort block)
    {
        if (destination.Length < 4)
        {
            throw new ArgumentException("Destination is smaller than an ACK.", nameof(destination));
        }

        BinaryPrimitives.WriteUInt16BigEndian(destination, TftpOpcode.Acknowledgement);
        BinaryPrimitives.WriteUInt16BigEndian(destination[2..], block);
        return 4;
    }

    /// <summary>Writes an ERROR packet. The message is truncated to 240 ASCII bytes.</summary>
    public static int EncodeError(Span<byte> destination, ushort code, string message)
    {
        var text = string.IsNullOrEmpty(message) ? "error" : message;
        if (text.Length > 240)
        {
            text = text[..240];
        }

        var offset = WriteOpcode(destination, TftpOpcode.Error);
        if (destination.Length < offset + 2)
        {
            throw new ArgumentException("Destination is smaller than an ERROR packet.", nameof(destination));
        }

        BinaryPrimitives.WriteUInt16BigEndian(destination[offset..], code);
        offset += 2;
        offset += WriteAsciiZ(destination[offset..], ToAscii(text));
        return offset;
    }

    /// <summary>
    /// Decodes one datagram. Returns false for truncated or non-ASCII packets.
    /// DATA and ACK views alias <paramref name="source"/>.
    /// </summary>
    public static bool TryRead(ReadOnlySpan<byte> source, out TftpView packet, out string error)
    {
        packet = default;
        error = "";
        if (source.Length < 2)
        {
            error = "packet shorter than an opcode";
            return false;
        }

        var opcode = BinaryPrimitives.ReadUInt16BigEndian(source);
        switch (opcode)
        {
            case TftpOpcode.ReadRequest:
            case TftpOpcode.WriteRequest:
                return TryReadRequest(source, opcode, out packet, out error);
            case TftpOpcode.OptionAcknowledgement:
                return TryReadOack(source, out packet, out error);
            case TftpOpcode.Data:
                if (source.Length < TftpLimits.DataHeaderLength)
                {
                    error = "DATA shorter than the header";
                    return false;
                }

                packet = new TftpView
                {
                    Opcode = opcode,
                    Block = BinaryPrimitives.ReadUInt16BigEndian(source[2..]),
                    Data = source[TftpLimits.DataHeaderLength..],
                    Options = [],
                };
                return true;
            case TftpOpcode.Acknowledgement:
                if (source.Length != 4)
                {
                    error = "ACK is not 4 bytes";
                    return false;
                }

                packet = new TftpView
                {
                    Opcode = opcode,
                    Block = BinaryPrimitives.ReadUInt16BigEndian(source[2..]),
                    Options = [],
                };
                return true;
            case TftpOpcode.Error:
                return TryReadError(source, out packet, out error);
            default:
                error = "unknown opcode";
                return false;
        }
    }

    /// <summary>True when <paramref name="fileName"/> is a single safe path segment.</summary>
    public static bool IsSafeFileName(string fileName)
    {
        if (string.IsNullOrEmpty(fileName) || fileName.Length > 128 || fileName[0] == '.')
        {
            return false;
        }

        foreach (var ch in fileName)
        {
            var ok = ch is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '_' or '-';
            if (!ok)
            {
                return false;
            }
        }

        return !fileName.Contains("..", StringComparison.Ordinal);
    }

    private static bool TryReadRequest(ReadOnlySpan<byte> source, ushort opcode, out TftpView packet, out string error)
    {
        packet = default;
        error = "";
        if (source.Length > TftpLimits.MaxRequestLength)
        {
            error = "request longer than 2048 bytes";
            return false;
        }

        var offset = 2;
        if (!TryReadCString(source, ref offset, out var fileName) || !TryReadCString(source, ref offset, out var mode))
        {
            error = "request is missing a NUL-terminated field";
            return false;
        }

        if (!TryReadOptions(source, ref offset, out var options, out error))
        {
            return false;
        }

        if (offset != source.Length)
        {
            error = "request has trailing bytes";
            return false;
        }

        packet = new TftpView
        {
            Opcode = opcode,
            FileName = fileName,
            Mode = mode,
            Options = options,
        };
        return true;
    }

    private static bool TryReadOack(ReadOnlySpan<byte> source, out TftpView packet, out string error)
    {
        packet = default;
        var offset = 2;
        if (!TryReadOptions(source, ref offset, out var options, out error))
        {
            return false;
        }

        if (options.Count == 0 || offset != source.Length)
        {
            error = options.Count == 0 ? "OACK contains no options" : "OACK has trailing bytes";
            return false;
        }

        packet = new TftpView
        {
            Opcode = TftpOpcode.OptionAcknowledgement,
            Options = options,
        };
        return true;
    }

    private static bool TryReadError(ReadOnlySpan<byte> source, out TftpView packet, out string error)
    {
        packet = default;
        error = "";
        if (source.Length < 5)
        {
            error = "ERROR shorter than the header and NUL";
            return false;
        }

        var offset = 4;
        if (!TryReadCString(source, ref offset, out var message) || offset != source.Length)
        {
            error = "ERROR message is not a single NUL-terminated string";
            return false;
        }

        packet = new TftpView
        {
            Opcode = TftpOpcode.Error,
            ErrorCode = BinaryPrimitives.ReadUInt16BigEndian(source[2..]),
            ErrorMessage = message,
            Options = [],
        };
        return true;
    }

    private static bool TryReadOptions(ReadOnlySpan<byte> source, ref int offset, out List<TftpOption> options, out string error)
    {
        options = [];
        error = "";
        while (offset < source.Length)
        {
            if (options.Count >= TftpLimits.MaxOptions)
            {
                error = "too many options";
                return false;
            }

            if (!TryReadCString(source, ref offset, out var name) || !TryReadCString(source, ref offset, out var value))
            {
                error = "option is missing a NUL-terminated field";
                return false;
            }

            if (name.Length == 0)
            {
                error = "option name is empty";
                return false;
            }

            options.Add(new TftpOption(name, value));
        }

        return true;
    }

    private static bool TryReadCString(ReadOnlySpan<byte> source, ref int offset, out string value)
    {
        var start = offset;
        while (offset < source.Length && source[offset] != 0)
        {
            if (source[offset] > 127)
            {
                value = "";
                return false;
            }

            offset++;
        }

        if (offset >= source.Length)
        {
            value = "";
            return false;
        }

        value = Encoding.ASCII.GetString(source.Slice(start, offset - start));
        offset++;
        return true;
    }

    private static int WriteOpcode(Span<byte> destination, ushort opcode)
    {
        if (destination.Length < 2)
        {
            throw new ArgumentException("Destination is smaller than an opcode.", nameof(destination));
        }

        BinaryPrimitives.WriteUInt16BigEndian(destination, opcode);
        return 2;
    }

    private static int WriteAsciiZ(Span<byte> destination, string text)
    {
        var ascii = ToAscii(text);
        if (destination.Length < ascii.Length + 1)
        {
            throw new ArgumentException("Destination is smaller than the ASCII field.", nameof(destination));
        }

        Encoding.ASCII.GetBytes(ascii, destination);
        destination[ascii.Length] = 0;
        return ascii.Length + 1;
    }

    private static string ToAscii(string text)
    {
        foreach (var ch in text)
        {
            if (ch > 127)
            {
                throw new ArgumentException("TFTP fields are ASCII.", nameof(text));
            }
        }

        return text;
    }
}
