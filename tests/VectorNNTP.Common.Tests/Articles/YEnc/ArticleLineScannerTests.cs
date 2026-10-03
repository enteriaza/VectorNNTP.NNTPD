using System.Text;
using VectorNNTP.Common.Articles.YEnc;

namespace VectorNNTP.Common.Tests.Articles.YEnc
{
    public sealed class ArticleLineScannerTests
    {
        [Theory]
        [InlineData("abc\r\ndef", 0, 3)]
        [InlineData("abc\ndef", 0, 3)]
        [InlineData("abc\rdef\nxyz", 0, 7)]
        [InlineData("\nxyz", 0, 0)]
        [InlineData("\r\nxyz", 0, 0)]
        [InlineData("abcdef", 0, -1)]
        public void IndexOfCrLf_WhenScanningMixedTerminators_ReturnsExpectedIndex(string text, int startOffset, int expected)
        {
            var index = ArticleLineScanner.IndexOfCrLf(Encoding.ASCII.GetBytes(text), startOffset);
            Assert.Equal(expected, index);
        }

        [Fact]
        public void IndexOfCrLf_WhenCrossingVectorBoundaries_MatchesExpectedResult()
        {
            var buffer = Encoding.ASCII.GetBytes(new string('a', 15) + "\r\n" + new string('b', 17) + "\n" + "tail");

            Assert.Equal(15, ArticleLineScanner.IndexOfCrLf(buffer, 0));
            Assert.Equal(34, ArticleLineScanner.IndexOfCrLf(buffer, 17));
            Assert.Equal(34, ArticleLineScanner.IndexOfCrLf(buffer, 33));
            Assert.Equal(-1, ArticleLineScanner.IndexOfCrLf(buffer, buffer.Length));
        }

        [Fact]
        public void AdvancePastLineTerminator_WhenCalledWithDifferentTerminators_ReturnsExpectedOffset()
        {
            var buffer = "a\r\nb\nc"u8.ToArray();

            Assert.Equal(3, ArticleLineScanner.AdvancePastLineTerminator(buffer, 1));
            Assert.Equal(5, ArticleLineScanner.AdvancePastLineTerminator(buffer, 4));
            Assert.Equal(buffer.Length, ArticleLineScanner.AdvancePastLineTerminator(buffer, 100));
        }

        [Fact]
        public void FindLineStartingWith_WhenSearchingForAnchoredPrefix_OnlyMatchesLineStart()
        {
            var buffer = "x=yend size=1 crc32=1\r\n=yend size=2 crc32=2\r\n"u8.ToArray();
            var match = ArticleLineScanner.FindLineStartingWith(buffer, 0, "=yend "u8);

            Assert.True(match > 0);
            Assert.Equal((byte)'=', buffer[match]);
            Assert.Equal(-1, ArticleLineScanner.FindLineStartingWith(buffer, match + 1, "=ybegin "u8));
        }

        [Fact]
        public void IndexOfCrLf_WhenComparedToScalarReference_ProducesIdenticalResults()
        {
            var buffer = BuildBoundaryHeavyBuffer();

            for (var start = 0; start < buffer.Length + 3; start++)
            {
                var simd = ArticleLineScanner.IndexOfCrLf(buffer, start);
                var scalar = IndexOfCrLfScalarReference(buffer, start);
                Assert.Equal(scalar, simd);
            }
        }

        [Fact]
        public void IndexOfCrLf_WhenRandomizedAcrossThousandsOfInputs_MatchesScalarReference()
        {
            var random = new Random(20260825);

            for (var caseIndex = 0; caseIndex < 4096; caseIndex++)
            {
                var length = random.Next(0, 1024);
                var buffer = new byte[length];

                for (var i = 0; i < length; i++)
                {
                    var selector = random.Next(0, 16);
                    buffer[i] = selector switch
                    {
                        0 => (byte)'\r',
                        1 => (byte)'\n',
                        2 => (byte)'.',
                        3 => (byte)'=',
                        _ => (byte)random.Next(32, 127),
                    };
                }

                for (var startOffset = 0; startOffset < length + 2; startOffset++)
                {
                    var simd = ArticleLineScanner.IndexOfCrLf(buffer, startOffset);
                    var scalar = IndexOfCrLfScalarReference(buffer, startOffset);
                    Assert.Equal(scalar, simd);
                }
            }
        }

        private static int IndexOfCrLfScalarReference(ReadOnlySpan<byte> buffer, int startOffset)
        {
            if ((uint)startOffset >= (uint)buffer.Length)
            {
                return -1;
            }

            for (var i = startOffset; i < buffer.Length; i++)
            {
                if (buffer[i] == (byte)'\r' && i + 1 < buffer.Length && buffer[i + 1] == (byte)'\n')
                {
                    return i;
                }

                if (buffer[i] == (byte)'\n' && (i == startOffset || buffer[i - 1] != (byte)'\r'))
                {
                    return i;
                }
            }

            return -1;
        }

        private static byte[] BuildBoundaryHeavyBuffer()
        {
            var data = new List<byte>(768);
            for (var i = 0; i < 384; i++)
            {
                data.Add((byte)('a' + (i % 26)));
                if (i % 16 == 15)
                {
                    data.Add((byte)'\r');
                    data.Add((byte)'\n');
                }
                else if (i % 17 == 0)
                {
                    data.Add((byte)'\n');
                }
                else if (i % 31 == 0)
                {
                    data.Add((byte)'\r');
                }
            }

            if (data[^1] != (byte)'\n')
            {
                data.Add((byte)'\n');
            }

            return [.. data];
        }
    }
}
