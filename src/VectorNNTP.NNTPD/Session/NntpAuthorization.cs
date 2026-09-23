using VectorNNTP.NNTPD.Transit;

namespace VectorNNTP.NNTPD.Session;

/// <summary>
/// Immutable authorization snapshot for an NNTP session.
/// </summary>
/// <remarks>
/// Authentication and authorization are distinct: a session may be authenticated without
/// reader, transit, posting, or streaming privileges. Production default for new sessions is
/// unauthenticated with <see cref="StreamingPermitted"/> false. A Transit AllowFrom match
/// retains <see cref="TransitPeerName"/> and <see cref="TransitPeerPolicy"/>.
/// </remarks>
public sealed class NntpAuthorization
{
    /// <summary>Default privileges before authentication (no reader/transit/posting/streaming).</summary>
    public static NntpAuthorization Unauthenticated { get; } = new(
        isAuthenticated: false,
        authorizedReader: false,
        authorizedTransit: false,
        postingPermitted: false,
        streamingPermitted: false);

    /// <summary>
    /// Unauthenticated transit/streaming peer privileges (ACL match).
    /// Does not grant reader access, posting, or <see cref="IsAuthenticated"/>.
    /// Nameless; production identification uses <see cref="ForTransitPeer"/>.
    /// </summary>
    public static NntpAuthorization TrustedTransitPeer { get; } = new(
        isAuthenticated: false,
        authorizedReader: false,
        authorizedTransit: true,
        postingPermitted: false,
        streamingPermitted: true);

    /// <summary>Initializes a new instance of the <see cref="NntpAuthorization"/> class.</summary>
    public NntpAuthorization(
        bool isAuthenticated,
        bool authorizedReader,
        bool authorizedTransit,
        bool postingPermitted,
        bool streamingPermitted,
        string? transitPeerName = null,
        TransitPeerPolicy? transitPeerPolicy = null)
    {
        IsAuthenticated = isAuthenticated;
        AuthorizedReader = authorizedReader;
        AuthorizedTransit = authorizedTransit;
        PostingPermitted = postingPermitted;
        StreamingPermitted = streamingPermitted;
        TransitPeerName = transitPeerName;
        TransitPeerPolicy = transitPeerPolicy;
    }

    /// <summary>Creates transit/streaming privileges bound to a named peer policy.</summary>
    public static NntpAuthorization ForTransitPeer(TransitPeerPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return new(
            isAuthenticated: false,
            authorizedReader: false,
            authorizedTransit: true,
            postingPermitted: false,
            streamingPermitted: true,
            transitPeerName: policy.Name,
            transitPeerPolicy: policy);
    }

    /// <summary>Gets a value indicating whether the peer has completed authentication.</summary>
    public bool IsAuthenticated { get; }

    /// <summary>Gets a value indicating whether reader commands are authorized.</summary>
    public bool AuthorizedReader { get; }

    /// <summary>Gets a value indicating whether transit/streaming feed commands are authorized.</summary>
    public bool AuthorizedTransit { get; }

    /// <summary>Gets a value indicating whether <c>POST</c> is permitted (affects greeting / MODE READER).</summary>
    public bool PostingPermitted { get; }

    /// <summary>Gets a value indicating whether streaming feed mode may be entered.</summary>
    public bool StreamingPermitted { get; }

    /// <summary>Gets the configured Transit peer name when this session was identified as a peer.</summary>
    public string? TransitPeerName { get; }

    /// <summary>Gets the identified Transit peer policy, or <see langword="null"/> when not a named peer.</summary>
    public TransitPeerPolicy? TransitPeerPolicy { get; }

    /// <summary>Returns a copy with updated flags, preserving Transit peer identity.</summary>
    public NntpAuthorization With(
        bool? isAuthenticated = null,
        bool? authorizedReader = null,
        bool? authorizedTransit = null,
        bool? postingPermitted = null,
        bool? streamingPermitted = null) =>
        new(
            isAuthenticated ?? IsAuthenticated,
            authorizedReader ?? AuthorizedReader,
            authorizedTransit ?? AuthorizedTransit,
            postingPermitted ?? PostingPermitted,
            streamingPermitted ?? StreamingPermitted,
            TransitPeerName,
            TransitPeerPolicy);
}
