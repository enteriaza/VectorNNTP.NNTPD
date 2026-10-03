using System.Buffers.Binary;

namespace VectorNNTP.Common.Transport.ArticleTransfer
{
    /// <summary>
    /// Fixed 16-byte VATP frame header: version, type, headerLength, streamId, payloadLength, flags.
    /// </summary>
    internal readonly record struct VatpFrameHeader(
        byte Version,
        VatpFrameType Type,
        ushort HeaderLength,
        uint StreamId,
        uint PayloadLength,
        uint Flags)
    {
        /// <summary>Gets a value indicating whether Flags.FIN is set.</summary>
        internal bool HasFin => (Flags & VatpProtocol.FlagFin) != 0;

        /// <summary>Writes this header in network byte order into <paramref name="destination"/>.</summary>
        /// <param name="destination">At least <see cref="VatpProtocol.HeaderLengthBytes"/> bytes.</param>
        internal void WriteTo(Span<byte> destination)
        {
            if (destination.Length < VatpProtocol.HeaderLengthBytes)
            {
                throw new ArgumentException("Destination is smaller than the protocol header.", nameof(destination));
            }

            destination[0] = Version;
            destination[1] = (byte)Type;
            BinaryPrimitives.WriteUInt16BigEndian(destination.Slice(2, 2), HeaderLength);
            BinaryPrimitives.WriteUInt32BigEndian(destination.Slice(4, 4), StreamId);
            BinaryPrimitives.WriteUInt32BigEndian(destination.Slice(8, 4), PayloadLength);
            BinaryPrimitives.WriteUInt32BigEndian(destination.Slice(12, 4), Flags);
        }

        /// <summary>Reads one header from network byte order.</summary>
        /// <param name="source">At least <see cref="VatpProtocol.HeaderLengthBytes"/> bytes.</param>
        /// <returns>The decoded header.</returns>
        internal static VatpFrameHeader ReadFrom(ReadOnlySpan<byte> source)
        {
            if (source.Length < VatpProtocol.HeaderLengthBytes)
            {
                throw new ArgumentException("Source is smaller than the protocol header.", nameof(source));
            }

            return new VatpFrameHeader(
                source[0],
                (VatpFrameType)source[1],
                BinaryPrimitives.ReadUInt16BigEndian(source.Slice(2, 2)),
                BinaryPrimitives.ReadUInt32BigEndian(source.Slice(4, 4)),
                BinaryPrimitives.ReadUInt32BigEndian(source.Slice(8, 4)),
                BinaryPrimitives.ReadUInt32BigEndian(source.Slice(12, 4)));
        }

        /// <summary>Creates a Version-1 header with <see cref="VatpProtocol.HeaderLengthBytes"/>.</summary>
        internal static VatpFrameHeader Create(VatpFrameType type, uint streamId, uint payloadLength, uint flags = 0) =>
            new(VatpProtocol.Version1, type, VatpProtocol.HeaderLengthBytes, streamId, payloadLength, flags);
    }
}
