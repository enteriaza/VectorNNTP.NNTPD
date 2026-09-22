namespace VectorNNTP.NNTPD.Session;

/// <summary>Validates NNTP message-id tokens used by transfer commands.</summary>
/// <remarks>
/// Minimal RFC 3977 shape check: <c>&lt;id-left@id-right&gt;</c> without full ABNF enforcement.
/// </remarks>
public static class NntpMessageId
{
    /// <summary>
    /// Returns whether <paramref name="value"/> looks like a usable message-id argument.
    /// </summary>
    public static bool IsWellFormed(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (value.Length < 5 || value.Length > 998)
        {
            return false;
        }

        if (value[0] != '<' || value[^1] != '>')
        {
            return false;
        }

        var at = value.IndexOf('@');
        return at > 1 && at < value.Length - 2;
    }
}
