using System.Buffers;
using System.Buffers.Binary;

namespace VectorNNTP.Common.Transport.ArticleTransfer
{
    /// <summary>
    /// HELLO payload codec: magic <c>VNATP01\0</c> + big-endian <c>u32</c> maxFramePayload.
    /// </summary>
    internal static class VatpHello
    {
        /// <summary>Decoded HELLO parameters.</summary>
        internal readonly record struct HelloPayload(uint MaxFramePayload);

        /// <summary>Attempts to decode a HELLO payload sequence.</summary>
        internal static bool TryDecode(in ReadOnlySequence<byte> payload, out HelloPayload hello, out VatpErrorCode error)
        {
            hello = default;
            error = VatpErrorCode.None;
            if (payload.Length != VatpProtocol.HelloPayloadLength)
            {
                error = VatpErrorCode.InvalidHello;
                return false;
            }

            Span<byte> buffer = stackalloc byte[VatpProtocol.HelloPayloadLength];
            payload.CopyTo(buffer);
            return TryDecode(buffer, out hello, out error);
        }

        /// <summary>Attempts to decode a contiguous HELLO payload.</summary>
        internal static bool TryDecode(ReadOnlySpan<byte> payload, out HelloPayload hello, out VatpErrorCode error)
        {
            hello = default;
            error = VatpErrorCode.None;
            if (payload.Length != VatpProtocol.HelloPayloadLength)
            {
                error = VatpErrorCode.InvalidHello;
                return false;
            }

            if (!payload[..8].SequenceEqual(VatpProtocol.HelloMagic))
            {
                error = VatpErrorCode.InvalidHello;
                return false;
            }

            var maxFramePayload = BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(8, 4));
            if (maxFramePayload == 0)
            {
                error = VatpErrorCode.InvalidMaxFramePayload;
                return false;
            }

            hello = new HelloPayload(maxFramePayload);
            return true;
        }

        /// <summary>Writes a HELLO payload into <paramref name="destination"/>.</summary>
        internal static void Encode(Span<byte> destination, uint maxFramePayload)
        {
            if (destination.Length < VatpProtocol.HelloPayloadLength)
            {
                throw new ArgumentException("Destination is smaller than the HELLO payload.", nameof(destination));
            }

#pragma warning disable CA1512 // Use ArgumentOutOfRangeException throw helper
            if (maxFramePayload == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxFramePayload));
            }
#pragma warning restore CA1512 // Use ArgumentOutOfRangeException throw helper

            VatpProtocol.HelloMagic.CopyTo(destination);
            BinaryPrimitives.WriteUInt32BigEndian(destination.Slice(8, 4), maxFramePayload);
        }
    }
}
