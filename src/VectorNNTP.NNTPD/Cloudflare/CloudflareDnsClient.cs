using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VectorNNTP.NNTPD.Configuration;

namespace VectorNNTP.NNTPD.Cloudflare;

/// <summary>
/// HTTP client for the Cloudflare DNS Records API (v4).
/// </summary>
/// <remarks>
/// Authenticates with <c>Authorization: Bearer</c> using <see cref="NntpdOptions.CloudFlareApiKey"/>.
/// Never logs the API key or authorization headers. Treats <c>success: false</c> and non-success
/// HTTP statuses as failures. Transport failures and timeouts during mutations are marked
/// <see cref="CloudflareDnsException.IsOutcomeUncertain"/> because Cloudflare may already have applied them.
/// </remarks>
public sealed class CloudflareDnsClient : ICloudflareDnsClient
{
    internal const string HttpClientName = "CloudflareDns";
    private const int DefaultPerPage = 100;
    private const int MaxRateLimitRetries = 3;
    private static readonly Uri ApiBaseAddress = new("https://api.cloudflare.com/client/v4/");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _httpClient;
    private readonly IOptions<NntpdOptions> _options;
    private readonly ILogger<CloudflareDnsClient> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="CloudflareDnsClient"/> class.
    /// </summary>
    public CloudflareDnsClient(
        HttpClient httpClient,
        IOptions<NntpdOptions> options,
        ILogger<CloudflareDnsClient> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _httpClient = httpClient;
        _options = options;
        _logger = logger;

        if (_httpClient.BaseAddress is null)
        {
            _httpClient.BaseAddress = ApiBaseAddress;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CloudflareDnsRecord>> ListRecordsAsync(
        string zoneId,
        string fqdn,
        string type,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(zoneId);
        ArgumentException.ThrowIfNullOrWhiteSpace(fqdn);
        ArgumentException.ThrowIfNullOrWhiteSpace(type);

        var normalizedFqdn = NormalizeFqdn(fqdn);
        var results = new List<CloudflareDnsRecord>();
        var page = 1;
        int totalPages;

        do
        {
            cancellationToken.ThrowIfCancellationRequested();

            var path =
                $"zones/{Uri.EscapeDataString(zoneId)}/dns_records" +
                $"?type={Uri.EscapeDataString(type)}" +
                $"&name={Uri.EscapeDataString(normalizedFqdn)}" +
                $"&page={page}" +
                $"&per_page={DefaultPerPage}" +
                "&match=all";

            var envelope = await SendAsync<List<CloudflareDnsRecord>>(
                    HttpMethod.Get,
                    path,
                    content: null,
                    isMutation: false,
                    cancellationToken)
                .ConfigureAwait(false);

            if (envelope.Result is { Count: > 0 })
            {
                foreach (var record in envelope.Result)
                {
                    if (NamesMatch(record.Name, normalizedFqdn)
                        && string.Equals(record.Type, type, StringComparison.OrdinalIgnoreCase))
                    {
                        results.Add(record);
                    }
                }
            }

            totalPages = envelope.ResultInfo?.TotalPages ?? page;
            page++;
        }
        while (page <= totalPages);

        return results;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CloudflareDnsRecord>> ListAllRecordsForNameAsync(
        string zoneId,
        string fqdn,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(zoneId);
        ArgumentException.ThrowIfNullOrWhiteSpace(fqdn);

        var normalizedFqdn = NormalizeFqdn(fqdn);
        var results = new List<CloudflareDnsRecord>();
        var page = 1;
        int totalPages;

        do
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Omit type so Cloudflare returns every record type for this name filter.
            var path =
                $"zones/{Uri.EscapeDataString(zoneId)}/dns_records" +
                $"?name={Uri.EscapeDataString(normalizedFqdn)}" +
                $"&page={page}" +
                $"&per_page={DefaultPerPage}" +
                "&match=all";

            var envelope = await SendAsync<List<CloudflareDnsRecord>>(
                    HttpMethod.Get,
                    path,
                    content: null,
                    isMutation: false,
                    cancellationToken)
                .ConfigureAwait(false);

            if (envelope.Result is { Count: > 0 })
            {
                foreach (var record in envelope.Result)
                {
                    // Exact FQDN only — never suffix-match parent/child hostnames.
                    if (NamesMatch(record.Name, normalizedFqdn))
                    {
                        results.Add(record);
                    }
                }
            }

            totalPages = envelope.ResultInfo?.TotalPages ?? page;
            page++;
        }
        while (page <= totalPages);

        return results;
    }

    /// <inheritdoc />
    public Task<CloudflareDnsRecord> CreateRecordAsync(
        string zoneId,
        CloudflareDnsRecordWriteRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(zoneId);
        ArgumentNullException.ThrowIfNull(request);

        var path = $"zones/{Uri.EscapeDataString(zoneId)}/dns_records";
        return SendRecordAsync(HttpMethod.Post, path, request, cancellationToken);
    }

    /// <inheritdoc />
    public Task<CloudflareDnsRecord> UpdateRecordAsync(
        string zoneId,
        string recordId,
        CloudflareDnsRecordWriteRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(zoneId);
        ArgumentException.ThrowIfNullOrWhiteSpace(recordId);
        ArgumentNullException.ThrowIfNull(request);

        var path =
            $"zones/{Uri.EscapeDataString(zoneId)}/dns_records/{Uri.EscapeDataString(recordId)}";
        return SendRecordAsync(HttpMethod.Put, path, request, cancellationToken);
    }

    /// <inheritdoc />
    public async Task DeleteRecordAsync(
        string zoneId,
        string recordId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(zoneId);
        ArgumentException.ThrowIfNullOrWhiteSpace(recordId);

        var path =
            $"zones/{Uri.EscapeDataString(zoneId)}/dns_records/{Uri.EscapeDataString(recordId)}";

        await SendAsync<object>(HttpMethod.Delete, path, content: null, isMutation: true, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<CloudflareDnsRecord> SendRecordAsync(
        HttpMethod method,
        string path,
        CloudflareDnsRecordWriteRequest request,
        CancellationToken cancellationToken)
    {
        var envelope = await SendAsync<CloudflareDnsRecord>(
                method,
                path,
                request,
                isMutation: true,
                cancellationToken)
            .ConfigureAwait(false);

        if (envelope.Result is null)
        {
            throw new CloudflareDnsException(
                $"Cloudflare DNS {method} {path} returned success without a result payload.")
            {
                FailedOperation = $"{method.Method} {path}",
                IsOutcomeUncertain = true,
            };
        }

        return envelope.Result;
    }

    private async Task<CloudflareApiResponse<T>> SendAsync<T>(
        HttpMethod method,
        string relativePath,
        object? content,
        bool isMutation,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt <= MaxRateLimitRetries; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var request = CreateRequest(method, relativePath, content);
            HttpResponseMessage response;
            try
            {
                response = await _httpClient
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Preserve cancellation semantics for host startup. A mutation may already have been
                // applied; the next reconcile attempt must re-read Cloudflare rather than assume no change.
                if (isMutation)
                {
                    _logger.LogWarning(
                        "Cloudflare DNS {Method} {Path} canceled during a mutation; remote outcome is uncertain.",
                        method.Method,
                        relativePath);
                }

                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
            {
                throw new CloudflareDnsException(
                    $"Cloudflare DNS request failed for {method.Method} {relativePath}: {ex.GetType().Name}." +
                    (isMutation
                        ? " Mutation outcome is uncertain; re-read Cloudflare state before assuming no change."
                        : string.Empty),
                    ex)
                {
                    FailedOperation = $"{method.Method} {relativePath}",
                    IsOutcomeUncertain = isMutation,
                };
            }

            using (response)
            {
                if ((int)response.StatusCode == 429 && attempt < MaxRateLimitRetries)
                {
                    var delay = GetRetryDelay(response, attempt);
                    _logger.LogWarning(
                        "Cloudflare DNS rate limited (HTTP 429) for {Method} {Path}. Retrying after {DelayMs} ms (attempt {Attempt}/{MaxAttempts}).",
                        method.Method,
                        relativePath,
                        delay.TotalMilliseconds,
                        attempt + 1,
                        MaxRateLimitRetries);
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                CloudflareApiResponse<T>? envelope;
                try
                {
                    envelope = string.IsNullOrWhiteSpace(payload)
                        ? null
                        : JsonSerializer.Deserialize<CloudflareApiResponse<T>>(payload, JsonOptions);
                }
                catch (JsonException ex)
                {
                    throw new CloudflareDnsException(
                        $"Cloudflare DNS returned an unreadable JSON body for {method.Method} {relativePath} (HTTP {(int)response.StatusCode})." +
                        (isMutation && response.IsSuccessStatusCode
                            ? " Mutation outcome is uncertain; re-read Cloudflare state before assuming no change."
                            : string.Empty),
                        ex)
                    {
                        StatusCode = (int)response.StatusCode,
                        FailedOperation = $"{method.Method} {relativePath}",
                        IsOutcomeUncertain = isMutation && response.IsSuccessStatusCode,
                    };
                }

                if (!response.IsSuccessStatusCode)
                {
                    throw CreateFailureException(method, relativePath, response.StatusCode, envelope, payload, isMutation);
                }

                if (envelope is null)
                {
                    throw new CloudflareDnsException(
                        $"Cloudflare DNS returned an empty body for {method.Method} {relativePath}." +
                        (isMutation
                            ? " Mutation outcome is uncertain; re-read Cloudflare state before assuming no change."
                            : string.Empty))
                    {
                        StatusCode = (int)response.StatusCode,
                        FailedOperation = $"{method.Method} {relativePath}",
                        IsOutcomeUncertain = isMutation,
                    };
                }

                if (!envelope.Success)
                {
                    throw CreateFailureException(method, relativePath, response.StatusCode, envelope, payload, isMutation);
                }

                return envelope;
            }
        }

        throw new CloudflareDnsException(
            $"Cloudflare DNS request exhausted retries for {method.Method} {relativePath}.")
        {
            StatusCode = 429,
            FailedOperation = $"{method.Method} {relativePath}",
            IsOutcomeUncertain = isMutation,
        };
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string relativePath, object? content)
    {
        var apiKey = _options.Value.CloudFlareApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new CloudflareDnsException(
                $"{NntpdOptions.CloudFlareApiKeyConfigurationKey} is not configured.")
            {
                FailedOperation = "Authenticate",
                IsPermanentFailure = true,
            };
        }

        var request = new HttpRequestMessage(method, relativePath);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (content is not null)
        {
            request.Content = JsonContent.Create(content, options: JsonOptions);
        }

        return request;
    }

    private static CloudflareDnsException CreateFailureException<T>(
        HttpMethod method,
        string relativePath,
        HttpStatusCode statusCode,
        CloudflareApiResponse<T>? envelope,
        string rawBody,
        bool isMutation)
    {
        var codes = envelope?.Errors.Select(static e => e.Code).ToArray() ?? [];
        var messages = envelope?.Errors
            .Select(static e => string.IsNullOrWhiteSpace(e.Message) ? $"code {e.Code}" : e.Message)
            .ToArray() ?? [];

        var detail = messages.Length > 0
            ? string.Join("; ", messages)
            : TruncateForDiagnostics(rawBody);

        // Explicit 4xx (except 429) are permanent. Auth/authz codes on HTTP 2xx + success:false are also permanent.
        // Ambiguous 5xx after a mutation is uncertain.
        var permanent = CloudflareDnsException.IsPermanentHttpStatus((int)statusCode)
            || IsPermanentCloudflareApiFailure((int)statusCode, codes);
        var uncertain = isMutation && (int)statusCode >= 500;

        return new CloudflareDnsException(
            $"Cloudflare DNS {method.Method} {relativePath} failed with HTTP {(int)statusCode}" +
            (detail.Length > 0 ? $": {detail}" : ".") +
            (uncertain
                ? " Mutation outcome may be uncertain; re-read Cloudflare state before assuming no change."
                : string.Empty))
        {
            StatusCode = (int)statusCode,
            CloudflareErrorCodes = codes,
            FailedOperation = $"{method.Method} {relativePath}",
            IsOutcomeUncertain = uncertain,
            IsPermanentFailure = permanent,
        };
    }

    /// <summary>
    /// Cloudflare often returns HTTP 200 with <c>success: false</c> for auth failures; treat known codes as permanent.
    /// </summary>
    private static bool IsPermanentCloudflareApiFailure(int statusCode, IReadOnlyList<int> codes)
    {
        if (statusCode is < 200 or >= 300)
        {
            return false;
        }

        foreach (var code in codes)
        {
            // 10000 authentication error; 9109 / 9106 unauthorized; 6003 invalid/missing auth.
            if (code is 10000 or 9109 or 9106 or 6003)
            {
                return true;
            }
        }

        return false;
    }

    private static TimeSpan GetRetryDelay(HttpResponseMessage response, int attempt)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta && delta > TimeSpan.Zero)
        {
            return delta > TimeSpan.FromMinutes(2) ? TimeSpan.FromMinutes(2) : delta;
        }

        if (response.Headers.RetryAfter?.Date is { } date)
        {
            var until = date - DateTimeOffset.UtcNow;
            if (until > TimeSpan.Zero)
            {
                return until > TimeSpan.FromMinutes(2) ? TimeSpan.FromMinutes(2) : until;
            }
        }

        var seconds = Math.Pow(2, attempt);
        return TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 8));
    }

    private static string TruncateForDiagnostics(string rawBody)
    {
        if (string.IsNullOrWhiteSpace(rawBody))
        {
            return string.Empty;
        }

        const int max = 240;
        var trimmed = rawBody.Trim();
        if (trimmed.Length <= max)
        {
            return trimmed;
        }

        return trimmed[..max] + "…";
    }

    private static string NormalizeFqdn(string fqdn) => fqdn.Trim().TrimEnd('.').ToLowerInvariant();

    private static bool NamesMatch(string recordName, string normalizedFqdn)
    {
        if (string.IsNullOrWhiteSpace(recordName))
        {
            return false;
        }

        return string.Equals(NormalizeFqdn(recordName), normalizedFqdn, StringComparison.Ordinal);
    }
}
