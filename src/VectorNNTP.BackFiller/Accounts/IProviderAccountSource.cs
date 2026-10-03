namespace VectorNNTP.BackFiller.Accounts;

/// <summary>
/// Control-plane query for provider accounts. Not used on the Article Work request path.
/// </summary>
internal interface IProviderAccountSource
{
    /// <summary>Loads account rows for the configured BackFiller server id.</summary>
    /// <param name="cancellationToken">Token the implementation observes while the query runs.</param>
    /// <returns>Parsed rows for that server id, before backbone and session validation.</returns>
    Task<IReadOnlyList<ProviderAccountRow>> QueryAsync(CancellationToken cancellationToken);
}
