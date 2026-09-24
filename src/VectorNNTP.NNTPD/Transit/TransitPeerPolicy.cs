using VectorNNTP.NNTPD.Configuration;

namespace VectorNNTP.NNTPD.Transit;

/// <summary>
/// Immutable policy snapshot for one named Transit peer.
/// </summary>
/// <remarks>
/// Do not log <see cref="Password"/>. Use <see cref="HasPeerCredentials"/> in diagnostics.
/// </remarks>
public sealed class TransitPeerPolicy
{
    /// <summary>Maximum accepted inbound or outbound connection limit.</summary>
    public const int MaxConnectionLimit = 4096;

    internal TransitPeerPolicy(
        string identifier,
        string peerName,
        int maxIncomingConnections,
        int maxOutgoingConnections,
        IReadOnlyList<IpPrefix> literalPrefixes,
        IReadOnlyList<string> dnsHostnames,
        IReadOnlyList<TransitConnectEndpoint> connectTo,
        string username,
        string password,
        TransitSslMode ssl,
        NewsfeedsPattern patterns,
        bool deferOnDuplicate,
        string pathToken,
        long maxSize,
        TransitMessageTypes messageTypes)
    {
        Identifier = identifier;
        PeerName = peerName;
        MaxIncomingConnections = maxIncomingConnections;
        MaxOutgoingConnections = maxOutgoingConnections;
        LiteralPrefixes = literalPrefixes;
        DnsHostnames = dnsHostnames;
        ConnectTo = connectTo;
        Username = username;
        Password = password;
        Ssl = ssl;
        Patterns = patterns;
        DeferOnDuplicate = deferOnDuplicate;
        PathToken = pathToken;
        MaxSize = maxSize;
        MessageTypes = messageTypes;
    }

    /// <summary>
    /// Gets the stable protocol/machine identifier (Transit dictionary key).
    /// </summary>
    /// <remarks>Exact configured string. Not derived from <see cref="PeerName"/>.</remarks>
    public string Identifier { get; }

    /// <summary>
    /// Gets the human-readable administrative display name.
    /// </summary>
    /// <remarks>Exact configured <c>PeerName</c>. Not a protocol argument.</remarks>
    public string PeerName { get; }

    /// <summary>Gets the inbound connection limit for this peer.</summary>
    public int MaxIncomingConnections { get; }

    /// <summary>Gets the future outbound connection limit for this peer.</summary>
    public int MaxOutgoingConnections { get; }

    /// <summary>Gets literal AllowFrom prefixes (addresses and CIDRs).</summary>
    public IReadOnlyList<IpPrefix> LiteralPrefixes { get; }

    /// <summary>Gets AllowFrom DNS hostnames (resolved separately).</summary>
    public IReadOnlyList<string> DnsHostnames { get; }

    /// <summary>Gets parsed ConnectTo endpoints (not connected).</summary>
    public IReadOnlyList<TransitConnectEndpoint> ConnectTo { get; }

    /// <summary>Gets the peer AUTHINFO username, or empty when credentials are not configured.</summary>
    public string Username { get; }

    /// <summary>
    /// Gets the peer AUTHINFO password, or empty when credentials are not configured.
    /// Never write this value to logs.
    /// </summary>
    public string Password { get; }

    /// <summary>Gets a value indicating whether both username and password are configured.</summary>
    public bool HasPeerCredentials => Username.Length > 0 && Password.Length > 0;

    /// <summary>Gets the canonical TLS mode.</summary>
    public TransitSslMode Ssl { get; }

    /// <summary>Gets the compiled newsfeeds-style Patterns expression.</summary>
    public NewsfeedsPattern Patterns { get; }

    /// <summary>Gets whether duplicate offers should later be deferred.</summary>
    public bool DeferOnDuplicate { get; }

    /// <summary>
    /// Gets the exact configured Path-header token (not consumed by routing yet).
    /// </summary>
    public string PathToken { get; }

    /// <summary>Gets the peer incoming-article size policy in bytes (not enforced yet).</summary>
    public long MaxSize { get; }

    /// <summary>Gets the configured message-type flags (classification not implemented).</summary>
    public TransitMessageTypes MessageTypes { get; }

    /// <inheritdoc />
    public override string ToString() => Identifier;

    /// <summary>Returns whether <paramref name="username"/> and <paramref name="password"/> match this peer.</summary>
    public bool CredentialsMatch(string username, string password)
    {
        ArgumentNullException.ThrowIfNull(username);
        ArgumentNullException.ThrowIfNull(password);
        return HasPeerCredentials
               && string.Equals(Username, username, StringComparison.Ordinal)
               && string.Equals(Password, password, StringComparison.Ordinal);
    }
}
