using VectorNNTP.Common.Configuration;

namespace VectorNNTP.Common.Tests.Configuration
{
    /// <summary>
    /// Locks the shared FQDN builder and the distinct application prefixes.
    /// </summary>
    public sealed class ApplicationFqdnTests
    {
        [Theory]
        [InlineData("nntpd", 1, "usenet.ninja", "nntpd01.usenet.ninja")]
        [InlineData("nntpd", 8, "usenet.ninja", "nntpd08.usenet.ninja")]
        [InlineData("nntpd", 99, "usenet.ninja", "nntpd99.usenet.ninja")]
        [InlineData("backfiller", 1, "usenet.ninja", "backfiller01.usenet.ninja")]
        [InlineData("backfiller", 8, "usenet.ninja", "backfiller08.usenet.ninja")]
        [InlineData("backfiller", 99, "usenet.ninja", "backfiller99.usenet.ninja")]
        public void Build_uses_fixed_prefix_two_digit_id_and_suffix(
            string prefix,
            int serverId,
            string dnsSuffix,
            string expected)
        {
            Assert.Equal(expected, ApplicationFqdn.Build(prefix, serverId, dnsSuffix));
            Assert.Equal(
                prefix + serverId.ToString("00", System.Globalization.CultureInfo.InvariantCulture),
                ApplicationFqdn.FormatHostLabel(prefix, serverId));
        }

        [Theory]
        [InlineData(1)]
        [InlineData(8)]
        [InlineData(99)]
        public void Distinct_application_prefixes_never_share_an_fqdn(int serverId)
        {
            var nntpd = ApplicationFqdn.Build("nntpd", serverId, "usenet.ninja");
            var backfiller = ApplicationFqdn.Build("backfiller", serverId, "usenet.ninja");
            Assert.NotEqual(nntpd, backfiller);
            Assert.StartsWith("nntpd", nntpd, StringComparison.Ordinal);
            Assert.StartsWith("backfiller", backfiller, StringComparison.Ordinal);
        }

        [Fact]
        public void Build_canonicalizes_to_invariant_lowercase()
        {
            Assert.Equal(
                "nntpd01.usenet.ninja",
                ApplicationFqdn.Build("NNTPD", 1, "Usenet.Ninja."));
            Assert.Equal(
                "backfiller08.usenet.ninja",
                ApplicationFqdn.Build("BackFiller", 8, "USENET.NINJA"));
        }
    }
}
