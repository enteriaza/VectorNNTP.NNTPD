using VectorNNTP.NNTPD.Transit;

namespace VectorNNTP.NNTPD.Session.Authentication;

/// <summary>Validates AUTHINFO USER/PASS against an identified Transit peer policy.</summary>
public interface ITransitPeerAuthenticator
{
    /// <summary>
    /// Authenticates <paramref name="username"/> / <paramref name="password"/> against
    /// <paramref name="policy"/>. On success, privileges remain
    /// <paramref name="currentAuthorization"/> (Transit identity; no reader/posting grant).
    /// </summary>
    NntpAuthenticationResult Authenticate(
        TransitPeerPolicy? policy,
        string username,
        string password,
        NntpAuthorization currentAuthorization);
}
