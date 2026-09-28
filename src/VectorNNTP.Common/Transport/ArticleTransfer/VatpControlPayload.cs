using System.Buffers.Binary;

namespace VectorNNTP.Common.Transport.ArticleTransfer;

/// <summary>
/// FAIL and WINDOW payload helpers.
/// </summary>
public static class VatpControlPayload
{
    /// <summary>Decodes a FAIL payload into error code and optional reason bytes.</summary>
    public static bool TryDecodeFail(
        ReadOnlySpan<byte> payload,
        out VatpErrorCode errorCode,
        out ReadOnlySpan<byte> reason)
    {
        errorCode = VatpErrorCode.None;
        reason = default;
        if (payload.Length < VatpProtocol.FailMinPayloadLength
            || payload.Length > VatpProtocol.FailMaxPayloadLength)
        {
            return false;
        }

        errorCode = (VatpErrorCode)BinaryPrimitives.ReadUInt16BigEndian(payload[..2]);
        reason = payload.Length > 2 ? payload[2..] : ReadOnlySpan<byte>.Empty;
        return true;
    }

    /// <summary>Decodes a WINDOW credit-add payload.</summary>
    public static bool TryDecodeWindow(ReadOnlySpan<byte> payload, out uint addCredit)
    {
        addCredit = 0;
        if (payload.Length != VatpProtocol.WindowPayloadLength)
        {
            return false;
        }

        addCredit = BinaryPrimitives.ReadUInt32BigEndian(payload);
        return true;
    }
}
