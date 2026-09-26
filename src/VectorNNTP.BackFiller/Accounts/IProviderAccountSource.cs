namespace VectorNNTP.BackFiller.Accounts;

/// <summary>
/// Control-plane query for provider accounts. Not used on the Article Work request path.
/// </summary>
public interface IProviderAccountSource
{
    /// <summary>Loads account rows for the configured BackFiller server id.</summary>
    Task<IReadOnlyList<ProviderAccountRow>> QueryAsync(CancellationToken cancellationToken);
}
