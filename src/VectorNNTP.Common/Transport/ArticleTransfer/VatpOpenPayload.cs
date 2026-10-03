using System.Buffers;
using VectorNNTP.Common.Articles;

namespace VectorNNTP.Common.Transport.ArticleTransfer
{
    /// <summary>
    /// OPEN payload: 16-byte RequestId GUID + 32-byte <see cref="ArticleId"/>.
    /// </summary>
    internal static class VatpOpenPayload
    {
        /// <summary>Decoded OPEN fields.</summary>
        internal readonly record struct OpenPayload(Guid RequestId, ArticleId ArticleId);

        /// <summary>Attempts to decode an OPEN payload sequence.</summary>
        internal static bool TryDecode(in ReadOnlySequence<byte> payload, out OpenPayload open, out VatpErrorCode error)
        {
            open = default;
            error = VatpErrorCode.None;
            if (payload.Length != VatpProtocol.OpenPayloadLength)
            {
                error = VatpErrorCode.InvalidOpen;
                return false;
            }

            Span<byte> buffer = stackalloc byte[VatpProtocol.OpenPayloadLength];
            payload.CopyTo(buffer);
            return TryDecode(buffer, out open, out error);
        }

        /// <summary>Attempts to decode a contiguous OPEN payload.</summary>
        private static bool TryDecode(ReadOnlySpan<byte> payload, out OpenPayload open, out VatpErrorCode error)
        {
            open = default;
            error = VatpErrorCode.None;
            if (payload.Length != VatpProtocol.OpenPayloadLength)
            {
                error = VatpErrorCode.InvalidOpen;
                return false;
            }

            var requestId = new Guid(payload[..VatpProtocol.RequestIdLength]);
            if (requestId == Guid.Empty)
            {
                error = VatpErrorCode.InvalidOpen;
                return false;
            }

            ArticleId articleId;
            try
            {
                articleId = ArticleId.FromSpan(payload.Slice(VatpProtocol.RequestIdLength, VatpProtocol.ArticleIdLength));
            }
            catch (ArgumentException)
            {
                error = VatpErrorCode.InvalidOpen;
                return false;
            }

            open = new OpenPayload(requestId, articleId);
            return true;
        }

        /// <summary>Writes OPEN payload bytes.</summary>
        private static void Encode(Span<byte> destination, Guid requestId, in ArticleId articleId)
        {
            if (destination.Length < VatpProtocol.OpenPayloadLength)
            {
                throw new ArgumentException("Destination is smaller than OPEN payload.", nameof(destination));
            }

#pragma warning disable CA1512 // Use ArgumentOutOfRangeException throw helper
            if (requestId == Guid.Empty)
            {
                throw new ArgumentOutOfRangeException(nameof(requestId));
            }
#pragma warning restore CA1512 // Use ArgumentOutOfRangeException throw helper

            if (!requestId.TryWriteBytes(destination[..VatpProtocol.RequestIdLength]))
            {
                throw new InvalidOperationException("Failed to write RequestId.");
            }

            articleId.CopyTo(destination.Slice(VatpProtocol.RequestIdLength, VatpProtocol.ArticleIdLength));
        }
    }
}
