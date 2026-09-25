using System.Text;
using VectorNNTP.NNTPD.Configuration;
using VectorNNTP.NNTPD.Moderation;
using VectorNNTP.NNTPD.Tests.TestDoubles;

namespace VectorNNTP.NNTPD.Tests.Moderation;

public sealed class ConfiguredModeratorAuthorizationTests
{
    [Fact]
    public void FirstMatch_WinsOverLaterGeneralPattern()
    {
        var auth = Create(
            new ModeratorMappingOptions { Pattern = "comp.example.moderated", Address = "specific@example.com", Username = "mod-specific" },
            new ModeratorMappingOptions { Pattern = "comp.example.*", Address = "general@example.com", Username = "mod-general" });

        Assert.True(auth.TryResolve(Bytes("comp.example.moderated"), out var identity));
        Assert.Equal("specific@example.com", identity.Address);
        Assert.Equal("mod-specific", identity.Username);
    }

    [Fact]
    public void Resolve_UnknownGroup_Fails()
    {
        var auth = Create(
            new ModeratorMappingOptions { Pattern = "group.a", Address = "a@example.com", Username = "moderator-a" });
        Assert.False(auth.TryResolve(Bytes("group.b"), out _));
    }

    [Fact]
    public void CanApprove_RequiresMatchingUsernameAndAddress()
    {
        var auth = Standard();
        Assert.True(auth.CanApprove(MapNntpAuthenticationProvider.ModeratorA, Bytes("moderator-a@example.com"), Bytes("group.a")));
        Assert.False(auth.CanApprove(MapNntpAuthenticationProvider.ModeratorA, Bytes("moderator-b@example.com"), Bytes("group.a")));
        Assert.False(auth.CanApprove(MapNntpAuthenticationProvider.ModeratorB, Bytes("moderator-a@example.com"), Bytes("group.a")));
        Assert.False(auth.CanApprove(MapNntpAuthenticationProvider.NormalUser, Bytes("moderator-a@example.com"), Bytes("group.a")));
        Assert.False(auth.CanApprove(null, Bytes("moderator-a@example.com"), Bytes("group.a")));
    }

    [Fact]
    public void CanApprove_AddressComparison_IsCaseInsensitive()
    {
        var auth = Standard();
        Assert.True(auth.CanApprove(MapNntpAuthenticationProvider.ModeratorA, Bytes("Moderator-A@Example.COM"), Bytes("group.a")));
    }

    [Fact]
    public void UsernameComparison_IsOrdinal()
    {
        var auth = Standard();
        Assert.False(auth.CanApprove("Moderator-A", Bytes("moderator-a@example.com"), Bytes("group.a")));
        Assert.True(auth.IsAuthenticatedModerator(MapNntpAuthenticationProvider.ModeratorA));
        Assert.False(auth.IsAuthenticatedModerator("Moderator-A"));
        Assert.False(auth.IsAuthenticatedModerator(MapNntpAuthenticationProvider.NormalUser));
    }

    [Fact]
    public void TryAuthorizeApproval_AllModeratedTargetsMustBeCovered()
    {
        var auth = Standard();
        Assert.True(auth.TryAuthorizeApproval(
            MapNntpAuthenticationProvider.ModeratorA,
            ["moderator-a@example.com"],
            ["group.a"],
            out _));
        Assert.False(auth.TryAuthorizeApproval(
            MapNntpAuthenticationProvider.ModeratorA,
            ["moderator-a@example.com"],
            ["group.a", "group.b"],
            out var detail));
        Assert.Equal("unauthorized moderator principal", detail);
    }

    [Fact]
    public void TryAuthorizeApproval_RejectsExtraUnrelatedIdentity()
    {
        var auth = Standard();
        Assert.False(auth.TryAuthorizeApproval(
            MapNntpAuthenticationProvider.ModeratorA,
            ["moderator-a@example.com", "other@example.com"],
            ["group.a"],
            out var detail));
        Assert.Equal("conflicting Approved", detail);
    }

    [Fact]
    public void TryAuthorizeApproval_SameModeratorOnTwoPatterns_Succeeds()
    {
        var auth = Create(
            new ModeratorMappingOptions { Pattern = "group.a", Address = "shared@example.com", Username = "moderator-a" },
            new ModeratorMappingOptions { Pattern = "group.b", Address = "shared@example.com", Username = "moderator-a" });
        Assert.True(auth.TryAuthorizeApproval(
            MapNntpAuthenticationProvider.ModeratorA,
            ["shared@example.com"],
            ["group.a", "group.b"],
            out _));
    }

    [Fact]
    public void EmptyCatalogue_ResolvesNothing()
    {
        var auth = new ConfiguredModeratorAuthorization(Array.Empty<ModeratorMappingOptions>());
        Assert.False(auth.TryResolve(Bytes("group.a"), out _));
        Assert.False(auth.TryAuthorizeApproval("moderator-a", ["a@example.com"], ["group.a"], out var detail));
        Assert.Equal("unresolved moderator", detail);
    }

    [Theory]
    [InlineData("fido7.some.group", "fido7-some-group@fido7.org")]
    [InlineData("perl.foo.bar", "news-moderator-perl-foo-bar@perl.org")]
    [InlineData("comp.test", "comp-test@moderators.isc.org")]
    public void InnTemplate_ExpandsPercentSWithDotsToDashes(string group, string expected)
    {
        Assert.Equal(expected, ModeratorAddressTemplate.Expand(AddressFor(group), Bytes(group)));
    }

    [Fact]
    public void InnRoutingCatalogue_FirstMatchAndCatchAll()
    {
        var auth = InnRouting();
        Assert.True(auth.TryResolve(Bytes("fido7.some.group"), out var fido7));
        Assert.Equal("fido7-some-group@fido7.org", fido7.Address);
        Assert.Equal(string.Empty, fido7.Username);
        Assert.True(auth.TryResolve(Bytes("perl.foo.bar"), out var perl));
        Assert.Equal("news-moderator-perl-foo-bar@perl.org", perl.Address);
        Assert.True(auth.TryResolve(Bytes("comp.test"), out var catchAll));
        Assert.Equal("comp-test@moderators.isc.org", catchAll.Address);
        Assert.Equal("*", catchAll.Pattern);
    }

    [Fact]
    public void InnRoutingCatalogue_DoesNotAuthorizeInjection()
    {
        var auth = InnRouting();
        Assert.False(auth.IsAuthenticatedModerator("moderator-a"));
        Assert.False(auth.CanApprove("moderator-a", Bytes("fido7-some-group@fido7.org"), Bytes("fido7.some.group")));
        Assert.False(auth.TryAuthorizeApproval(
            "moderator-a",
            ["fido7-some-group@fido7.org"],
            ["fido7.some.group"],
            out var detail));
        Assert.Equal("unauthorized moderator principal", detail);
    }

    [Fact]
    public void RoutingOnlyEntry_CompileDoesNotDropMissingUsername()
    {
        var auth = Create(new ModeratorMappingOptions { Pattern = "fido7.*", Address = "%s@fido7.org" });
        Assert.True(auth.TryResolve(Bytes("fido7.announce"), out var identity));
        Assert.Equal("fido7-announce@fido7.org", identity.Address);
        Assert.Equal(string.Empty, identity.Username);
    }

    private static ConfiguredModeratorAuthorization InnRouting() =>
        Create(
            new ModeratorMappingOptions { Pattern = "fido7.*", Address = "%s@fido7.org" },
            new ModeratorMappingOptions { Pattern = "fj.*", Address = "%s@moderators.fj-news.org" },
            new ModeratorMappingOptions { Pattern = "medlux.*", Address = "%s@news.medlux.ru" },
            new ModeratorMappingOptions { Pattern = "nl.*", Address = "%s@nl.news-admin.org" },
            new ModeratorMappingOptions { Pattern = "perl.*", Address = "news-moderator-%s@perl.org" },
            new ModeratorMappingOptions { Pattern = "relcom.*", Address = "%s@moderators.relcom.ru" },
            new ModeratorMappingOptions { Pattern = "si.*", Address = "%s@arnes.si" },
            new ModeratorMappingOptions { Pattern = "*", Address = "%s@moderators.isc.org" });

    private static string AddressFor(string group) =>
        group.StartsWith("fido7.", StringComparison.Ordinal) ? "%s@fido7.org"
        : group.StartsWith("perl.", StringComparison.Ordinal) ? "news-moderator-%s@perl.org"
        : "%s@moderators.isc.org";

    private static ConfiguredModeratorAuthorization Standard() =>
        Create(
            new ModeratorMappingOptions { Pattern = "group.a", Address = "moderator-a@example.com", Username = MapNntpAuthenticationProvider.ModeratorA },
            new ModeratorMappingOptions { Pattern = "group.b", Address = "moderator-b@example.com", Username = MapNntpAuthenticationProvider.ModeratorB });

    private static ConfiguredModeratorAuthorization Create(params ModeratorMappingOptions[] mappings) =>
        new(mappings);

    private static byte[] Bytes(string value) => Encoding.ASCII.GetBytes(value);
}
