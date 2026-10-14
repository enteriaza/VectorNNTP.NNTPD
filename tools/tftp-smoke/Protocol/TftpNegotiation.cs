namespace VectorNNTP.TftpSmoke.Protocol;

/// <summary>Block size and window size agreed for one transfer.</summary>
/// <param name="BlockSize">DATA payload size in bytes.</param>
/// <param name="WindowSize">DATA blocks a sender may transmit before it must wait for an ACK.</param>
/// <param name="Oack">Encoded OACK. Empty when no option was accepted and the transfer stays at RFC 1350 defaults.</param>
public readonly record struct NegotiatedTransfer(int BlockSize, int WindowSize, byte[] Oack);

/// <summary>RFC 2347, RFC 2348, and RFC 7440 option negotiation.</summary>
public static class TftpNegotiation
{
    /// <summary>
    /// Accepts a request's options. Unknown options are omitted. Illegal values of
    /// blksize or windowsize fail the request. The OACK never raises a value above the request.
    /// </summary>
    public static bool TryAccept(
        IReadOnlyList<TftpOption> requested,
        int maxBlockSize,
        int maxWindowSize,
        out NegotiatedTransfer negotiated,
        out ushort errorCode,
        out string error)
    {
        negotiated = default;
        errorCode = TftpErrorCode.OptionNegotiationFailed;
        error = "";
        if (maxBlockSize is < TftpLimits.MinBlockSize or > TftpLimits.MaxBlockSize)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBlockSize));
        }

        if (maxWindowSize is < TftpLimits.MinWindowSize or > TftpLimits.MaxWindowSize)
        {
            throw new ArgumentOutOfRangeException(nameof(maxWindowSize));
        }

        var blockSize = TftpLimits.DefaultBlockSize;
        var windowSize = TftpLimits.DefaultWindowSize;
        var accepted = new List<TftpOption>(2);
        var seenBlock = false;
        var seenWindow = false;
        foreach (var option in requested)
        {
            if (option.Name.Equals(TftpLimits.BlockSizeOption, StringComparison.OrdinalIgnoreCase))
            {
                if (seenBlock)
                {
                    error = "duplicate blksize";
                    return false;
                }

                seenBlock = true;
                if (!TryParseDecimal(option.Value, TftpLimits.MinBlockSize, TftpLimits.MaxBlockSize, out var requestedBlock))
                {
                    error = "blksize is outside 8..65464";
                    return false;
                }

                blockSize = Math.Min(requestedBlock, maxBlockSize);
                accepted.Add(new TftpOption(TftpLimits.BlockSizeOption, blockSize.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            }
            else if (option.Name.Equals(TftpLimits.WindowSizeOption, StringComparison.OrdinalIgnoreCase))
            {
                if (seenWindow)
                {
                    error = "duplicate windowsize";
                    return false;
                }

                seenWindow = true;
                if (!TryParseDecimal(option.Value, TftpLimits.MinWindowSize, TftpLimits.MaxWindowSize, out var requestedWindow))
                {
                    error = "windowsize is outside 1..65535";
                    return false;
                }

                windowSize = Math.Min(requestedWindow, maxWindowSize);
                accepted.Add(new TftpOption(TftpLimits.WindowSizeOption, windowSize.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            }
        }

        byte[] oack = [];
        if (accepted.Count > 0)
        {
            var buffer = new byte[512];
            var written = TftpCodec.EncodeOack(buffer, accepted.ToArray());
            oack = buffer[..written];
        }

        negotiated = new NegotiatedTransfer(blockSize, windowSize, oack);
        errorCode = 0;
        return true;
    }

    /// <summary>
    /// Applies an OACK to the client's request. Missing options fall back to RFC 1350 defaults.
    /// A value above the client's request, or an option the client did not send, is rejected.
    /// </summary>
    public static bool TryAdopt(
        bool requestedBlockSize,
        int requestedBlock,
        bool requestedWindowSize,
        int requestedWindow,
        IReadOnlyList<TftpOption> oack,
        out int blockSize,
        out int windowSize,
        out string error)
    {
        blockSize = TftpLimits.DefaultBlockSize;
        windowSize = TftpLimits.DefaultWindowSize;
        error = "";
        var seenBlock = false;
        var seenWindow = false;
        foreach (var option in oack)
        {
            if (option.Name.Equals(TftpLimits.BlockSizeOption, StringComparison.OrdinalIgnoreCase))
            {
                if (!requestedBlockSize || seenBlock)
                {
                    error = "OACK blksize was not requested once";
                    return false;
                }

                seenBlock = true;
                if (!TryParseDecimal(option.Value, TftpLimits.MinBlockSize, TftpLimits.MaxBlockSize, out var value) || value > requestedBlock)
                {
                    error = "OACK blksize is invalid or higher than the request";
                    return false;
                }

                blockSize = value;
            }
            else if (option.Name.Equals(TftpLimits.WindowSizeOption, StringComparison.OrdinalIgnoreCase))
            {
                if (!requestedWindowSize || seenWindow)
                {
                    error = "OACK windowsize was not requested once";
                    return false;
                }

                seenWindow = true;
                if (!TryParseDecimal(option.Value, TftpLimits.MinWindowSize, TftpLimits.MaxWindowSize, out var value) || value > requestedWindow)
                {
                    error = "OACK windowsize is invalid or higher than the request";
                    return false;
                }

                windowSize = value;
            }
            else
            {
                error = "OACK contains an option the client did not request";
                return false;
            }
        }

        return true;
    }

    /// <summary>Parses an unsigned decimal with no sign, spaces, or empty text.</summary>
    public static bool TryParseDecimal(string text, int min, int max, out int value)
    {
        value = 0;
        if (string.IsNullOrEmpty(text) || text.Length > 5)
        {
            return false;
        }

        var parsed = 0;
        foreach (var ch in text)
        {
            if (ch is < '0' or > '9')
            {
                return false;
            }

            parsed = (parsed * 10) + (ch - '0');
        }

        if (parsed < min || parsed > max)
        {
            return false;
        }

        value = parsed;
        return true;
    }
}
