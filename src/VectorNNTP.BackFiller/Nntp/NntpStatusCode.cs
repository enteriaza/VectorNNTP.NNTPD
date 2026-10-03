namespace VectorNNTP.BackFiller.Nntp;

/// <summary>
/// Named NNTP status codes used by provider retrieval.
/// Values are from RFC 3977, RFC 4642, and RFC 4643; do not scatter raw literals at call sites.
/// </summary>
internal static class NntpStatusCode
{
    /// <summary>RFC 3977 CAPABILITIES success; multiline capability list follows.</summary>
    internal const int CapabilityListFollows = 101;

    /// <summary>RFC 3977 DATE success.</summary>
    internal const int DateFollows = 111;

    /// <summary>RFC 3977 greeting: service available, posting allowed.</summary>
    internal const int ServiceReadyPostingAllowed = 200;

    /// <summary>RFC 3977 greeting: service available, posting prohibited.</summary>
    internal const int ServiceReadyPostingProhibited = 201;

    /// <summary>RFC 3977 ARTICLE success; multiline article follows.</summary>
    internal const int ArticleFollows = 220;

    /// <summary>RFC 4643 AUTHINFO accepted.</summary>
    internal const int AuthenticationAccepted = 281;

    /// <summary>RFC 4643 AUTHINFO USER: password required.</summary>
    internal const int PasswordRequired = 381;

    /// <summary>RFC 4642 STARTTLS: continue with TLS negotiation.</summary>
    internal const int ContinueWithTlsNegotiation = 382;

    /// <summary>RFC 3977: no newsgroup selected (ARTICLE by number).</summary>
    internal const int NoNewsgroupSelected = 412;

    /// <summary>RFC 3977: current article number invalid.</summary>
    internal const int CurrentArticleInvalid = 420;

    /// <summary>RFC 3977: no article with that number.</summary>
    internal const int NoArticleWithNumber = 423;

    /// <summary>RFC 3977 ARTICLE by Message-ID: no such article.</summary>
    internal const int NoArticleWithMessageId = 430;

    /// <summary>RFC 3977/4643: authentication required for the command.</summary>
    internal const int AuthenticationRequired = 480;

    /// <summary>RFC 4643: authentication failed or rejected.</summary>
    internal const int AuthenticationRejected = 481;

    /// <summary>RFC 4643: authentication commands out of sequence.</summary>
    internal const int AuthenticationOutOfSequence = 482;

    /// <summary>RFC 3977: command unknown.</summary>
    internal const int CommandUnknown = 500;

    /// <summary>RFC 3977: command syntax error.</summary>
    internal const int CommandSyntaxError = 501;

    /// <summary>RFC 3977: command not permitted / service permanently unavailable.</summary>
    internal const int CommandNotPermitted = 502;

    /// <summary>RFC 3977: program fault / temporary unavailability.</summary>
    internal const int ProgramFault = 503;

    /// <summary>Returns whether <paramref name="code"/> is a usable NNTP greeting.</summary>
    internal static bool IsServiceReadyGreeting(int code) =>
        code is ServiceReadyPostingAllowed or ServiceReadyPostingProhibited;

    /// <summary>Returns whether <paramref name="code"/> is an AUTHINFO credential failure.</summary>
    internal static bool IsAuthenticationFailure(int code) =>
        code is AuthenticationRequired or AuthenticationRejected or AuthenticationOutOfSequence;

    /// <summary>Returns whether <paramref name="code"/> is a generic command rejection.</summary>
    internal static bool IsCommandRejected(int code) =>
        code is CommandUnknown or CommandSyntaxError or CommandNotPermitted or ProgramFault;
}
