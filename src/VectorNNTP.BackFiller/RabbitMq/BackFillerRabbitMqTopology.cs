namespace VectorNNTP.BackFiller.RabbitMq;

/// <summary>
/// Canonical <c>backfiller.*</c> topology names consumed by later Article Work.
/// </summary>
/// <remarks>
/// Locked NNTPD already declares these durable fanout exchanges and quorum queues.
/// This type does not declare, bind, or delete broker entities. Legacy <c>grabbers.*</c>
/// names are out of scope. <c>backfiller.storage</c> is NNTPD-internal and is not a
/// BackFiller consume target.
/// </remarks>
public static class BackFillerRabbitMqTopology
{
    /// <summary>Topology namespace prefix.</summary>
    public const string Prefix = "backfiller";

    /// <summary>NNTPD-internal storage path. Not consumed by BackFiller.</summary>
    public const string StorageEntity = "backfiller.storage";

    /// <summary>
    /// Provider backbone labels used to compose <c>backfiller.&lt;backbone&gt;</c> entity names.
    /// </summary>
    public static readonly IReadOnlyList<string> ProviderBackbones =
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
    public static string Normalize(string entityName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityName);
        return entityName.Trim().ToLowerInvariant();
    }

    /// <summary>
    /// Builds <c>backfiller.{backbone}</c> after normalizing both parts.
    /// </summary>
    /// <param name="backbone">Unqualified provider backbone label.</param>
    /// <returns>The composed entity name.</returns>
    public static string ComposeProviderEntity(string backbone)
    {
        var normalized = Normalize(backbone);
        return $"{Prefix}.{normalized}";
    }
}
