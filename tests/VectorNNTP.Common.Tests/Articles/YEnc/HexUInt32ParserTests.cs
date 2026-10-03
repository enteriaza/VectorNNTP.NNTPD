using System.Text;
using VectorNNTP.Common.Articles.YEnc;

namespace VectorNNTP.Common.Tests.Articles.YEnc
{
    public sealed class HexUInt32ParserTests
    {
        [Theory]
        [InlineData("1", 0x1u)]
        [InlineData("ABCDEF12", 0xABCDEF12u)]
        [InlineData("abcdef12", 0xABCDEF12u)]
        [InlineData("00000000", 0x00000000u)]
        public void TryParseHexUInt32_WhenInputIsStrictHex_ReturnsExpectedValue(string input, uint expected)
        {
            var parsed = HexUInt32Parser.TryParseHexUInt32(Encoding.ASCII.GetBytes(input), out var value);

            Assert.True(parsed);
            Assert.Equal(expected, value);
        }

        [Theory]
        [InlineData("")]
        [InlineData("123456789")]
        [InlineData("ABCDEF12G")]
        [InlineData("12345678XYZ")]
        [InlineData("-1")]
        [InlineData("+")]
        [InlineData("G")]
        [InlineData(" ")]
        [InlineData("12 34")]
        [InlineData("12_34")]
        [InlineData("１２")]
        public void TryParseHexUInt32_WhenInputContainsGarbageOrIsOutOfBounds_ReturnsFalse(string input)
        {
            var parsed = HexUInt32Parser.TryParseHexUInt32(Encoding.ASCII.GetBytes(input), out var value);

            Assert.False(parsed);
            Assert.Equal(0u, value);
        }
    }
}
