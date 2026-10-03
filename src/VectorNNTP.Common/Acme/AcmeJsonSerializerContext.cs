using System.Text.Json.Serialization;

namespace VectorNNTP.Common.Acme
{
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
        /// <summary>Wire field <c>account_uri</c>. Required. The ACME account Location URL.</summary>
        [JsonPropertyName("account_uri")]
        public required string AccountUri { get; init; }

        /// <summary>Wire field <c>directory_url</c>. Required. Directory URL the account was registered against.</summary>
        [JsonPropertyName("directory_url")]
        public required string DirectoryUrl { get; init; }

        /// <summary>
        /// Wire field <c>registration_body</c>. Required on write.
        /// Issuance currently stores an empty string. <see cref="AccountStore"/> treats a missing property on read as empty.
        /// </summary>
        [JsonPropertyName("registration_body")]
        public required string RegistrationBody { get; init; }
    }

    /// <summary>Persisted pending-registration metadata written before ACME account creation completes.</summary>
    internal sealed class AcmeAccountPendingPayload
    {
        /// <summary>Wire field <c>version</c>. <see cref="AccountStore"/> writes <c>1</c>. Omitted JSON deserializes as <c>0</c>.</summary>
        [JsonPropertyName("version")]
        public int Version { get; init; }

        /// <summary>Wire field <c>directory_url</c>. Required. Directory URL the pending key is bound to.</summary>
        [JsonPropertyName("directory_url")]
        public required string DirectoryUrl { get; init; }
    }

    /// <summary>One DNS-01 recovery file under <c>live/{fqdn}/dns01/</c>.</summary>
    internal sealed class Dns01ChallengeJournalPayload
    {
        /// <summary>Wire field <c>version</c>. Recovery accepts only <c>1</c>. Omitted JSON deserializes as <c>0</c>.</summary>
        [JsonPropertyName("version")]
        public int Version { get; init; }

        /// <summary>Wire field <c>entry_id</c>. Required. File name (without <c>.json</c>) must match this value.</summary>
        [JsonPropertyName("entry_id")]
        public required string EntryId { get; init; }

        /// <summary>Wire field <c>phase</c>. Required. Recovery accepts <c>creating</c> or <c>placed</c>.</summary>
        [JsonPropertyName("phase")]
        public required string Phase { get; init; }

        /// <summary>Wire field <c>zone_id</c>. Required. Cloudflare zone that owns the TXT record.</summary>
        [JsonPropertyName("zone_id")]
        public required string ZoneId { get; init; }

        /// <summary>Wire field <c>name</c>. Required. TXT record name (<c>_acme-challenge.</c> plus the identifier).</summary>
        [JsonPropertyName("name")]
        public required string Name { get; init; }

        /// <summary>Wire field <c>content</c>. Required. TXT RDATA (DNS-01 digest).</summary>
        [JsonPropertyName("content")]
        public required string Content { get; init; }

        /// <summary>
        /// Wire field <c>record_id</c>. <see langword="null"/> while <c>phase</c> is <c>creating</c>.
        /// Required when <c>phase</c> is <c>placed</c>.
        /// </summary>
        [JsonPropertyName("record_id")]
        public string? RecordId { get; init; }

        /// <summary>Wire field <c>fqdn</c>. Required. Must equal the certificate FQDN that owns the recovery directory.</summary>
        [JsonPropertyName("fqdn")]
        public required string Fqdn { get; init; }

        /// <summary>
        /// Wire field <c>transaction_id</c>. <see langword="null"/> when no journal transaction was active at placement.
        /// Recovery attaches the removal event to this id when the journal still contains it.
        /// </summary>
        [JsonPropertyName("transaction_id")]
        public string? TransactionId { get; init; }
    }

    /// <summary>On-disk wire shape for <see cref="AcmeTransactionJournal"/> (timestamps as formatted strings).</summary>
    internal sealed class AcmeJournalWireDocument
    {
        /// <summary>Wire field <c>version</c>. <see cref="AcmeTransactionJournal.SchemaVersion"/> is <c>1</c>. Omitted JSON deserializes as <c>0</c>.</summary>
        [JsonPropertyName("version")]
        public int Version { get; init; }

        /// <summary>Wire field <c>fqdn</c>. Required. Must match the journal file's certificate FQDN.</summary>
        [JsonPropertyName("fqdn")]
        public required string Fqdn { get; init; }

        /// <summary>Wire field <c>transactions</c>. Required on write. Empty when no requests have been recorded.</summary>
        [JsonPropertyName("transactions")]
        public required List<AcmeJournalWireTransaction> Transactions { get; init; }

        /// <summary>
        /// Wire field <c>unattributed_events</c>. Required on write.
        /// Empty when every recovery event was attached to a known transaction.
        /// </summary>
        [JsonPropertyName("unattributed_events")]
        public required List<AcmeJournalWireEvent> UnattributedEvents { get; init; }
    }

    /// <summary>Wire transaction object inside <see cref="AcmeJournalWireDocument"/>.</summary>
    internal sealed class AcmeJournalWireTransaction
    {
        /// <summary>Wire field <c>id</c>. Required. Transaction id from <see cref="AcmeTransactionJournal.BeginTransaction"/>.</summary>
        [JsonPropertyName("id")]
        public required string Id { get; init; }

        /// <summary>Wire field <c>started_at</c>. Required. Round-trip UTC timestamp (<c>o</c> format).</summary>
        [JsonPropertyName("started_at")]
        public required string StartedAt { get; init; }

        /// <summary>Wire field <c>completed_at</c>. <see langword="null"/> while the transaction is in progress.</summary>
        [JsonPropertyName("completed_at")]
        public string? CompletedAt { get; init; }

        /// <summary>Wire field <c>identifiers</c>. Required. Requested DNS names. Empty strings are dropped on read.</summary>
        [JsonPropertyName("identifiers")]
        public required List<string> Identifiers { get; init; }

        /// <summary>Wire field <c>directory_url</c>. Required. ACME directory URL recorded for the request.</summary>
        [JsonPropertyName("directory_url")]
        public required string DirectoryUrl { get; init; }

        /// <summary>
        /// Wire field <c>status</c>. Required.
        /// One of <see cref="AcmeTransactionJournal.StatusInProgress"/>, <see cref="AcmeTransactionJournal.StatusSucceeded"/>,
        /// or <see cref="AcmeTransactionJournal.StatusFailed"/>.
        /// </summary>
        [JsonPropertyName("status")]
        public required string Status { get; init; }

        /// <summary>Wire field <c>events</c>. Required. Lifecycle events in write order.</summary>
        [JsonPropertyName("events")]
        public required List<AcmeJournalWireEvent> Events { get; init; }

        /// <summary>Wire field <c>serial_number</c>. <see langword="null"/> until success inspection stores the leaf serial.</summary>
        [JsonPropertyName("serial_number")]
        public string? SerialNumber { get; init; }

        /// <summary>Wire field <c>thumbprint</c>. <see langword="null"/> until success inspection stores the leaf thumbprint.</summary>
        [JsonPropertyName("thumbprint")]
        public string? Thumbprint { get; init; }

        /// <summary>Wire field <c>not_before</c>. <see langword="null"/> until a certificate timestamp is known. Round-trip UTC text.</summary>
        [JsonPropertyName("not_before")]
        public string? NotBefore { get; init; }

        /// <summary>Wire field <c>not_after</c>. <see langword="null"/> until a certificate timestamp is known. Round-trip UTC text.</summary>
        [JsonPropertyName("not_after")]
        public string? NotAfter { get; init; }

        /// <summary>Wire field <c>generation_id</c>. <see langword="null"/> until the live generation that holds the certificate is recorded.</summary>
        [JsonPropertyName("generation_id")]
        public string? GenerationId { get; init; }

        /// <summary>Wire field <c>failure_category</c>. <see langword="null"/> on success and while in progress. Cleared when a later success completes the same id.</summary>
        [JsonPropertyName("failure_category")]
        public string? FailureCategory { get; init; }

        /// <summary>Wire field <c>failure_detail</c>. <see langword="null"/> on success and while in progress. Sanitized text when failed.</summary>
        [JsonPropertyName("failure_detail")]
        public string? FailureDetail { get; init; }
    }

    /// <summary>Wire event object inside journal transactions / unattributed events.</summary>
    internal sealed class AcmeJournalWireEvent
    {
        /// <summary>Wire field <c>type</c>. Required. One of the <c>AcmeTransactionJournal.Event*</c> constants.</summary>
        [JsonPropertyName("type")]
        public required string Type { get; init; }

        /// <summary>Wire field <c>at</c>. Required. Round-trip UTC timestamp (<c>o</c> format).</summary>
        [JsonPropertyName("at")]
        public required string At { get; init; }

        /// <summary>Wire field <c>record_name</c>. <see langword="null"/> when the event is not a DNS-01 record event.</summary>
        [JsonPropertyName("record_name")]
        public string? RecordName { get; init; }

        /// <summary>Wire field <c>zone_id</c>. <see langword="null"/> when the event has no Cloudflare zone.</summary>
        [JsonPropertyName("zone_id")]
        public string? ZoneId { get; init; }

        /// <summary>Wire field <c>record_id</c>. <see langword="null"/> when the Cloudflare record id is unknown.</summary>
        [JsonPropertyName("record_id")]
        public string? RecordId { get; init; }

        /// <summary>Wire field <c>txt_content</c>. <see langword="null"/> when the event has no TXT RDATA.</summary>
        [JsonPropertyName("txt_content")]
        public string? TxtContent { get; init; }

        /// <summary>Wire field <c>generation_id</c>. <see langword="null"/> when the event is not a persist or promote event.</summary>
        [JsonPropertyName("generation_id")]
        public string? GenerationId { get; init; }

        /// <summary>Wire field <c>not_before</c>. <see langword="null"/> when the event has no certificate validity. Round-trip UTC text.</summary>
        [JsonPropertyName("not_before")]
        public string? NotBefore { get; init; }

        /// <summary>Wire field <c>not_after</c>. <see langword="null"/> when the event has no certificate validity. Round-trip UTC text.</summary>
        [JsonPropertyName("not_after")]
        public string? NotAfter { get; init; }

        /// <summary>Wire field <c>failure_category</c>. <see langword="null"/> except on <see cref="AcmeTransactionJournal.EventCertificateIssuanceFailed"/>.</summary>
        [JsonPropertyName("failure_category")]
        public string? FailureCategory { get; init; }

        /// <summary>Wire field <c>failure_detail</c>. <see langword="null"/> except on <see cref="AcmeTransactionJournal.EventCertificateIssuanceFailed"/>.</summary>
        [JsonPropertyName("failure_detail")]
        public string? FailureDetail { get; init; }

        /// <summary>Wire field <c>recovery_entry_id</c>. <see langword="null"/> except on recovered DNS-01 removals.</summary>
        [JsonPropertyName("recovery_entry_id")]
        public string? RecoveryEntryId { get; init; }
    }
}
