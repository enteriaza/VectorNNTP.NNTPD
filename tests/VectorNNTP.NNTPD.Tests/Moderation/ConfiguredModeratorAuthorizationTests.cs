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

    private static ConfiguredModeratorAuthorization Standard() =>
        Create(
            new ModeratorMappingOptions { Pattern = "group.a", Address = "moderator-a@example.com", Username = MapNntpAuthenticationProvider.ModeratorA },
            new ModeratorMappingOptions { Pattern = "group.b", Address = "moderator-b@example.com", Username = MapNntpAuthenticationProvider.ModeratorB });

    private static ConfiguredModeratorAuthorization Create(params ModeratorMappingOptions[] mappings) =>
        new(mappings);

    private static byte[] Bytes(string value) => Encoding.ASCII.GetBytes(value);
}
