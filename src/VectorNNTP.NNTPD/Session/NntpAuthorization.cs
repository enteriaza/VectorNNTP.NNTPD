namespace VectorNNTP.NNTPD.Session;

/// <summary>
/// Immutable authorization snapshot for an NNTP session.
/// </summary>
/// <remarks>
/// Authentication and authorization are distinct: a session may be authenticated without
/// reader, transit, posting, or streaming privileges. Production default for new sessions is
/// unauthenticated with <see cref="StreamingPermitted"/> false.
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

    /// <summary>Initializes a new instance of the <see cref="NntpAuthorization"/> class.</summary>
    public NntpAuthorization(
        bool isAuthenticated,
        bool authorizedReader,
        bool authorizedTransit,
        bool postingPermitted,
        bool streamingPermitted)
    {
        IsAuthenticated = isAuthenticated;
        AuthorizedReader = authorizedReader;
        AuthorizedTransit = authorizedTransit;
        PostingPermitted = postingPermitted;
        StreamingPermitted = streamingPermitted;
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

    /// <summary>Returns a copy with updated flags.</summary>
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
            streamingPermitted ?? StreamingPermitted);
}
