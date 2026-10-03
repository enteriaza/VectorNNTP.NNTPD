namespace VectorNNTP.BackFiller.Accounts
{
    /// <summary>
    /// One <c>nntpbackfilleraccounts</c> row after column parsing and before backbone/session validation.
    /// Passwords must not be logged.
    /// </summary>
    /// <param name="Backbone">Raw <c>backbone</c> text, before canonicalization.</param>
    /// <param name="Hostname">Raw <c>hostname</c> text.</param>
    /// <param name="KeepAliveSeconds">Parsed <c>keepalive</c> tinyint.</param>
    /// <param name="MaxConnections"><c>maxconnections</c> integer as returned by the reader.</param>
    /// <param name="Username">Raw <c>username</c> text.</param>
    /// <param name="Password"><c>password</c> text. Secret; must not be logged.</param>
    /// <param name="Port"><c>port</c> integer as returned by the reader.</param>
    /// <param name="UseSslRaw">Raw <c>usessl</c> text, before <c>y</c>/<c>n</c> parsing.</param>
    /// <remarks>
    /// <c>entryid</c> and <c>serverid</c> are not projected. The query already filters by server id,
    /// and neither column is read by <see cref="ProviderAccountMapper"/>.
    /// </remarks>
    internal sealed record ProviderAccountRow(
        string Backbone,
        string Hostname,
        byte KeepAliveSeconds,
        int MaxConnections,
        string Username,
        string Password,
        int Port,
        string UseSslRaw);

    /// <summary>A row that was not published into the provider snapshot.</summary>
    /// <param name="Backbone">Backbone associated with the rejection. Map failures keep the raw value; duplicates use the canonical label.</param>
    /// <param name="Reason">Human-readable rejection reason. Not an exception.</param>
    internal sealed record RejectedProviderAccountRow(string Backbone, string Reason);

    /// <summary>Validated snapshot plus rejected rows from one query.</summary>
    /// <param name="Providers">Definitions accepted for publication. The first valid row for each canonical backbone wins.</param>
    /// <param name="Rejected">Rows excluded from <paramref name="Providers"/>.</param>
    internal sealed record ProviderAccountMapResult(
        IReadOnlyList<Nntp.BackFillerProviderDefinition> Providers,
        IReadOnlyList<RejectedProviderAccountRow> Rejected);
}
