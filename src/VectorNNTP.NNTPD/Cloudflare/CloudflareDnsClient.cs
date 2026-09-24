using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using VectorNNTP.NNTPD.Configuration;

namespace VectorNNTP.NNTPD.Cloudflare;

/// <summary>
/// HTTP client for the Cloudflare DNS Records API (v4).
/// </summary>
/// <remarks>
/// Authenticates with <c>Authorization: Bearer</c> using <see cref="NntpdOptions.CloudFlareApiKey"/>.
/// Never logs the API key or authorization headers. Treats <c>success: false</c> and non-success
/// HTTP statuses as failures. Transport failures and per-request timeouts during mutations are marked
/// <see cref="CloudflareDnsException.IsOutcomeUncertain"/> because Cloudflare may already have applied them.
/// List operations require complete, consistent <c>result_info</c> pagination metadata and reject records
/// whose <c>zone_id</c> (when present) does not match the requested zone.
/// Each HTTP attempt is cancelled after <see cref="PerRequestTimeout"/> or the remaining
/// <see cref="CloudflareOperationBudget"/>, whichever is shorter. Caller cancellation is honoured immediately
/// and is not converted into success.
/// </remarks>
public sealed class CloudflareDnsClient : ICloudflareDnsClient
{
    internal const string HttpClientName = "CloudflareDns";
    private const int DefaultPerPage = 100;
    private const int MaxRateLimitRetries = 3;

    /// <summary>
    /// Maximum duration for a single HTTP attempt (headers). Further bounded by remaining
    /// <see cref="CloudflareOperationBudget"/> when an operation scope is active.
    /// </summary>
    internal static readonly TimeSpan PerRequestTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Hard ceiling on list pages (matches Cloudflare's practical upper bound for a single hostname listing).
    /// </summary>
    internal const int MaxListPages = 20;

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
    /// Gets or sets the delay function used for HTTP 429 backoff (tests may replace this).
    /// </summary>
    internal Func<TimeSpan, CancellationToken, Task> DelayAsync { get; set; } =
        static (delay, cancellationToken) => Task.Delay(delay, cancellationToken);

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
    public Task<IReadOnlyList<CloudflareDnsRecord>> ListRecordsAsync(
        string zoneId,
        string fqdn,
        string type,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(zoneId);
        ArgumentException.ThrowIfNullOrWhiteSpace(fqdn);
        ArgumentException.ThrowIfNullOrWhiteSpace(type);

        var normalizedFqdn = NormalizeFqdn(fqdn);
        var query =
            $"type={Uri.EscapeDataString(type)}" +
            $"&name={Uri.EscapeDataString(normalizedFqdn)}" +
            $"&per_page={DefaultPerPage}" +
            "&match=all";

        return ListPagedRecordsAsync(
            zoneId,
            query,
            record =>
                NamesMatch(record.Name, normalizedFqdn)
                && string.Equals(record.Type, type, StringComparison.OrdinalIgnoreCase),
            record => EnsureListedAddressRecordValid(record, type),
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<CloudflareDnsRecord>> ListAllRecordsForNameAsync(
        string zoneId,
        string fqdn,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(zoneId);
        ArgumentException.ThrowIfNullOrWhiteSpace(fqdn);

        var normalizedFqdn = NormalizeFqdn(fqdn);
        var query =
            $"name={Uri.EscapeDataString(normalizedFqdn)}" +
            $"&per_page={DefaultPerPage}" +
            "&match=all";

        return ListPagedRecordsAsync(
            zoneId,
            query,
            record => NamesMatch(record.Name, normalizedFqdn),
            EnsureListedCleanupRecordValid,
            cancellationToken);
    }

    private async Task<IReadOnlyList<CloudflareDnsRecord>> ListPagedRecordsAsync(
        string zoneId,
        string queryWithoutPage,
        Func<CloudflareDnsRecord, bool> includeRecord,
        Action<CloudflareDnsRecord> validateRecord,
        CancellationToken cancellationToken)
    {
        var results = new List<CloudflareDnsRecord>();
        int? declaredTotalPages = null;

        for (var page = 1; page <= MaxListPages; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CloudflareOperationBudget.Current?.ThrowIfExpired(cancellationToken);

            var path =
                $"zones/{Uri.EscapeDataString(zoneId)}/dns_records" +
                $"?{queryWithoutPage}&page={page}";

            var envelope = await SendAsync<List<CloudflareDnsRecord>>(
                    HttpMethod.Get,
                    path,
                    content: null,
                    isMutation: false,
                    cancellationToken)
                .ConfigureAwait(false);

            if (envelope.Result is null)
            {
                throw CreatePermanentDataException(
                    "List",
                    "Cloudflare DNS list response is missing result; listing completeness cannot be established.");
            }

            var totalPages = RequireTotalPages(envelope.ResultInfo, expectedPage: page);
            if (declaredTotalPages is null)
            {
                declaredTotalPages = totalPages;
            }
            else if (totalPages != declaredTotalPages.Value)
            {
                throw CreatePermanentDataException(
                    "List",
                    "Cloudflare DNS list pagination is inconsistent: total_pages changed between pages.");
            }

            if (page > totalPages)
            {
                throw CreatePermanentDataException(
                    "List",
                    "Cloudflare DNS list pagination is inconsistent: requested page exceeds total_pages.");
            }

            foreach (var record in envelope.Result)
            {
                EnsureRecordZoneMatches(record, zoneId, operation: "List");
                validateRecord(record);
                if (includeRecord(record))
                {
                    results.Add(record);
                }
            }

            if (page == totalPages)
            {
                return results;
            }
        }

        throw CreatePermanentDataException(
            "List",
            $"Cloudflare DNS list exceeded {MaxListPages} pages " +
            $"(total_pages={declaredTotalPages?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"}). " +
            "Listing is incomplete; reconciliation must not mutate from a partial result.");
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
        return SendRecordAsync(HttpMethod.Post, path, zoneId, request, cancellationToken);
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
        return SendRecordAsync(HttpMethod.Put, path, zoneId, request, cancellationToken);
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
        string zoneId,
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

        EnsureRecordZoneMatches(envelope.Result, zoneId, operation: method.Method);
        if (string.Equals(envelope.Result.Type, CloudflareDnsRecordTypes.A, StringComparison.OrdinalIgnoreCase)
            || string.Equals(envelope.Result.Type, CloudflareDnsRecordTypes.AAAA, StringComparison.OrdinalIgnoreCase))
        {
            EnsureListedAddressRecordValid(envelope.Result, envelope.Result.Type);
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
            CloudflareOperationBudget.Current?.ThrowIfExpired(cancellationToken);

            using var request = CreateRequest(method, relativePath, content);
            using var requestTimeoutCts = CreateRequestTimeoutCts(cancellationToken, out var sendToken);
            HttpResponseMessage response;
            try
            {
                response = await _httpClient
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, sendToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Caller or shared operation budget cancelled. A mutation may already have been applied;
                // do not claim definitive failure — propagate cancellation so the caller stops.
                if (isMutation)
                {
                    CloudflareLogMessages.MutationCanceledUncertain(_logger, method.Method, relativePath);
                }

                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
            {
                // Per-request timeout (or transport) without operation cancellation.
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

            TimeSpan? rateLimitDelay = null;
            using (response)
            {
                if ((int)response.StatusCode == 429 && attempt < MaxRateLimitRetries)
                {
                    rateLimitDelay = GetRetryDelay(response, attempt);
                    CloudflareLogMessages.RateLimited(
                        _logger,
                        method.Method,
                        relativePath,
                        rateLimitDelay.Value.TotalMilliseconds,
                        attempt + 1,
                        MaxRateLimitRetries);
                }
                else
                {
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
                        // Malformed payloads are data-integrity failures. List/read must not be retried as
                        // transient; mutation responses with HTTP success remain outcome-uncertain.
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
                            IsPermanentFailure = !isMutation,
                        };
                    }

                    if (!response.IsSuccessStatusCode)
                    {
                        throw CreateFailureException(method, relativePath, response.StatusCode, envelope, isMutation);
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
                            IsPermanentFailure = !isMutation,
                        };
                    }

                    if (!envelope.Success)
                    {
                        throw CreateFailureException(method, relativePath, response.StatusCode, envelope, isMutation);
                    }

                    return envelope;
                }
            }

            // Backoff only after the 429 response is disposed.
            await DelayForRateLimitAsync(rateLimitDelay!.Value, cancellationToken).ConfigureAwait(false);
        }

        throw new CloudflareDnsException(
            $"Cloudflare DNS request exhausted retries for {method.Method} {relativePath}.")
        {
            StatusCode = 429,
            FailedOperation = $"{method.Method} {relativePath}",
            IsOutcomeUncertain = isMutation,
        };
    }

    /// <summary>
    /// Builds a per-attempt token cancelled when the operation cancels or the request timeout elapses.
    /// </summary>
    /// <remarks>
    /// Timeout is <see cref="PerRequestTimeout"/> capped by remaining <see cref="CloudflareOperationBudget"/>
    /// so a short remaining budget cannot be extended by the full per-request allowance.
    /// </remarks>
    internal static CancellationTokenSource CreateRequestTimeoutCts(
        CancellationToken operationToken,
        out CancellationToken sendToken)
    {
        operationToken.ThrowIfCancellationRequested();
        CloudflareOperationBudget.Current?.ThrowIfExpired(operationToken);

        var limit = PerRequestTimeout;
        if (CloudflareOperationBudget.Current is { } budget)
        {
            var remaining = budget.Remaining;
            if (remaining <= TimeSpan.Zero)
            {
                throw new OperationCanceledException(
                    "Cloudflare DNS operation budget has expired.",
                    operationToken);
            }

            if (remaining < limit)
            {
                limit = remaining;
            }
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(operationToken);
        cts.CancelAfter(limit);
        sendToken = cts.Token;
        return cts;
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
        bool isMutation)
    {
        var codes = envelope?.Errors.Select(static e => e.Code).ToArray() ?? [];
        var messages = envelope?.Errors
            .Select(static e => string.IsNullOrWhiteSpace(e.Message) ? $"code {e.Code}" : SanitizeDiagnosticText(e.Message))
            .ToArray() ?? [];

        var detail = messages.Length > 0
            ? string.Join("; ", messages)
            : "no Cloudflare error details returned";

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

    private async Task DelayForRateLimitAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CloudflareOperationBudget.Current?.ThrowIfExpired(cancellationToken);

        if (delay <= TimeSpan.Zero)
        {
            return;
        }

        if (CloudflareOperationBudget.Current is { } budget && delay > budget.Remaining)
        {
            throw new OperationCanceledException(
                "Cloudflare DNS rate-limit Retry-After exceeds the remaining operation budget.",
                cancellationToken);
        }

        await DelayAsync(delay, cancellationToken).ConfigureAwait(false);
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

    private static string SanitizeDiagnosticText(string text)
    {
        const int max = 160;
        var trimmed = text.Trim();
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

    /// <summary>
    /// Validates a listed A/AAAA record for reconcile safety (no deserialization-default compliance).
    /// </summary>
    internal static void EnsureListedAddressRecordValid(CloudflareDnsRecord record, string expectedType)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedType);

        if (string.IsNullOrWhiteSpace(record.Id))
        {
            throw CreatePermanentDataException("List", "Cloudflare DNS record is missing id.");
        }

        if (string.IsNullOrWhiteSpace(record.Type)
            || !string.Equals(record.Type, expectedType, StringComparison.OrdinalIgnoreCase))
        {
            throw CreatePermanentDataException(
                "List",
                "Cloudflare DNS record type is missing or does not match the requested type.");
        }

        if (string.IsNullOrWhiteSpace(record.Content))
        {
            throw CreatePermanentDataException("List", "Cloudflare DNS record content is missing.");
        }

        if (record.Ttl is not { } ttl || ttl < 1)
        {
            throw CreatePermanentDataException(
                "List",
                "Cloudflare DNS record ttl is missing or invalid.");
        }

        if (record.Proxied is null)
        {
            throw CreatePermanentDataException(
                "List",
                "Cloudflare DNS record proxied flag is missing.");
        }
    }

    /// <summary>
    /// Validates a clean-up listing record: identity fields required; type-specific TTL/proxy not enforced.
    /// </summary>
    internal static void EnsureListedCleanupRecordValid(CloudflareDnsRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (string.IsNullOrWhiteSpace(record.Id))
        {
            throw CreatePermanentDataException("List", "Cloudflare DNS record is missing id.");
        }

        if (string.IsNullOrWhiteSpace(record.Type))
        {
            throw CreatePermanentDataException("List", "Cloudflare DNS record type is missing.");
        }

        if (string.IsNullOrWhiteSpace(record.Name))
        {
            throw CreatePermanentDataException("List", "Cloudflare DNS record name is missing.");
        }
    }

    /// <summary>
    /// Validates Cloudflare <c>result_info</c> and returns a proven <c>total_pages</c>.
    /// </summary>
    /// <remarks>
    /// Missing or unusable pagination metadata must not be treated as a successful single-page listing.
    /// </remarks>
    internal static int RequireTotalPages(CloudflareResultInfo? info, int expectedPage)
    {
        if (info is null)
        {
            throw CreatePermanentDataException(
                "List",
                "Cloudflare DNS list response is missing result_info; listing completeness cannot be established.");
        }

        if (info.TotalPages is not { } totalPages)
        {
            throw CreatePermanentDataException(
                "List",
                "Cloudflare DNS list result_info is missing total_pages; listing completeness cannot be established.");
        }

        if (totalPages < 1)
        {
            throw CreatePermanentDataException(
                "List",
                "Cloudflare DNS list result_info.total_pages must be >= 1.");
        }

        if (info.Page is { } reportedPage && reportedPage != expectedPage)
        {
            throw CreatePermanentDataException(
                "List",
                "Cloudflare DNS list result_info.page does not match the requested page.");
        }

        return totalPages;
    }

    /// <summary>
    /// Applies the zone ownership policy for a returned DNS record.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Policy (aligned with Cloudflare's current RecordResponse schema and the pyNNTPD reference):
    /// when <c>zone_id</c> is omitted, the path zone is authoritative and the record is accepted;
    /// when present, it must be a non-empty string exactly matching the requested zone.
    /// </para>
    /// </remarks>
    internal static void EnsureRecordZoneMatches(CloudflareDnsRecord record, string expectedZoneId, string operation)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedZoneId);

        if (record.ZoneId is null)
        {
            return;
        }

        if (record.ZoneId.Length == 0 || string.IsNullOrWhiteSpace(record.ZoneId))
        {
            throw CreatePermanentDataException(
                operation,
                "Cloudflare DNS record zone_id is empty or whitespace; failing closed.");
        }

        if (!string.Equals(record.ZoneId, expectedZoneId, StringComparison.Ordinal))
        {
            throw CreatePermanentDataException(
                operation,
                "Cloudflare DNS record zone_id does not match the requested zone; " +
                "record must not enter the reconciliation plan.");
        }
    }

    private static CloudflareDnsException CreatePermanentDataException(string operation, string message) =>
        new(message)
        {
            FailedOperation = operation,
            IsPermanentFailure = true,
            IsOutcomeUncertain = false,
        };
}
