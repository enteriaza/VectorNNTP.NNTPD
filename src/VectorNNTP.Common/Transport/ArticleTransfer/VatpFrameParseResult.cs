using System.Buffers;

namespace VectorNNTP.Common.Transport.ArticleTransfer
{
    /// <summary>One parsed VATP frame (header plus payload sequence).</summary>
    internal readonly record struct VatpParsedFrame(VatpFrameHeader Header, ReadOnlySequence<byte> Payload);

    /// <summary>Incremental parse status for one frame attempt.</summary>
    internal enum VatpFrameParseStatus
    {
        /// <summary>Need more bytes.</summary>
        Incomplete = 0,

        /// <summary>One valid frame.</summary>
        Success = 1,

        /// <summary>One invalid frame.</summary>
        Invalid = 2,
    }

    /// <summary>Result of one parse attempt.</summary>
    internal readonly record struct VatpFrameParseResult(
        VatpFrameParseStatus Status,
        VatpErrorCode Error,
        long ConsumedBytes,
        VatpParsedFrame? Frame)
    {
        /// <summary>Creates an incomplete result.</summary>
        internal static VatpFrameParseResult Incomplete() =>
            new(VatpFrameParseStatus.Incomplete, VatpErrorCode.None, 0, null);

        /// <summary>Creates a success result.</summary>
        internal static VatpFrameParseResult Success(long consumedBytes, VatpParsedFrame frame) =>
            new(VatpFrameParseStatus.Success, VatpErrorCode.None, consumedBytes, frame);

        /// <summary>Creates an invalid result that consumes the declared frame when available.</summary>
        internal static VatpFrameParseResult Invalid(VatpErrorCode error, long consumedBytes) =>
            new(VatpFrameParseStatus.Invalid, error, consumedBytes, null);
    }
}
