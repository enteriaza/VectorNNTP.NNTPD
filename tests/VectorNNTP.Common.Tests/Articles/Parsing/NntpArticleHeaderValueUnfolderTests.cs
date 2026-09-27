using VectorNNTP.Common.Articles.Parsing;

namespace VectorNNTP.Common.Tests.Articles.Parsing;

public sealed class NntpArticleHeaderValueUnfolderTests
{
    [Fact]
    public void Unfold_replaces_crlf_fold_with_a_single_space()
    {
        ReadOnlySpan<byte> raw = "Sat, 26 Sep\r\n 2026 12:00:00 +0000"u8;
        Span<byte> destination = stackalloc byte[64];

        Assert.True(NntpArticleHeaderValueUnfolder.TryUnfold(raw, destination, out var written));
        Assert.True("Sat, 26 Sep 2026 12:00:00 +0000"u8.SequenceEqual(destination[..written]));
    }

    [Theory]
    [InlineData("Sat, 26 Sep\r 2026", "Sat, 26 Sep 2026")]
    [InlineData("Sat, 26 Sep\n 2026", "Sat, 26 Sep 2026")]
    public void Unfold_accepts_single_character_fold_terminators(string rawText, string expected)
    {
        var raw = System.Text.Encoding.ASCII.GetBytes(rawText);
        Span<byte> destination = stackalloc byte[64];

        Assert.True(NntpArticleHeaderValueUnfolder.TryUnfold(raw, destination, out var written));
        Assert.Equal(expected, System.Text.Encoding.ASCII.GetString(destination[..written]));
    }

    [Fact]
    public void Unfold_rejects_a_line_break_that_is_not_a_continuation()
    {
        ReadOnlySpan<byte> raw = "Sat, 26 Sep\r\n2026"u8;
        Span<byte> destination = stackalloc byte[64];
        Assert.False(NntpArticleHeaderValueUnfolder.TryUnfold(raw, destination, out _));
    }

    [Fact]
    public void Unfold_rejects_when_destination_is_too_small()
    {
        ReadOnlySpan<byte> raw = "Sat, 26 Sep\r\n 2026"u8;
        Span<byte> destination = stackalloc byte[4];
        Assert.False(NntpArticleHeaderValueUnfolder.TryUnfold(raw, destination, out _));
    }

    [Fact]
    public void Unfold_copies_an_unfolded_value_exactly()
    {
        ReadOnlySpan<byte> raw = "plain-value"u8;
        Span<byte> destination = stackalloc byte[16];
        Assert.True(NntpArticleHeaderValueUnfolder.TryUnfold(raw, destination, out var written));
        Assert.True(raw.SequenceEqual(destination[..written]));
    }
}
