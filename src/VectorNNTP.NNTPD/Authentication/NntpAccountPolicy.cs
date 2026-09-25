namespace VectorNNTP.NNTPD.Authentication;

/// <summary>
/// Authenticated account policy copied from <c>nntpusers</c> after a successful credential check.
/// </summary>
/// <remarks>
/// Session and source-IP limits are enforced by
/// <see cref="VectorNNTP.NNTPD.SessionState.ISessionStateTracker"/>.
/// <see cref="RateLimitMbps"/> and <see cref="ByteLimit"/> are exposed for traffic accounting
/// and are not enforced by the authentication subsystem.
/// </remarks>
public sealed class NntpAccountPolicy
{
    /// <summary>Initializes a new account policy snapshot.</summary>
    public NntpAccountPolicy(
        string username,
        NntpAccountType accountType,
        int rateLimitMbps,
        long byteLimit,
        int sessionLimit,
        int srcIpLimit,
        string customerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        Username = username;
        AccountType = accountType;
        RateLimitMbps = rateLimitMbps;
        ByteLimit = byteLimit;
        SessionLimit = sessionLimit;
        SrcIpLimit = srcIpLimit;
        CustomerId = customerId ?? string.Empty;
    }

    /// <summary>Gets the plaintext wire username.</summary>
    public string Username { get; }

    /// <summary>Gets the billing/enforcement model.</summary>
    public NntpAccountType AccountType { get; }

    /// <summary>Gets <c>account_rate_limit</c> in decimal SI megabits per second. <c>0</c> is unlimited.</summary>
    public int RateLimitMbps { get; }

    /// <summary>
    /// Gets the AUTHINFO-time snapshot of <c>account_byte_limit</c>.
    /// For B accounts this is remaining bytes at last user-record load: <c>0</c> is
    /// exhausted, not unlimited. Live remaining is observed by
    /// <see cref="VectorNNTP.NNTPD.SessionState.BytesAccounting.IAccountByteAccountant"/> and must not
    /// be taken from this cached snapshot. R accounts do not participate in byte accounting.
    /// </summary>
    public long ByteLimit { get; }

    /// <summary>Gets the cluster-wide authenticated session cap. <c>0</c> is unlimited.</summary>
    public int SessionLimit { get; }

    /// <summary>Gets the cluster-wide distinct source-IP cap. <c>0</c> is unlimited.</summary>
    public int SrcIpLimit { get; }

    /// <summary>Gets the customer/tenant identifier.</summary>
    public string CustomerId { get; }

    /// <summary>Gets whether session or source-IP admission must run.</summary>
    public bool RequiresAdmission => SessionLimit > 0 || SrcIpLimit > 0;

    /// <summary>Maps a database <c>account_type</c> octet. Only <c>R</c>/<c>r</c> are rate-limited.</summary>
    public static NntpAccountType MapAccountType(char accountType) =>
        accountType is 'R' or 'r' ? NntpAccountType.RateLimited : NntpAccountType.ByteLimited;

    /// <summary>Builds policy from a validated user record.</summary>
    public static NntpAccountPolicy FromRecord(NntpUserRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return new(
            record.AccountName,
            MapAccountType(record.AccountType),
            record.RateLimit,
            record.ByteLimit,
            record.SessionLimit,
            record.SrcIpLimit,
            record.CustomerId);
    }
}
