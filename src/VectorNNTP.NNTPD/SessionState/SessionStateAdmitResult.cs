namespace VectorNNTP.NNTPD.SessionState;

/// <summary>Cluster membership outcome for one combined session/source-IP admit attempt.</summary>
public enum SessionStateAdmitStatus
{
    /// <summary>Admitted; the source IP was already active.</summary>
    AcceptedExisting,

    /// <summary>Admitted; a new distinct source IP was established.</summary>
    AcceptedNew,

    /// <summary>Rejected: cluster <c>account_session_limit</c> would be exceeded.</summary>
    RejectedSessionLimit,

    /// <summary>Rejected: cluster <c>account_srcip_limit</c> would be exceeded.</summary>
    RejectedSourceLimit,

    /// <summary>Redis (or the membership store) was unavailable. Fail closed.</summary>
    Unavailable,
}

/// <summary>Result of a cluster session/source-IP admit attempt.</summary>
public readonly struct SessionStateAdmitResult
{
    /// <summary>Initializes a membership admit result.</summary>
    public SessionStateAdmitResult(SessionStateAdmitStatus status)
    {
        Status = status;
    }

    /// <summary>Gets the membership outcome.</summary>
    public SessionStateAdmitStatus Status { get; }

    /// <summary>Gets whether membership was granted.</summary>
    public bool Accepted =>
        Status is SessionStateAdmitStatus.AcceptedExisting or SessionStateAdmitStatus.AcceptedNew;
}

/// <summary>Outcome of a node ownership lease renewal.</summary>
public enum SessionStateRenewStatus
{
    /// <summary>This owner's requested leases were refreshed.</summary>
    Renewed,

    /// <summary>At least one requested ownership field was gone or mismatched.</summary>
    Lost,

    /// <summary>The membership store could not be reached.</summary>
    Unavailable,
}
