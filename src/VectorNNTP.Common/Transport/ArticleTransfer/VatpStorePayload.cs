using System.Buffers;
using VectorNNTP.Common.Articles;

namespace VectorNNTP.Common.Transport.ArticleTransfer
{
    /// <summary>STORE payload: 32-byte <see cref="ArticleId"/>.</summary>
    internal static class VatpStorePayload
    {
        /// <summary>Attempts to decode a STORE payload sequence.</summary>
        internal static bool TryDecode(in ReadOnlySequence<byte> payload, out ArticleId articleId, out VatpErrorCode error)
        {
            articleId = default;
            error = VatpErrorCode.None;
            if (payload.Length != VatpProtocol.StorePayloadLength)
            {
                error = VatpErrorCode.InvalidFrameLength;
                return false;
            }

            Span<byte> buffer = stackalloc byte[VatpProtocol.StorePayloadLength];
            payload.CopyTo(buffer);
            return TryDecode(buffer, out articleId, out error);
        }

        /// <summary>Attempts to decode a contiguous STORE payload.</summary>
        private static bool TryDecode(ReadOnlySpan<byte> payload, out ArticleId articleId, out VatpErrorCode error)
        {
            articleId = default;
            error = VatpErrorCode.None;
            if (payload.Length != VatpProtocol.StorePayloadLength)
            {
                error = VatpErrorCode.InvalidFrameLength;
                return false;
            }

            try
            {
                articleId = ArticleId.FromSpan(payload);
            }
            catch (ArgumentException)
            {
                error = VatpErrorCode.InvalidFrameLength;
                return false;
            }

            return true;
        }
    }

    /// <summary>RESULT payload: one outcome byte.</summary>
    internal static class VatpResultPayload
    {
        /// <summary>Attempts to decode a RESULT payload sequence.</summary>
        internal static bool TryDecode(in ReadOnlySequence<byte> payload, out byte outcome, out VatpErrorCode error)
        {
            outcome = 0;
            error = VatpErrorCode.None;
            if (payload.Length != VatpProtocol.ResultPayloadLength)
            {
                error = VatpErrorCode.InvalidFrameLength;
                return false;
            }

            if (payload.IsSingleSegment)
            {
                outcome = payload.FirstSpan[0];
                return true;
            }

            Span<byte> buffer = stackalloc byte[VatpProtocol.ResultPayloadLength];
            payload.CopyTo(buffer);
            outcome = buffer[0];
            return true;
        }
    }
}
