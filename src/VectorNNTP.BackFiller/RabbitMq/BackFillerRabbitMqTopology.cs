namespace VectorNNTP.BackFiller.RabbitMq;

/// <summary>
/// Canonical <c>backfiller.*</c> topology names consumed by Article Work.
/// </summary>
/// <remarks>
/// BackFiller declares durable fanout exchanges and quorum queues for each backbone that
/// becomes usable. This type does not declare, bind, or delete broker entities.
/// <c>cache.requests</c> is the NNTPD→StorageServer fleet article-presence fanout exchange
/// (not a BackFiller consume target and not a shared work queue).
/// Legacy <c>grabbers.*</c> names are out of scope.
/// </remarks>
internal static class BackFillerRabbitMqTopology
{
    /// <summary>Topology namespace prefix.</summary>
    internal const string Prefix = "backfiller";

    /// <summary>
    /// Provider backbone labels used to compose <c>backfiller.&lt;backbone&gt;</c> entity names.
    /// </summary>
    internal static readonly IReadOnlyList<string> ProviderBackbones =
    [
        "Abavia",
        "Altopia",
        "BaseIP",
        "Eweka",
        "Elbracht",
        "Giganews",
        "GTT",
        "Highwinds",
        "ItsHosted",
        "Novia",
        "UExpress",
        "UsenetNode1",
    ];

    /// <summary>
    /// Normalizes a topology entity name: trim, then invariant lower-case.
    /// </summary>
    /// <param name="entityName">Exchange, queue, routing-key, or backbone label.</param>
    /// <returns>The trimmed invariant-lowercase name.</returns>
    private static string Normalize(string entityName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityName);
        return entityName.Trim().ToLowerInvariant();
    }

    /// <summary>
    /// Builds <c>backfiller.{backbone}</c> after normalizing both parts.
    /// </summary>
    /// <param name="backbone">Unqualified provider backbone label.</param>
    /// <returns>The composed entity name.</returns>
    internal static string ComposeProviderEntity(string backbone)
    {
        var normalized = Normalize(backbone);
        return $"{Prefix}.{normalized}";
    }
}
