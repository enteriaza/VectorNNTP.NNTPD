using System.Text.Json.Serialization;

namespace VectorNNTP.NNTPD.Cloudflare;

/// <summary>Cloudflare DNS record type names used by this host.</summary>
public static class CloudflareDnsRecordTypes
{
    /// <summary>IPv4 address record.</summary>
    public const string A = "A";

    /// <summary>IPv6 address record.</summary>
    public const string AAAA = "AAAA";

    /// <summary>Text record (used for ACME DNS-01 challenges).</summary>
    public const string TXT = "TXT";
}

/// <summary>A DNS record returned by the Cloudflare API.</summary>
public sealed class CloudflareDnsRecord
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
public sealed class CloudflareDnsRecordWriteRequest
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

internal sealed class CloudflareApiResponse<T>
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("errors")]
    public List<CloudflareApiError> Errors { get; set; } = [];

    [JsonPropertyName("messages")]
    public List<CloudflareApiMessage> Messages { get; set; } = [];

    [JsonPropertyName("result")]
    public T? Result { get; set; }

    [JsonPropertyName("result_info")]
    public CloudflareResultInfo? ResultInfo { get; set; }
}

internal sealed class CloudflareApiError
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}

internal sealed class CloudflareApiMessage
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}

internal sealed class CloudflareResultInfo
{
    /// <summary>Gets or sets the current page number when Cloudflare includes it.</summary>
    [JsonPropertyName("page")]
    public int? Page { get; set; }

    [JsonPropertyName("per_page")]
    public int? PerPage { get; set; }

    /// <summary>
    /// Gets or sets total pages. Null means the field was absent or not an integer — listing is incomplete.
    /// </summary>
    [JsonPropertyName("total_pages")]
    public int? TotalPages { get; set; }

    [JsonPropertyName("count")]
    public int? Count { get; set; }

    [JsonPropertyName("total_count")]
    public int? TotalCount { get; set; }
}
