using System.Net;

namespace VectorNNTP.NNTPD.Authentication;

/// <summary>
/// Atomically admits and releases concurrent authenticated reader sessions per account.
/// </summary>
/// <remarks>
/// <c>session_limit</c> 0 and <c>srcip_limit</c> 0 are unlimited.
/// <c>srcip_limit</c> is a cap on distinct source IPs that currently have at least one
/// admitted session, not a per-IP session cap.
/// </remarks>
public interface INntpSessionAdmissionTracker
{
    /// <summary>
    /// Attempts to admit <paramref name="sessionId"/> for <paramref name="accountName"/>.
    /// Re-admitting the same session id is idempotent.
    /// </summary>
    NntpSessionAdmissionResult TryAdmit(
        string accountName,
        string sessionId,
        IPAddress sourceAddress,
        int sessionLimit,
        int srcIpLimit);

    /// <summary>Releases a previously admitted session. Idempotent when the session is unknown.</summary>
    void Release(string accountName, string sessionId);
}
