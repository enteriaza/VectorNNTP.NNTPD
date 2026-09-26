namespace VectorNNTP.BackFiller.Nntp;

/// <summary>
/// Named NNTP status codes used by provider retrieval.
/// Values are from RFC 3977 and RFC 4643; do not scatter raw literals at call sites.
/// </summary>
public static class NntpStatusCode
{
    /// <summary>RFC 3977 DATE success.</summary>
    public const int DateFollows = 111;

    /// <summary>RFC 3977 greeting: service available, posting allowed.</summary>
    public const int ServiceReadyPostingAllowed = 200;

    /// <summary>RFC 3977 greeting: service available, posting prohibited.</summary>
    public const int ServiceReadyPostingProhibited = 201;

    /// <summary>RFC 3977 ARTICLE success; multiline article follows.</summary>
    public const int ArticleFollows = 220;

    /// <summary>RFC 4643 AUTHINFO accepted.</summary>
    public const int AuthenticationAccepted = 281;

    /// <summary>RFC 4643 AUTHINFO USER: password required.</summary>
    public const int PasswordRequired = 381;

    /// <summary>RFC 3977: no newsgroup selected (ARTICLE by number).</summary>
    public const int NoNewsgroupSelected = 412;

    /// <summary>RFC 3977: current article number invalid.</summary>
    public const int CurrentArticleInvalid = 420;

    /// <summary>RFC 3977: no article with that number.</summary>
    public const int NoArticleWithNumber = 423;

    /// <summary>RFC 3977 ARTICLE by Message-ID: no such article.</summary>
    public const int NoArticleWithMessageId = 430;

    /// <summary>RFC 3977/4643: authentication required for the command.</summary>
    public const int AuthenticationRequired = 480;

    /// <summary>RFC 4643: authentication failed or rejected.</summary>
    public const int AuthenticationRejected = 481;

    /// <summary>RFC 4643: authentication commands out of sequence.</summary>
    public const int AuthenticationOutOfSequence = 482;

    /// <summary>RFC 3977: command unknown.</summary>
    public const int CommandUnknown = 500;

    /// <summary>RFC 3977: command syntax error.</summary>
    public const int CommandSyntaxError = 501;

    /// <summary>RFC 3977: command not permitted / service permanently unavailable.</summary>
    public const int CommandNotPermitted = 502;

    /// <summary>RFC 3977: program fault / temporary unavailability.</summary>
    public const int ProgramFault = 503;

    /// <summary>Returns whether <paramref name="code"/> is a usable NNTP greeting.</summary>
    public static bool IsServiceReadyGreeting(int code) =>
        code is ServiceReadyPostingAllowed or ServiceReadyPostingProhibited;

    /// <summary>Returns whether <paramref name="code"/> is an AUTHINFO credential failure.</summary>
    public static bool IsAuthenticationFailure(int code) =>
        code is AuthenticationRequired or AuthenticationRejected or AuthenticationOutOfSequence;

    /// <summary>Returns whether <paramref name="code"/> is a generic command rejection.</summary>
    public static bool IsCommandRejected(int code) =>
        code is CommandUnknown or CommandSyntaxError or CommandNotPermitted or ProgramFault;
}
