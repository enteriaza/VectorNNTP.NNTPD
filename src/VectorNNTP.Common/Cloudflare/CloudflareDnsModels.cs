using System.Text.Json.Serialization;

namespace VectorNNTP.Common.Cloudflare
{
    /// <summary>Cloudflare DNS record type names used by this host.</summary>
    internal static class CloudflareDnsRecordTypes
    {
        /// <summary>IPv4 address record.</summary>
        public const string A = "A";

        /// <summary>IPv6 address record.</summary>
        public const string AAAA = "AAAA";

        /// <summary>Text record (used for ACME DNS-01 challenges).</summary>
        public const string TXT = "TXT";
    }

    /// <summary>A DNS record returned by the Cloudflare API.</summary>
    internal sealed class CloudflareDnsRecord
    {
        /// <summary>Gets or sets the Cloudflare record identifier.</summary>
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        /// <summary>Gets or sets the record type (for example <c>A</c> or <c>AAAA</c>).</summary>
        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;

        /// <summary>Gets or sets the DNS name.</summary>
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        /// <summary>Gets or sets the record content (IP address for A/AAAA).</summary>
        [JsonPropertyName("content")]
        public string Content { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the TTL when Cloudflare includes it (seconds; <c>1</c> means automatic).
        /// </summary>
        /// <remarks>
        /// Null means the field was absent. Managed A/AAAA reconciliation requires
        /// <see cref="CloudflareManagedDnsPolicy.ManagedTtl"/>.
        /// </remarks>
        [JsonPropertyName("ttl")]
        public int? Ttl { get; set; }

        /// <summary>
        /// Gets or sets whether the record is Cloudflare-proxied when the field is present.
        /// </summary>
        /// <remarks>
        /// Null means the field was absent. Managed A/AAAA reconciliation requires
        /// <see cref="CloudflareManagedDnsPolicy.ManagedProxied"/>.
        /// </remarks>
        [JsonPropertyName("proxied")]
        public bool? Proxied { get; set; }

        /// <summary>
        /// Gets or sets the zone identifier when Cloudflare includes it on the record object.
        /// </summary>
        /// <remarks>
        /// Current Cloudflare list/create/update schemas often omit this field; the request path zone
        /// is then authoritative. When present, it must match the requested zone.
        /// </remarks>
        [JsonPropertyName("zone_id")]
        public string? ZoneId { get; set; }
    }

    /// <summary>Request body for creating or updating an A/AAAA record.</summary>
    internal sealed class CloudflareDnsRecordWriteRequest
    {
        /// <summary>Gets or sets the record type.</summary>
        [JsonPropertyName("type")]
        public required string Type { get; set; }

        /// <summary>Gets or sets the DNS name.</summary>
        [JsonPropertyName("name")]
        public required string Name { get; set; }

        /// <summary>Gets or sets the record content.</summary>
        [JsonPropertyName("content")]
        public required string Content { get; set; }

        /// <summary>Gets or sets the TTL (seconds). Managed A/AAAA use <see cref="CloudflareManagedDnsPolicy.ManagedTtl"/>.</summary>
        [JsonPropertyName("ttl")]
        public int Ttl { get; set; } = CloudflareManagedDnsPolicy.ManagedTtl;

        /// <summary>Gets or sets whether the record is Cloudflare-proxied (must be false for NNTP).</summary>
        [JsonPropertyName("proxied")]
        public bool Proxied { get; set; } = CloudflareManagedDnsPolicy.ManagedProxied;
    }

    /// <summary>
    /// Cloudflare API v4 response envelope. <see cref="Success"/> false is an API failure even when HTTP status is 200.
    /// </summary>
    /// <typeparam name="T">Deserialized <c>result</c> payload.</typeparam>
    internal sealed class CloudflareApiResponse<T>
    {
        /// <summary>Gets or sets whether Cloudflare accepted the request. False is a failure even with HTTP 200.</summary>
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        /// <summary>Gets or sets Cloudflare error objects. Empty when the payload omits errors.</summary>
        [JsonPropertyName("errors")]
        public List<CloudflareApiError> Errors { get; set; } = [];

        /// <summary>Gets or sets Cloudflare informational messages. The DNS client does not treat these as success or failure.</summary>
        [JsonPropertyName("messages")]
        public List<CloudflareApiMessage> Messages { get; set; } = [];

        /// <summary>Gets or sets the operation payload. Null when the field is absent.</summary>
        [JsonPropertyName("result")]
        public T? Result { get; set; }

        /// <summary>Gets or sets list pagination metadata. Null when the field is absent; list calls then fail closed.</summary>
        [JsonPropertyName("result_info")]
        public CloudflareResultInfo? ResultInfo { get; set; }
    }

    /// <summary>One object from a Cloudflare API <c>errors</c> array.</summary>
    internal sealed class CloudflareApiError
    {
        /// <summary>Gets or sets the Cloudflare error code. Auth codes 10000, 9109, 9106, and 6003 are treated as permanent on HTTP 2xx.</summary>
        [JsonPropertyName("code")]
        public int Code { get; set; }

        /// <summary>Gets or sets the Cloudflare error text. Diagnostics truncate it and do not log the API key.</summary>
        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;
    }

    /// <summary>One object from a Cloudflare API <c>messages</c> array.</summary>
    internal sealed class CloudflareApiMessage
    {
        /// <summary>Gets or sets the Cloudflare message code.</summary>
        [JsonPropertyName("code")]
        public int Code { get; set; }

        /// <summary>Gets or sets the Cloudflare message text.</summary>
        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;
    }

    /// <summary>
    /// Cloudflare <c>result_info</c> pagination object. List calls require <see cref="CloudflareResultInfo.TotalPages"/>
    /// and fail closed when it is missing.
    /// </summary>
    internal sealed class CloudflareResultInfo
    {
        /// <summary>Gets or sets the current page number when Cloudflare includes it.</summary>
        [JsonPropertyName("page")]
        public int? Page { get; set; }

        /// <summary>Gets or sets the page size Cloudflare reported. Null when absent. Listing completeness uses <see cref="TotalPages"/>, not this value.</summary>
        [JsonPropertyName("per_page")]
        public int? PerPage { get; set; }

        /// <summary>
        /// Gets or sets total pages. Null means the field was absent or not an integer — listing is incomplete.
        /// </summary>
        [JsonPropertyName("total_pages")]
        public int? TotalPages { get; set; }

        /// <summary>Gets or sets the count Cloudflare reported for the current page. Null when absent. Not used to decide listing completeness.</summary>
        [JsonPropertyName("count")]
        public int? Count { get; set; }

        /// <summary>Gets or sets the total record count Cloudflare reported. Null when absent. Not used to decide listing completeness.</summary>
        [JsonPropertyName("total_count")]
        public int? TotalCount { get; set; }
    }
}
