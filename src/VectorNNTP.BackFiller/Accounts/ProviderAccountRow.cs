namespace VectorNNTP.BackFiller.Accounts;

/// <summary>
/// One <c>nntpbackfilleraccounts</c> row after column parsing and before backbone/session validation.
/// Passwords must not be logged.
/// </summary>
internal sealed record ProviderAccountRow(
    Guid EntryId,
    string Backbone,
    string Hostname,
    byte KeepAliveSeconds,
    int MaxConnections,
    string Username,
    string Password,
    int Port,
    int ServerId,
    string UseSslRaw);

/// <summary>A row that was not published into the provider snapshot.</summary>
internal sealed record RejectedProviderAccountRow(string Backbone, string Reason);

/// <summary>Validated snapshot plus rejected rows from one query.</summary>
internal sealed record ProviderAccountMapResult(
    IReadOnlyList<Nntp.BackFillerProviderDefinition> Providers,
    IReadOnlyList<RejectedProviderAccountRow> Rejected);
