using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using VectorNNTP.Common.Configuration;

namespace VectorNNTP.Common.Cloudflare
{
    /// <summary>
    /// HTTP client for the Cloudflare DNS Records API (v4).
    /// </summary>
    /// <remarks>
    /// Authenticates with <c>Authorization: Bearer</c> using <see cref="AcmeCloudflareOptions.CloudFlareApiKey"/>.
    /// Never logs the API key or authorization headers. Treats <c>success: false</c> and non-success
    /// HTTP statuses as failures. Transport failures and per-request timeouts during mutations are marked
    /// <see cref="CloudflareDnsException.IsOutcomeUncertain"/> because Cloudflare may already have applied them.
    /// List operations require complete, consistent <c>result_info</c> pagination metadata and reject records
    /// whose <c>zone_id</c> (when present) does not match the requested zone.
    /// Each HTTP attempt is cancelled after <see cref="PerRequestTimeout"/> or the remaining
    /// <see cref="CloudflareOperationBudget"/>, whichever is shorter. Caller cancellation is honoured immediately
    /// and is not converted into success.
    /// </remarks>
    internal sealed class CloudflareDnsClient : ICloudflareDnsClient
    {
        /// <summary>Named <see cref="HttpClient"/> registered for the Cloudflare DNS API.</summary>
        internal const string HttpClientName = "CloudflareDns";

        /// <summary>Page size sent as <c>per_page</c> on every DNS list request.</summary>
        private const int DefaultPerPage = 100;

        /// <summary>
        /// HTTP 429 waits after the first response. The send loop then makes one more attempt that does not wait.
        /// </summary>
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

        /// <summary>
        /// Default Cloudflare API v4 base URI, applied only when the injected <see cref="HttpClient"/> has no base address.
        /// </summary>
        private static readonly Uri ApiBaseAddress = new("https://api.cloudflare.com/client/v4/");

        /// <summary>HTTP client used for DNS record calls. Its base address is set to <see cref="ApiBaseAddress"/> when missing.</summary>
        private readonly HttpClient _httpClient;

        /// <summary>Supplies the bearer token from <see cref="AcmeCloudflareOptions.CloudFlareApiKey"/> on each request.</summary>
        private readonly IOptions<AcmeCloudflareOptions> _options;

        /// <summary>Client diagnostics. The API key and authorization header are not logged.</summary>
        private readonly ILogger<CloudflareDnsClient> _logger;

        /// <summary>
        /// Gets or sets the delay function used for HTTP 429 backoff (tests may replace this).
        /// </summary>
        internal Func<TimeSpan, CancellationToken, Task> DelayAsync { get; set; } =
            static (delay, cancellationToken) => Task.Delay(delay, cancellationToken);

        /// <summary>
        /// Initializes a new instance of the <see cref="CloudflareDnsClient"/> class.
        /// </summary>
        /// <param name="httpClient">Client for the Cloudflare API. A missing base address is set to the v4 API root.</param>
        /// <param name="options">Options that supply the API token. The token is read per request and is not logged.</param>
        /// <param name="logger">Client logger.</param>
        internal CloudflareDnsClient(
            HttpClient httpClient,
            IOptions<AcmeCloudflareOptions> options,
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

        /// <summary>
        /// Lists DNS records page by page until <c>result_info.total_pages</c> is reached.
        /// </summary>
        /// <param name="zoneId">Cloudflare zone identifier used in the request path and zone check.</param>
        /// <param name="queryWithoutPage">Query string without the <c>page</c> parameter.</param>
        /// <param name="includeRecord">Client-side filter applied after validation. Non-matching records are omitted.</param>
        /// <param name="validateRecord">Per-record check that throws a permanent data failure when required fields are missing.</param>
        /// <param name="cancellationToken">Cancels the list. Cancellation is not treated as success.</param>
        /// <returns>Records that passed <paramref name="includeRecord"/>, across every page.</returns>
        /// <exception cref="CloudflareDnsException">
        /// Pagination metadata is missing or inconsistent, a page exceeds <see cref="MaxListPages"/>,
        /// or a record fails zone or field validation. Those failures are permanent.
        /// </exception>
        /// <remarks>
        /// <see cref="CloudflareOperationBudget.Current"/> is checked before each page. A changing
        /// <c>total_pages</c> between pages fails the list so reconciliation cannot mutate from a partial result.
        /// </remarks>
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

                var envelope = await SendAsync(
                        HttpMethod.Get,
                        path,
                        content: null,
                        isMutation: false,
                        CloudflareJsonSerializerContext.Default.CloudflareApiResponseListCloudflareDnsRecord,
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

            await SendAsync(
                    HttpMethod.Delete,
                    path,
                    content: null,
                    isMutation: true,
                    CloudflareJsonSerializerContext.Default.CloudflareApiResponseCloudflareDeleteResult,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Creates or updates one DNS record and requires a result payload.
        /// </summary>
        /// <param name="method"><see cref="HttpMethod.Post"/> for create or <see cref="HttpMethod.Put"/> for update.</param>
        /// <param name="path">Relative DNS-record path, including the zone and, for update, the record id.</param>
        /// <param name="zoneId">Zone that a returned <c>zone_id</c> must match when Cloudflare includes one.</param>
        /// <param name="request">Record body. A/AAAA writes use the managed TTL and proxy flag supplied by the caller.</param>
        /// <param name="cancellationToken">Cancels the mutation. Cancellation after send is logged as an uncertain outcome and propagated.</param>
        /// <returns>The record Cloudflare returned.</returns>
        /// <exception cref="CloudflareDnsException">
        /// The call failed, or HTTP success arrived without a result payload. A missing result is outcome-uncertain.
        /// Returned A/AAAA records must include id, type, content, TTL, and proxy flag.
        /// </exception>
        private async Task<CloudflareDnsRecord> SendRecordAsync(
            HttpMethod method,
            string path,
            string zoneId,
            CloudflareDnsRecordWriteRequest request,
            CancellationToken cancellationToken)
        {
            var envelope = await SendAsync(
                    method,
                    path,
                    request,
                    isMutation: true,
                    CloudflareJsonSerializerContext.Default.CloudflareApiResponseCloudflareDnsRecord,
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

        /// <summary>
        /// Sends one Cloudflare API call, retrying HTTP 429 up to <see cref="MaxRateLimitRetries"/> waits.
        /// </summary>
        /// <typeparam name="T">Deserialized <c>result</c> type.</typeparam>
        /// <param name="method">HTTP method.</param>
        /// <param name="relativePath">Path relative to the API base address.</param>
        /// <param name="content">JSON body for create and update. Null for list and delete.</param>
        /// <param name="isMutation">
        /// <see langword="true"/> for create, update, and delete. Transport failure and caller cancellation then leave the remote outcome uncertain.
        /// </param>
        /// <param name="responseTypeInfo">Source-generated metadata for the response envelope.</param>
        /// <param name="cancellationToken">Caller or operation-budget token. Caller cancellation is propagated and is not converted into success.</param>
        /// <returns>The success envelope, including a false-success check that throws instead of returning.</returns>
        /// <exception cref="CloudflareDnsException">
        /// Transport failure, unreadable JSON, empty body, non-success HTTP, <c>success: false</c>, or exhausted 429 retries.
        /// Mutations mark <see cref="CloudflareDnsException.IsOutcomeUncertain"/> when Cloudflare may already have applied the call.
        /// Non-mutation data failures are permanent.
        /// </exception>
        /// <exception cref="OperationCanceledException">The caller or operation budget canceled the attempt.</exception>
        private async Task<CloudflareApiResponse<T>> SendAsync<T>(
            HttpMethod method,
            string relativePath,
            CloudflareDnsRecordWriteRequest? content,
            bool isMutation,
            System.Text.Json.Serialization.Metadata.JsonTypeInfo<CloudflareApiResponse<T>> responseTypeInfo,
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
                                : JsonSerializer.Deserialize(payload, responseTypeInfo);
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
        /// <param name="operationToken">Caller or operation-budget token. Already-canceled tokens throw before the source is created.</param>
        /// <param name="sendToken">Token the HTTP send must observe. Canceled with the returned source.</param>
        /// <returns>The linked source. The caller disposes it.</returns>
        /// <exception cref="OperationCanceledException">
        /// <paramref name="operationToken"/> is canceled or the active budget has already expired.
        /// </exception>
        /// <remarks>
        /// When remaining <see cref="CloudflareOperationBudget"/> is at least <see cref="PerRequestTimeout"/>,
        /// this token independently times out after <see cref="PerRequestTimeout"/>. When remaining budget is
        /// shorter, it only links the operation token so budget expiry surfaces as operation cancellation
        /// rather than a per-request timeout (which would wrap List into <see cref="CloudflareDnsException"/>).
        /// </remarks>
        internal static CancellationTokenSource CreateRequestTimeoutCts(
            CancellationToken operationToken,
            out CancellationToken sendToken)
        {
            operationToken.ThrowIfCancellationRequested();
            CloudflareOperationBudget.Current?.ThrowIfExpired(operationToken);

            var applyPerRequestTimeout = true;
            if (CloudflareOperationBudget.Current is { } budget)
            {
                var remaining = budget.Remaining;
                if (remaining <= TimeSpan.Zero)
                {
                    throw new OperationCanceledException(
                        "Cloudflare DNS operation budget has expired.",
                        operationToken);
                }

                if (remaining < PerRequestTimeout)
                {
                    applyPerRequestTimeout = false;
                }
            }

            var cts = CancellationTokenSource.CreateLinkedTokenSource(operationToken);
            if (applyPerRequestTimeout)
            {
                cts.CancelAfter(PerRequestTimeout);
            }

            sendToken = cts.Token;
            return cts;
        }

        /// <summary>
        /// Builds a JSON request with <c>Authorization: Bearer</c> from <see cref="AcmeCloudflareOptions.CloudFlareApiKey"/>.
        /// </summary>
        /// <param name="method">HTTP method.</param>
        /// <param name="relativePath">Path relative to the API base address.</param>
        /// <param name="content">Optional record body. Null omits the request content.</param>
        /// <returns>The request. The caller owns and disposes it.</returns>
        /// <exception cref="CloudflareDnsException">
        /// The API key is missing or whitespace. That failure is permanent and does not include the key.
        /// </exception>
        private HttpRequestMessage CreateRequest(
            HttpMethod method,
            string relativePath,
            CloudflareDnsRecordWriteRequest? content)
        {
            var apiKey = _options.Value.CloudFlareApiKey;
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new CloudflareDnsException(
                    $"{AcmeCloudflareOptions.CloudFlareApiKeyConfigurationKey} is not configured.")
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
                request.Content = JsonContent.Create(
                    content,
                    CloudflareJsonSerializerContext.Default.CloudflareDnsRecordWriteRequest);
            }

            return request;
        }

        /// <summary>
        /// Maps a non-success HTTP status or <c>success: false</c> envelope into <see cref="CloudflareDnsException"/>.
        /// </summary>
        /// <typeparam name="T">Envelope result type. Only <c>errors</c> are read.</typeparam>
        /// <param name="method">HTTP method that failed.</param>
        /// <param name="relativePath">Relative path that failed.</param>
        /// <param name="statusCode">HTTP status.</param>
        /// <param name="envelope">Parsed envelope, or null when the body was empty or unreadable.</param>
        /// <param name="isMutation"><see langword="true"/> when a 5xx response leaves the remote outcome uncertain.</param>
        /// <returns>
        /// An exception whose 4xx status (except 429) is permanent, whose known auth codes on HTTP 2xx are permanent,
        /// and whose 5xx mutation is outcome-uncertain.
        /// </returns>
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
        /// <param name="statusCode">HTTP status. Values outside 200–299 return <see langword="false"/>.</param>
        /// <param name="codes">Cloudflare error codes from the envelope.</param>
        /// <returns>
        /// <see langword="true"/> when <paramref name="statusCode"/> is HTTP 2xx and a code is 10000, 9109, 9106, or 6003.
        /// </returns>
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

        /// <summary>
        /// Waits for an HTTP 429 delay unless it exceeds the remaining <see cref="CloudflareOperationBudget"/>.
        /// </summary>
        /// <param name="delay">Delay from <see cref="GetRetryDelay"/>. Zero or negative returns without waiting.</param>
        /// <param name="cancellationToken">Caller token. Cancellation before the wait throws.</param>
        /// <exception cref="OperationCanceledException">
        /// The token is canceled, the budget is already expired, or <paramref name="delay"/> is longer than the remaining budget.
        /// </exception>
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

        /// <summary>
        /// Chooses the HTTP 429 wait: <c>Retry-After</c> delta or HTTP-date, capped at two minutes;
        /// otherwise <c>2^attempt</c> seconds clamped to 1–8.
        /// </summary>
        /// <param name="response">429 response. Its headers are read before the caller disposes it.</param>
        /// <param name="attempt">Zero-based send index used only for the exponential fallback.</param>
        /// <returns>The delay passed to <see cref="DelayForRateLimitAsync"/>.</returns>
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

        /// <summary>
        /// Trims Cloudflare error text and truncates it to 160 characters so exception messages stay bounded.
        /// </summary>
        /// <param name="text">Error text from the API. This method does not redact secrets.</param>
        /// <returns>The trimmed text, with an ellipsis appended when truncated.</returns>
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

        /// <summary>Trims <paramref name="fqdn"/>, removes one trailing dot, and lowercases it with the invariant culture.</summary>
        /// <param name="fqdn">DNS name from configuration or a Cloudflare record.</param>
        /// <returns>The normalized name used for exact comparisons.</returns>
        private static string NormalizeFqdn(string fqdn) => fqdn.Trim().TrimEnd('.').ToLowerInvariant();

        /// <summary>
        /// Returns whether <paramref name="recordName"/> is the same DNS name as <paramref name="normalizedFqdn"/> after <see cref="NormalizeFqdn"/>.
        /// </summary>
        /// <param name="recordName">Name returned by Cloudflare. Whitespace does not match.</param>
        /// <param name="normalizedFqdn">Name already passed through <see cref="NormalizeFqdn"/>.</param>
        /// <returns><see langword="true"/> for an ordinal exact match.</returns>
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

        /// <summary>
        /// Creates a permanent, outcome-certain data failure for list or response validation.
        /// </summary>
        /// <param name="operation">Operation name stored on <see cref="CloudflareDnsException.FailedOperation"/>.</param>
        /// <param name="message">Exception message. It must not include the API key.</param>
        /// <returns>The permanent failure.</returns>
        private static CloudflareDnsException CreatePermanentDataException(string operation, string message) =>
            new(message)
            {
                FailedOperation = operation,
                IsPermanentFailure = true,
                IsOutcomeUncertain = false,
            };
    }
}
