using System.Text.Json.Serialization;

namespace VectorNNTP.NNTPD.Acme;

/// <summary>
/// Source-generated JSON metadata for ACME account and DNS-01 challenge journal payloads.
/// </summary>
/// <remarks>
/// Options match the historical reflection-based serializers:
/// indented output, default encoder (including <c>+</c> → <c>\u002B</c>), and explicit
/// snake_case property names via <see cref="JsonPropertyNameAttribute"/>.
/// </remarks>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AcmeAccountRegistrationPayload))]
[JsonSerializable(typeof(AcmeAccountPendingPayload))]
[JsonSerializable(typeof(Dns01ChallengeJournalPayload))]
[JsonSerializable(typeof(AcmeJournalWireDocument))]
internal partial class AcmeJsonSerializerContext : JsonSerializerContext;

/// <summary>Persisted <c>registration.json</c> body beside the ACME account key.</summary>
internal sealed class AcmeAccountRegistrationPayload
{
    [JsonPropertyName("account_uri")]
    public required string AccountUri { get; init; }

    [JsonPropertyName("directory_url")]
    public required string DirectoryUrl { get; init; }

    [JsonPropertyName("registration_body")]
    public required string RegistrationBody { get; init; }
}

/// <summary>Persisted pending-registration metadata written before ACME account creation completes.</summary>
internal sealed class AcmeAccountPendingPayload
{
    [JsonPropertyName("version")]
    public int Version { get; init; }

    [JsonPropertyName("directory_url")]
    public required string DirectoryUrl { get; init; }
}

/// <summary>One DNS-01 challenge journal file under <c>journal/dns01/</c>.</summary>
internal sealed class Dns01ChallengeJournalPayload
{
    [JsonPropertyName("version")]
    public int Version { get; init; }

    [JsonPropertyName("entry_id")]
    public required string EntryId { get; init; }

    [JsonPropertyName("phase")]
    public required string Phase { get; init; }

    [JsonPropertyName("zone_id")]
    public required string ZoneId { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("content")]
    public required string Content { get; init; }

    [JsonPropertyName("record_id")]
    public string? RecordId { get; init; }

    [JsonPropertyName("fqdn")]
    public required string Fqdn { get; init; }

    [JsonPropertyName("transaction_id")]
    public string? TransactionId { get; init; }
}

/// <summary>On-disk wire shape for <see cref="AcmeTransactionJournal"/> (timestamps as formatted strings).</summary>
internal sealed class AcmeJournalWireDocument
{
    [JsonPropertyName("version")]
    public int Version { get; init; }

    [JsonPropertyName("fqdn")]
    public required string Fqdn { get; init; }

    [JsonPropertyName("transactions")]
    public required List<AcmeJournalWireTransaction> Transactions { get; init; }

    [JsonPropertyName("unattributed_events")]
    public required List<AcmeJournalWireEvent> UnattributedEvents { get; init; }
}

/// <summary>Wire transaction object inside <see cref="AcmeJournalWireDocument"/>.</summary>
internal sealed class AcmeJournalWireTransaction
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("started_at")]
    public required string StartedAt { get; init; }

    [JsonPropertyName("completed_at")]
    public string? CompletedAt { get; init; }

    [JsonPropertyName("identifiers")]
    public required List<string> Identifiers { get; init; }

    [JsonPropertyName("directory_url")]
    public required string DirectoryUrl { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("events")]
    public required List<AcmeJournalWireEvent> Events { get; init; }

    [JsonPropertyName("serial_number")]
    public string? SerialNumber { get; init; }

    [JsonPropertyName("thumbprint")]
    public string? Thumbprint { get; init; }

    [JsonPropertyName("not_before")]
    public string? NotBefore { get; init; }

    [JsonPropertyName("not_after")]
    public string? NotAfter { get; init; }

    [JsonPropertyName("generation_id")]
    public string? GenerationId { get; init; }

    [JsonPropertyName("failure_category")]
    public string? FailureCategory { get; init; }

    [JsonPropertyName("failure_detail")]
    public string? FailureDetail { get; init; }
}

/// <summary>Wire event object inside journal transactions / unattributed events.</summary>
internal sealed class AcmeJournalWireEvent
{
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("at")]
    public required string At { get; init; }

    [JsonPropertyName("record_name")]
    public string? RecordName { get; init; }

    [JsonPropertyName("zone_id")]
    public string? ZoneId { get; init; }

    [JsonPropertyName("record_id")]
    public string? RecordId { get; init; }

    [JsonPropertyName("txt_content")]
    public string? TxtContent { get; init; }

    [JsonPropertyName("generation_id")]
    public string? GenerationId { get; init; }

    [JsonPropertyName("not_before")]
    public string? NotBefore { get; init; }

    [JsonPropertyName("not_after")]
    public string? NotAfter { get; init; }

    [JsonPropertyName("failure_category")]
    public string? FailureCategory { get; init; }

    [JsonPropertyName("failure_detail")]
    public string? FailureDetail { get; init; }

    [JsonPropertyName("recovery_entry_id")]
    public string? RecoveryEntryId { get; init; }
}
