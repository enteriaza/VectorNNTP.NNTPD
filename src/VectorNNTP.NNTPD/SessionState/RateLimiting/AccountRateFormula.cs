namespace VectorNNTP.NNTPD.SessionState.RateLimiting;

/// <summary>
/// Converts <c>account_rate_limit</c> (decimal SI Mbps) to integer bytes/sec and
/// divides that aggregate by the cluster-wide active session count.
/// </summary>
/// <remarks>
/// <c>account_rate_limit = 0</c> is unlimited (existing policy). Division uses
/// integer floor so <c>sum(per-session caps) &lt;= account bytes/sec</c>.
/// The denominator is the live active session count, never
/// <c>account_session_limit</c>.
/// </remarks>
public static class AccountRateFormula
{
    /// <summary>Bytes per second in one decimal SI megabit (<c>1_000_000 / 8</c>).</summary>
    public const long BytesPerSecondPerMbps = 125_000;

    /// <summary>
    /// Account-wide cap in bytes/sec. <paramref name="rateMbps"/> <c>&lt;= 0</c> is unlimited
    /// and returns <c>0</c>.
    /// </summary>
    public static long AccountBytesPerSecond(int rateMbps)
    {
        if (rateMbps <= 0)
        {
            return 0;
        }

        return (long)rateMbps * BytesPerSecondPerMbps;
    }

    /// <summary>
    /// Per-session cap in bytes/sec: <c>floor(accountBytesPerSec / activeSessions)</c>.
    /// Returns <c>0</c> (unlimited / no allocation) when the account rate is unlimited
    /// or there are no active sessions. A positive rate that floors to <c>0</c> is
    /// not unlimited; use <see cref="EqualShareCap"/> on the write path.
    /// </summary>
    public static long PerSessionBytesPerSecond(int rateMbps, int activeSessions)
    {
        if (rateMbps <= 0 || activeSessions <= 0)
        {
            return 0;
        }

        return AccountBytesPerSecond(rateMbps) / activeSessions;
    }

    /// <summary>
    /// Equal-share cap for a positive rate. A floor of <c>0</c> is
    /// <see cref="BlockedBytesPerSecond"/> so the limiter cannot treat it as unlimited.
    /// </summary>
    public static long EqualShareCap(int rateMbps, int activeSessions)
    {
        if (rateMbps <= 0)
        {
            return 0;
        }

        var perSession = PerSessionBytesPerSecond(rateMbps, activeSessions);
        return perSession > 0 ? perSession : BlockedBytesPerSecond;
    }

    /// <summary>
    /// Returns whether <c>perSession * activeSessions</c> stays at or under the account cap.
    /// </summary>
    public static bool AggregateDoesNotExceed(int rateMbps, int activeSessions)
    {
        if (rateMbps <= 0 || activeSessions <= 0)
        {
            return true;
        }

        var perSession = PerSessionBytesPerSecond(rateMbps, activeSessions);
        return perSession * activeSessions <= AccountBytesPerSecond(rateMbps);
    }

    /// <summary>
    /// Bytes/sec a newly admitted session may take while remotes may still hold
    /// <c>floor(account / previousSessions)</c>. This is the unused remainder
    /// after that previous equal split. <c>0</c> means no residual; callers must
    /// not treat it as unlimited.
    /// </summary>
    public static long ConservativeJoinBytesPerSecond(int rateMbps, int previousActiveSessions)
    {
        if (rateMbps <= 0 || previousActiveSessions <= 0)
        {
            return 0;
        }

        return AccountBytesPerSecond(rateMbps) % previousActiveSessions;
    }

    /// <summary>
    /// Negative sentinel: block writes until the cap is replaced. Distinct from
    /// <c>0</c>, which remains unlimited.
    /// </summary>
    public const long BlockedBytesPerSecond = -1;

    /// <summary>Counts a stored cap toward a cluster aggregate. Blocked contributes 0; unlimited is 0.</summary>
    public static long AllocatedBytesPerSecond(long cap) => cap > 0 ? cap : 0;
}
