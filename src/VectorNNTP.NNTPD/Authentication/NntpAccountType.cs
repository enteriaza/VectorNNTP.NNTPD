namespace VectorNNTP.NNTPD.Authentication;

/// <summary>
/// Billing/enforcement model from <c>nntpusers.account_type</c>.
/// </summary>
/// <remarks>
/// This is not a reader-versus-transit role. Successful reader authentication grants
/// reader and posting privileges for both types. Rate and byte enforcement belong to
/// a later accounting subsystem; authentication only exposes the policy.
/// </remarks>
public enum NntpAccountType
{
    /// <summary><c>R</c>: aggregate outbound rate (SI Mbps) across concurrent authenticated sessions.</summary>
    RateLimited,

    /// <summary><c>B</c> (and any value other than <c>R</c>/<c>r</c>): cluster-wide byte budget.</summary>
    ByteLimited,
}
