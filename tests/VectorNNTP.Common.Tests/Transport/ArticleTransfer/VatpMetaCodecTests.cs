using System.Buffers.Binary;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Articles.Parsing;
using VectorNNTP.Common.Transport.ArticleTransfer;

namespace VectorNNTP.Common.Tests.Transport.ArticleTransfer
{
    public sealed class VatpMetaCodecTests
    {
        [Fact]
        public void EncodeDecode_RoundTripsExact76Bytes()
        {
            var (record, selected, _) = VatpTestArticles.CreateCanonical();
            var meta = ArticleCanonicalTransferMeta.FromRecord(record, selected);
            var encoded = VatpMetaCodec.Encode(in meta);
            Assert.Equal(76, encoded.Length);

            Assert.True(VatpMetaCodec.TryDecode(encoded, out var decoded, out var error));
            Assert.Equal(VatpErrorCode.None, error);
            Assert.Equal(meta, decoded);
            Assert.Equal(0, encoded[17]);
            Assert.Equal(0, encoded[18]);
            Assert.Equal(0, encoded[19]);
        }

        [Fact]
        public void Decode_NonZeroPad_Fails()
        {
            var (record, selected, _) = VatpTestArticles.CreateCanonical();
            var meta = ArticleCanonicalTransferMeta.FromRecord(record, selected);
            var encoded = VatpMetaCodec.Encode(in meta);
            encoded[18] = 1;
            Assert.False(VatpMetaCodec.TryDecode(encoded, out _, out var error));
            Assert.Equal(VatpErrorCode.InvalidMeta, error);
        }

        [Fact]
        public void Decode_AbsentRangeWithNonZeroLength_Fails()
        {
            var encoded = new byte[76];
            BinaryPrimitives.WriteInt32BigEndian(encoded.AsSpan(20, 4), -1);
            BinaryPrimitives.WriteInt32BigEndian(encoded.AsSpan(24, 4), 5);
            Assert.False(VatpMetaCodec.TryDecode(encoded, out _, out var error));
            Assert.Equal(VatpErrorCode.InvalidFieldRange, error);
        }

        [Fact]
        public void Decode_WrongLength_Fails()
        {
            Assert.False(VatpMetaCodec.TryDecode(new byte[75], out _, out var error));
            Assert.Equal(VatpErrorCode.InvalidMeta, error);
        }

        [Fact]
        public void Encode_MaximumValidValues_RoundTrip()
        {
            var fields = new ArticleFieldTable(
                new ArticleByteRange(0, ArticleResourceLimits.MaxArticleBytes),
                ArticleByteRange.Absent,
                ArticleByteRange.Absent,
                ArticleByteRange.Absent,
                ArticleByteRange.Absent,
                ArticleByteRange.Absent,
                ArticleByteRange.Absent);
            var meta = new ArticleCanonicalTransferMeta(
                ulong.MaxValue,
                ArticleResourceLimits.MaxArticleBytes,
                ArticleResourceLimits.MaxArticleBytes,
                NntpArticleHeaderName.Date,
                fields);
            var encoded = VatpMetaCodec.Encode(in meta);
            Assert.True(VatpMetaCodec.TryDecode(encoded, out var decoded, out _));
            Assert.Equal(meta, decoded);
        }

        [Fact]
        public void FrameEncoder_Meta_Requires76Bytes()
        {
            Assert.Throws<ArgumentException>(() => VatpFrameEncoder.EncodeMeta(1, new byte[75]));
        }
    }
}
