using VectorNNTP.Common.Articles.DateParser;

namespace VectorNNTP.Common.Tests.Articles.DateParser;

public sealed class PrintableAsciiSimdTests
{
    [Fact]
    public void Empty_spans_are_printable()
    {
        Assert.True(PrintableAsciiSimd.IsAllPrintableAscii(ReadOnlySpan<char>.Empty));
        Assert.True(PrintableAsciiSimd.IsAllPrintableAscii(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void Printable_ascii_is_accepted_across_vector_widths()
    {
        var text = new string('A', 40) + " ~";
        Assert.True(PrintableAsciiSimd.IsAllPrintableAscii(text.AsSpan()));
        Assert.True(PrintableAsciiSimd.IsAllPrintableAscii(System.Text.Encoding.ASCII.GetBytes(text)));
    }

    [Fact]
    public void Control_and_high_bytes_are_rejected()
    {
        Assert.False(PrintableAsciiSimd.IsAllPrintableAscii("\nDate".AsSpan()));
        Assert.False(PrintableAsciiSimd.IsAllPrintableAscii("Date\x7F".AsSpan()));
        Assert.False(PrintableAsciiSimd.IsAllPrintableAscii([(byte)'A', 0x00]));
        Assert.False(PrintableAsciiSimd.IsAllPrintableAscii([(byte)'A', 0xFF]));
    }

    [Fact]
    public void Byte_and_char_overloads_agree_on_ascii()
    {
        const string value = "Sat, 26 Sep 2026 12:00:00 +0000";
        Assert.Equal(
            PrintableAsciiSimd.IsAllPrintableAscii(value.AsSpan()),
            PrintableAsciiSimd.IsAllPrintableAscii(System.Text.Encoding.ASCII.GetBytes(value)));
    }
}
