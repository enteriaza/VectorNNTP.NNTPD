using VectorNNTP.NNTPD.Transit;

namespace VectorNNTP.NNTPD.Session.Authentication;

/// <summary>Ordinal Transit peer USER/PASS check. No MySQL fall-through.</summary>
public sealed class TransitPeerAuthenticator : ITransitPeerAuthenticator
{
    /// <summary>Shared production instance.</summary>
    public static TransitPeerAuthenticator Instance { get; } = new();

    /// <inheritdoc />
    public NntpAuthenticationResult Authenticate(
        TransitPeerPolicy? policy,
        string username,
        string password,
        NntpAuthorization currentAuthorization)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentNullException.ThrowIfNull(password);
        ArgumentNullException.ThrowIfNull(currentAuthorization);
        if (policy is not null && policy.CredentialsMatch(username, password))
        {
            return NntpAuthenticationResult.Success(username, currentAuthorization);
        }

        return NntpAuthenticationResult.Failed;
    }
}
