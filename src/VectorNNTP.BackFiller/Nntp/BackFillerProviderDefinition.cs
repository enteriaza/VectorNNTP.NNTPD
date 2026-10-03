namespace VectorNNTP.BackFiller.Nntp;

/// <summary>
/// Explicit provider identity and session-capacity for one backbone.
/// Phase 8 publishes these from MySQL <c>nntpbackfilleraccounts</c>. Tests may construct them directly.
/// </summary>
/// <param name="Backbone">Provider backbone label (queue context).</param>
/// <param name="Host">Upstream NNTP hostname or address.</param>
/// <param name="Port">Upstream NNTP port.</param>
/// <param name="UseTls">Whether the session uses implicit TLS from connect (old <c>usessl</c>).</param>
/// <param name="Username">AUTHINFO USER value, or null when authentication is not configured.</param>
/// <param name="Password">AUTHINFO PASS value, or null when authentication is not configured.</param>
/// <param name="MinSessions">Leftover port field. Not used for spawn; MaxSessions is the eager desired count.</param>
/// <param name="MaxSessions">Desired eager NNTP session count and hard concurrent bound (MySQL maxconnections).</param>
/// <param name="KeepAliveSeconds">
/// Per-account idle interval from MySQL <c>nntpbackfilleraccounts.keepalive</c>.
/// When greater than zero, an idle pooled session issues RFC 3977 DATE on that cadence.
/// Zero disables DATE keepalive.
/// </param>
internal sealed record BackFillerProviderDefinition(
    string Backbone,
    string Host,
    int Port,
    bool UseTls,
    string? Username,
    string? Password,
    int MinSessions,
    int MaxSessions,
    byte KeepAliveSeconds = 0)
{
    /// <summary>Returns whether AUTHINFO should be attempted.</summary>
    internal bool RequiresAuthentication =>
        !string.IsNullOrWhiteSpace(Username) || !string.IsNullOrWhiteSpace(Password);

    /// <summary>Returns whether idle DATE keepalive is enabled for this provider.</summary>
    internal bool DateKeepAliveEnabled => KeepAliveSeconds > 0;

    /// <summary>
    /// Returns whether <paramref name="previous"/> differs only by a lower <see cref="MaxSessions"/>.
    /// Host, port, TLS, credentials, and keepalive still require pool replacement.
    /// </summary>
    internal bool IsMaxSessionsShrinkOf(BackFillerProviderDefinition previous)
    {
        ArgumentNullException.ThrowIfNull(previous);
        return MaxSessions > 0
            && MaxSessions < previous.MaxSessions
            && HasSameConnectionIdentity(previous);
    }

    /// <summary>
    /// Returns whether upstream connection identity matches <paramref name="other"/>.
    /// <see cref="MinSessions"/> and <see cref="MaxSessions"/> are ignored.
    /// </summary>
    internal bool HasSameConnectionIdentity(BackFillerProviderDefinition other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return string.Equals(Backbone, other.Backbone, StringComparison.OrdinalIgnoreCase)
            && string.Equals(Host, other.Host, StringComparison.Ordinal)
            && Port == other.Port
            && UseTls == other.UseTls
            && string.Equals(Username, other.Username, StringComparison.Ordinal)
            && string.Equals(Password, other.Password, StringComparison.Ordinal)
            && KeepAliveSeconds == other.KeepAliveSeconds;
    }
}
