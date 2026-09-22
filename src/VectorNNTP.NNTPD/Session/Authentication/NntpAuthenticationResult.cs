namespace VectorNNTP.NNTPD.Session.Authentication;

/// <summary>Result of a credential verification attempt.</summary>
public sealed class NntpAuthenticationResult
{
    /// <summary>Failed authentication (no identity, no authorization).</summary>
    public static NntpAuthenticationResult Failed { get; } = new(succeeded: false, username: null, authorization: null);

    private NntpAuthenticationResult(bool succeeded, string? username, NntpAuthorization? authorization)
    {
        Succeeded = succeeded;
        Username = username;
        Authorization = authorization;
    }

    /// <summary>Gets a value indicating whether credentials were accepted.</summary>
    public bool Succeeded { get; }

    /// <summary>Gets the authenticated identity username when <see cref="Succeeded"/> is <see langword="true"/>.</summary>
    public string? Username { get; }

    /// <summary>
    /// Gets authorization granted by the provider on success.
    /// Authentication success does not imply any particular privilege flags.
    /// </summary>
    public NntpAuthorization? Authorization { get; }

    /// <summary>Creates a successful result with explicit authorization (flags are provider-defined).</summary>
    public static NntpAuthenticationResult Success(string username, NntpAuthorization authorization)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentNullException.ThrowIfNull(authorization);
        return new(succeeded: true, username: username, authorization: authorization);
    }
}
