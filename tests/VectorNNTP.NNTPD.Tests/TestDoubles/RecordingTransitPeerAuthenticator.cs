using VectorNNTP.NNTPD.Session;
using VectorNNTP.NNTPD.Session.Authentication;
using VectorNNTP.NNTPD.Transit;

namespace VectorNNTP.NNTPD.Tests.TestDoubles;

/// <summary>Counts Transit AUTHINFO invocations. Never logs credentials.</summary>
internal sealed class RecordingTransitPeerAuthenticator : ITransitPeerAuthenticator
{
    public int AuthenticateCount { get; private set; }

    public NntpAuthenticationResult Authenticate(
        TransitPeerPolicy? policy,
        string username,
        string password,
        NntpAuthorization currentAuthorization)
    {
        AuthenticateCount++;
        return TransitPeerAuthenticator.Instance.Authenticate(policy, username, password, currentAuthorization);
    }
}
