using System.Text;
using VectorNNTP.Common.Articles.YEnc;

namespace VectorNNTP.Common.Tests.Articles.YEnc
{
    /// <summary>
    /// Proves <see cref="YEncArticleValidator"/> accepts the CRC from the scalar <see cref="YEncCrc32"/> oracle,
    /// including batch boundaries, escapes, multipart <c>pcrc32</c>, a mismatch, and a trailing escape.
    /// </summary>
    public sealed class YEncHardwareCrcEquivalenceTests
    {
        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(15)]
        [InlineData(16)]
        [InlineData(511)]
        [InlineData(512)]
        [InlineData(513)]
        [InlineData(4096)]
        public void Validate_AcceptsScalarCrc_ForDecodedLengthsThatCrossTheBatchBoundary(int length)
        {
            var decoded = MixedPayload(length);
            var result = YEncArticleValidator.Validate(SinglePart(decoded, YEncCrc32.Compute(decoded)));

            Assert.Equal(YEncArticleValidationStatus.ValidSinglePart, result.Status);
            Assert.Equal(1, result.SectionsValidated);
        }

        [Fact]
        public void Validate_AcceptsTheIeeeCheckValue_AndRejectsCastagnoli()
        {
            var decoded = "123456789"u8.ToArray();
            var ieee = YEncCrc32.Compute(decoded);

            Assert.Equal(0xCBF43926u, ieee);
            Assert.NotEqual(0xE3069283u, ieee);
            Assert.Equal(
                YEncArticleValidationStatus.ValidSinglePart,
                YEncArticleValidator.Validate(SinglePart(decoded, ieee)).Status);
            Assert.Equal(
                YEncArticleValidationStatus.CrcMismatch,
                YEncArticleValidator.Validate(SinglePart(decoded, 0xE3069283u)).Status);
        }

        [Fact]
        public void Validate_MatchesScalarCrc_ForEscapedAndOrdinaryBytes()
        {
            byte[] escaped = [4, 19, 214, 223, 224, 227, 246];
            byte[] ordinary = [0, 1, 65, 100, 200];
            var mixed = new byte[escaped.Length + ordinary.Length];
            escaped.CopyTo(mixed, 0);
            ordinary.CopyTo(mixed, escaped.Length);

            Assert.Equal(
                YEncArticleValidationStatus.ValidSinglePart,
                YEncArticleValidator.Validate(SinglePart(escaped, YEncCrc32.Compute(escaped))).Status);
            Assert.Equal(
                YEncArticleValidationStatus.ValidSinglePart,
                YEncArticleValidator.Validate(SinglePart(ordinary, YEncCrc32.Compute(ordinary))).Status);
            Assert.Equal(
                YEncArticleValidationStatus.ValidSinglePart,
                YEncArticleValidator.Validate(SinglePart(mixed, YEncCrc32.Compute(mixed))).Status);
        }

        [Fact]
        public void Validate_MatchesScalarCrc_ForMultipartPcrcAndAFollowingSection()
        {
            var first = MixedPayload(600);
            var second = MixedPayload(20);
            var article = Concat(
                MultiPart(first, begin: 1, end: first.Length, YEncCrc32.Compute(first)),
                SinglePart(second, YEncCrc32.Compute(second)));

            var result = YEncArticleValidator.Validate(article);

            Assert.Equal(YEncArticleValidationStatus.ValidMultiPart, result.Status);
            Assert.Equal(2, result.SectionsValidated);
        }

        [Fact]
        public void Validate_RejectsAScalarCrcThatDoesNotMatchTheDecodedBytes()
        {
            var decoded = MixedPayload(512);
            var crc = YEncCrc32.Compute(decoded) ^ 1u;

            Assert.Equal(
                YEncArticleValidationStatus.CrcMismatch,
                YEncArticleValidator.Validate(SinglePart(decoded, crc)).Status);
        }

        [Fact]
        public void Validate_RejectsATrailingEscape_BeforePublishingACrc()
        {
            var article = "=ybegin line=128 size=1 name=t.bin\r\n=\r\n=yend size=1 crc32=00000000\r\n"u8;

            Assert.Equal(
                YEncArticleValidationStatus.InvalidEscapeSequence,
                YEncArticleValidator.Validate(article).Status);
        }

        private static byte[] MixedPayload(int length)
        {
            byte[] pattern = [0, 1, 4, 19, 65, 100, 200, 214, 223, 224, 227, 246];
            var payload = new byte[length];
            for (var i = 0; i < payload.Length; i++)
            {
                payload[i] = pattern[i % pattern.Length];
            }

            return payload;
        }

        private static byte[] SinglePart(byte[] decoded, uint crc)
        {
            var prefix = Encoding.ASCII.GetBytes($"=ybegin line=128 size={decoded.Length} name=t.bin\r\n");
            var suffix = Encoding.ASCII.GetBytes($"=yend size={decoded.Length} crc32={crc:x8}\r\n");
            return Concat(prefix, Encode(decoded), suffix);
        }

        private static byte[] MultiPart(byte[] decoded, int begin, int end, uint crc)
        {
            var prefix = Encoding.ASCII.GetBytes(
                $"=ybegin part=1 line=128 size={end} name=t.bin\r\n=ypart begin={begin} end={end}\r\n");
            var suffix = Encoding.ASCII.GetBytes($"=yend size={decoded.Length} part=1 pcrc32={crc:x8}\r\n");
            return Concat(prefix, Encode(decoded), suffix);
        }

        private static byte[] Encode(byte[] decoded)
        {
            var output = new List<byte>(decoded.Length + (decoded.Length / 32));
            var lineCount = 0;
            foreach (var value in decoded)
            {
                var encoded = unchecked((byte)(value + 42));
                if (encoded is 0 or 9 or 10 or 13 or 32 or 46 or 61)
                {
                    output.Add((byte)'=');
                    output.Add(unchecked((byte)(encoded + 64)));
                    lineCount += 2;
                }
                else
                {
                    output.Add(encoded);
                    lineCount++;
                }

                if (lineCount >= 128)
                {
                    output.Add((byte)'\r');
                    output.Add((byte)'\n');
                    lineCount = 0;
                }
            }

            if (output.Count == 0 || output[^1] != (byte)'\n')
            {
                output.Add((byte)'\r');
                output.Add((byte)'\n');
            }

            return [.. output];
        }

        private static byte[] Concat(params byte[][] parts)
        {
            var length = 0;
            foreach (var part in parts)
            {
                length += part.Length;
            }

            var result = new byte[length];
            var offset = 0;
            foreach (var part in parts)
            {
                part.CopyTo(result, offset);
                offset += part.Length;
            }

            return result;
        }
    }
}
