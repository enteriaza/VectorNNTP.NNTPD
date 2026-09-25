using VectorNNTP.NNTPD.Networking.Proxy;

namespace VectorNNTP.NNTPD.Session.Commands.Posting;

/// <summary>Outcome of one streaming POST article receive.</summary>
internal enum StreamingPostReadStatus
{
    /// <summary>Terminator seen; stuffed queue wire is ready for History Peek and TryAdmit.</summary>
    Completed = 0,

    /// <summary>Peer disconnected or cancelled before the terminating dot line.</summary>
    Incomplete = 1,

    /// <summary>Destuffed article exceeded <c>Nntpd:MaxArticleSize</c>; remaining bytes were drained.</summary>
    TooLarge = 2,

    /// <summary>Header parse or POST validation failed; remaining bytes were drained when needed.</summary>
    Rejected = 3,
}

/// <summary>Inputs required to stream one POST article into a stuffed queue payload.</summary>
internal sealed class StreamingPostReadOptions
{
    /// <summary>Gets the destuffed POST size limit (<c>Nntpd:MaxArticleSize</c>).</summary>
    public required int MaxArticleSize { get; init; }

    /// <summary>Gets the clock used for the single injection timestamp.</summary>
    public required TimeProvider Time { get; init; }

    /// <summary>Gets the newsgroup posting policy.</summary>
    public required INewsgroupPostingPolicy NewsgroupPolicy { get; init; }

    /// <summary>Gets the server injection identity (<c>nntpd{ServerId:00}.{DnsSuffix}</c>).</summary>
    public required string InjectionIdentity { get; init; }

    /// <summary>Gets the session client identity used only inside protected <c>X-Trace</c>.</summary>
    public required ConnectionClientIdentity ClientIdentity { get; init; }

    /// <summary>Gets the configured <c>mail-complaints-to</c> mailbox.</summary>
    public required string MailComplaintsTo { get; init; }

    /// <summary>Gets the <c>X-Trace</c> protector, or <see langword="null"/> when unset.</summary>
    public IPostingTraceProtector? TraceProtector { get; init; }
}

/// <summary>Result of <see cref="StreamingPostArticleReader.ReadAsync"/>.</summary>
/// <param name="Status">Receive / validation status.</param>
/// <param name="Failure">Set when <paramref name="Status"/> is <see cref="StreamingPostReadStatus.Rejected"/> or TooLarge.</param>
/// <param name="Wire">Stuffed queue payload (terminator omitted) when Completed; otherwise empty.</param>
/// <param name="MessageId">Article Message-ID when known.</param>
/// <param name="Newsgroups">Validated newsgroup names when known.</param>
/// <param name="DestuffedSize">Logical destuffed client article size (stuffing dots excluded).</param>
/// <param name="InjectionUtc">Single captured UTC timestamp written into server-owned headers.</param>
internal readonly record struct StreamingPostReadResult(
    StreamingPostReadStatus Status,
    PostingFailure Failure,
    ReadOnlyMemory<byte> Wire,
    string? MessageId,
    string[] Newsgroups,
    int DestuffedSize,
    DateTimeOffset InjectionUtc);
