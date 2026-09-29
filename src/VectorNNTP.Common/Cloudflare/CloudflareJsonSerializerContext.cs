using System.Text.Json.Serialization;

namespace VectorNNTP.NNTPD.Cloudflare;

/// <summary>
/// Source-generated JSON metadata for Cloudflare DNS API request/response envelopes.
/// </summary>
/// <remarks>
/// Mirrors the historical <see cref="CloudflareDnsClient"/> options:
/// case-insensitive property names on read, and omit nulls when writing request bodies.
/// Closed generic response types cover every <c>T</c> used by the client.
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CloudflareApiResponse<List<CloudflareDnsRecord>>))]
[JsonSerializable(typeof(CloudflareApiResponse<CloudflareDnsRecord>))]
[JsonSerializable(typeof(CloudflareApiResponse<CloudflareDeleteResult>))]
[JsonSerializable(typeof(CloudflareDnsRecordWriteRequest))]
internal partial class CloudflareJsonSerializerContext : JsonSerializerContext;

/// <summary>Minimal delete-result payload (<c>result.id</c>) when Cloudflare returns one.</summary>
internal sealed class CloudflareDeleteResult
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }
}
