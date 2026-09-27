using VectorNNTP.NNTPD.Configuration;
using VectorNNTP.NNTPD.PostFilter;

namespace VectorNNTP.NNTPD.Tests.PostFilter;

public sealed class PostFilterPolicyCompilerTests
{
    [Fact]
    public void Compile_AssignsRevision()
    {
        var snapshot = PostFilterPolicyCompiler.Compile(new PostFilterOptions(), revision: 12);
        Assert.Equal(12, snapshot.Revision);
        Assert.Equal(PostFilterGateState.Disabled, snapshot.Gate);
    }

    [Fact]
    public void Compile_DuplicateAccount_FailsCompletely()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            PostFilterPolicyCompiler.Compile(new PostFilterOptions
            {
                DeniedAccounts = ["poster", "poster"],
            }));
        Assert.Contains("duplicate", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_EmptyAccount_FailsCompletely()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            PostFilterPolicyCompiler.Compile(new PostFilterOptions
            {
                AllowlistedAccounts = [" "],
            }));
        Assert.Contains("malformed", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_InvalidCidr_FailsCompletely()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            PostFilterPolicyCompiler.Compile(new PostFilterOptions
            {
                DeniedCidrs = ["not-a-cidr"],
            }));
        Assert.Contains("CIDR", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_InvalidWindow_FailsCompletely()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            PostFilterPolicyCompiler.Compile(new PostFilterOptions
            {
                Quota = new PostFilterQuotaOptions { LongWindow = TimeSpan.Zero },
            }));
        Assert.Contains("window", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_NegativeMaxArticleSize_FailsCompletely()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            PostFilterPolicyCompiler.Compile(new PostFilterOptions
            {
                SpamAssassin = new PostFilterSpamAssassinOptions { MaxArticleSize = -1 },
            }));
        Assert.Contains("MaxArticleSize", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_UnknownArtType_FailsCompletely()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            PostFilterPolicyCompiler.Compile(new PostFilterOptions
            {
                RejectArtTypes = ["NotAnArtType"],
            }));
        Assert.Contains("ArtType", ex.Message, StringComparison.Ordinal);
    }
}
