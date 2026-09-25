namespace VectorNNTP.NNTPD.Authentication;

/// <summary>Outcome of authenticated-session admission.</summary>
public enum NntpSessionAdmissionResult
{
    /// <summary>The session was admitted, or admission was skipped because both limits are 0.</summary>
    Success,

    /// <summary><c>account_session_limit</c> would be exceeded.</summary>
    MaxSessionsExceeded,

    /// <summary>A new distinct source IP would exceed <c>account_srcip_limit</c>.</summary>
    IpLimitExceeded,
}
