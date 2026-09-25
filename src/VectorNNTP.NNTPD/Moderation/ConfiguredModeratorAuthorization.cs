using System.Text;
using Microsoft.Extensions.Options;
using VectorNNTP.NNTPD.Configuration;
using VectorNNTP.NNTPD.Newsgroups;

namespace VectorNNTP.NNTPD.Moderation;

/// <summary>
/// Immutable first-match moderator catalogue compiled from <see cref="ModerationOptions"/>.
/// </summary>
/// <remarks>
/// Lookups do not query MySQL. The compiled table is safe for concurrent POST use.
/// Username comparison is ordinal (same as AUTHINFO). Approved identities compare
/// ASCII case-insensitively after mailbox extraction.
/// </remarks>
public sealed class ConfiguredModeratorAuthorization : IModeratorAuthorization
{
    private readonly ModeratorRule[] _rules;

    /// <summary>Initializes a new instance from bound options.</summary>
    public ConfiguredModeratorAuthorization(IOptions<ModerationOptions> options)
        : this(options?.Value.Moderators)
    {
    }

    /// <summary>Initializes a new instance from an explicit mapping list.</summary>
    public ConfiguredModeratorAuthorization(IReadOnlyList<ModeratorMappingOptions>? mappings)
    {
        _rules = Compile(mappings);
    }

    /// <inheritdoc />
    public bool IsAuthenticatedModerator(string? authenticatedUsername)
    {
        if (string.IsNullOrEmpty(authenticatedUsername))
        {
            return false;
        }

        for (var i = 0; i < _rules.Length; i++)
        {
            if (string.Equals(_rules[i].Username, authenticatedUsername, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <inheritdoc />
    public bool TryResolve(ReadOnlySpan<byte> newsgroup, out ModeratorIdentity identity)
    {
        for (var i = 0; i < _rules.Length; i++)
        {
            var rule = _rules[i];
            if (NntpWildmat.IsMatchValidated(newsgroup, rule.PatternBytes))
            {
                identity = new ModeratorIdentity(rule.Pattern, rule.Address, rule.Username);
                return true;
            }
        }

        identity = default;
        return false;
    }

    /// <inheritdoc />
    public bool CanApprove(
        string? authenticatedUsername,
        ReadOnlySpan<byte> approvedIdentity,
        ReadOnlySpan<byte> newsgroup)
    {
        if (string.IsNullOrEmpty(authenticatedUsername)
            || !TryResolve(newsgroup, out var identity)
            || !string.Equals(authenticatedUsername, identity.Username, StringComparison.Ordinal))
        {
            return false;
        }

        return MailboxesEqual(approvedIdentity, identity.Address);
    }

    /// <inheritdoc />
    public bool TryAuthorizeApproval(
        string? authenticatedUsername,
        ReadOnlySpan<string> approvedIdentities,
        ReadOnlySpan<string> moderatedGroups,
        out string? failureDetail)
    {
        if (string.IsNullOrEmpty(authenticatedUsername))
        {
            failureDetail = "unauthenticated approval";
            return false;
        }

        if (approvedIdentities.IsEmpty)
        {
            failureDetail = "missing Approved";
            return false;
        }

        if (moderatedGroups.IsEmpty)
        {
            failureDetail = "no moderated groups";
            return false;
        }

        Span<byte> groupBytes = stackalloc byte[128];
        for (var i = 0; i < moderatedGroups.Length; i++)
        {
            var group = moderatedGroups[i];
            if (group.Length is < 1 or > 128)
            {
                failureDetail = "malformed newsgroup name";
                return false;
            }

            var written = Encoding.ASCII.GetBytes(group, groupBytes);
            if (!TryResolve(groupBytes[..written], out var identity))
            {
                failureDetail = "unresolved moderator";
                return false;
            }

            if (!string.Equals(authenticatedUsername, identity.Username, StringComparison.Ordinal))
            {
                failureDetail = "unauthorized moderator principal";
                return false;
            }

            if (!ContainsIdentity(approvedIdentities, identity.Address))
            {
                failureDetail = "Approved identity not authorized for group";
                return false;
            }
        }

        for (var i = 0; i < approvedIdentities.Length; i++)
        {
            if (!IdentityCoversAModeratedGroup(approvedIdentities[i], moderatedGroups))
            {
                failureDetail = "conflicting Approved";
                return false;
            }
        }

        failureDetail = null;
        return true;
    }

    private bool IdentityCoversAModeratedGroup(string approvedIdentity, ReadOnlySpan<string> moderatedGroups)
    {
        Span<byte> groupBytes = stackalloc byte[128];
        for (var i = 0; i < moderatedGroups.Length; i++)
        {
            var group = moderatedGroups[i];
            if (group.Length is < 1 or > 128)
            {
                continue;
            }

            var written = Encoding.ASCII.GetBytes(group, groupBytes);
            if (TryResolve(groupBytes[..written], out var identity)
                && MailboxesEqual(approvedIdentity, identity.Address))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsIdentity(ReadOnlySpan<string> identities, string expected)
    {
        for (var i = 0; i < identities.Length; i++)
        {
            if (MailboxesEqual(identities[i], expected))
            {
                return true;
            }
        }

        return false;
    }

    private static bool MailboxesEqual(ReadOnlySpan<char> left, ReadOnlySpan<char> right)
    {
        var a = Trim(left);
        var b = Trim(right);
        if (a.Length != b.Length)
        {
            return false;
        }

        for (var i = 0; i < a.Length; i++)
        {
            if (FoldChar(a[i]) != FoldChar(b[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool MailboxesEqual(ReadOnlySpan<byte> left, ReadOnlySpan<char> right)
    {
        var a = Trim(left);
        var b = Trim(right);
        if (a.Length != b.Length)
        {
            return false;
        }

        for (var i = 0; i < a.Length; i++)
        {
            if (FoldByte(a[i]) != FoldChar(b[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static ReadOnlySpan<char> Trim(ReadOnlySpan<char> value)
    {
        var start = 0;
        var end = value.Length;
        while (start < end && char.IsWhiteSpace(value[start]))
        {
            start++;
        }

        while (end > start && char.IsWhiteSpace(value[end - 1]))
        {
            end--;
        }

        return value[start..end];
    }

    private static ReadOnlySpan<byte> Trim(ReadOnlySpan<byte> value)
    {
        var start = 0;
        var end = value.Length;
        while (start < end && value[start] is (byte)' ' or (byte)'\t')
        {
            start++;
        }

        while (end > start && value[end - 1] is (byte)' ' or (byte)'\t')
        {
            end--;
        }

        return value[start..end];
    }

    private static char FoldChar(char c) =>
        c is >= 'a' and <= 'z' ? (char)(c - 32) : c;

    private static char FoldByte(byte b) =>
        (char)(b is >= (byte)'a' and <= (byte)'z' ? b - 32 : b);

    private static ModeratorRule[] Compile(IReadOnlyList<ModeratorMappingOptions>? mappings)
    {
        if (mappings is null || mappings.Count == 0)
        {
            return [];
        }

        var rules = new List<ModeratorRule>(mappings.Count);
        for (var i = 0; i < mappings.Count; i++)
        {
            var mapping = mappings[i];
            if (mapping is null)
            {
                continue;
            }

            var pattern = mapping.Pattern?.Trim() ?? string.Empty;
            var address = mapping.Address?.Trim() ?? string.Empty;
            var username = mapping.Username?.Trim() ?? string.Empty;
            if (pattern.Length == 0 || address.Length == 0 || username.Length == 0)
            {
                continue;
            }

            var patternBytes = Encoding.ASCII.GetBytes(pattern);
            if (!NntpWildmat.TryValidate(patternBytes))
            {
                continue;
            }

            rules.Add(new ModeratorRule(pattern, patternBytes, address, username));
        }

        return [.. rules];
    }

    private readonly record struct ModeratorRule(
        string Pattern,
        byte[] PatternBytes,
        string Address,
        string Username);
}
