namespace VectorNNTP.NNTPD.Session.Authentication;

/// <summary>
/// Verifies NNTP AUTHINFO USER/PASS credentials and returns identity plus authorization.
/// </summary>
/// <remarks>
/// Implementations must not log passwords. The session layer never stores the password after
/// this call returns. Whether AUTHINFO USER/PASS is allowed without TLS is a server policy
/// (<c>Nntpd:AllowCleartextAuth</c>), not a provider concern.
/// </remarks>
public interface INntpAuthenticationProvider
{
    /// <summary>Verifies username/password and returns authentication + authorization outcome.</summary>
    ValueTask<NntpAuthenticationResult> AuthenticateAsync(
        string username,
        string password,
        CancellationToken cancellationToken = default);
}
