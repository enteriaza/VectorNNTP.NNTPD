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
/// <param name="MinSessions">Sessions connected during controlled warmup. 0 means fully lazy.</param>
/// <param name="MaxSessions">Hard bound on concurrent sessions for this provider.</param>
public sealed record BackFillerProviderDefinition(
    string Backbone,
    string Host,
    int Port,
    bool UseTls,
    string? Username,
    string? Password,
    int MinSessions,
    int MaxSessions)
{
    /// <summary>Returns whether AUTHINFO should be attempted.</summary>
    public bool RequiresAuthentication =>
        !string.IsNullOrWhiteSpace(Username) || !string.IsNullOrWhiteSpace(Password);
}
