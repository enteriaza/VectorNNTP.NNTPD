using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace VectorNNTP.Common.Acme.Protocol
{
    /// <summary>
    /// ACME HTTP transport: directory cache, nonce pool, JWS POST, badNonce / transient retries.
    /// Creates an <see cref="HttpClient"/> per request via <see cref="IHttpClientFactory"/>.
    /// </summary>
    internal sealed class AcmeHttpTransport
    {
        /// <summary>JWS request <c>Content-Type</c> required by RFC 8555 §6.2, with no charset parameter.</summary>
        private const string JoseContentType = "application/jose+json";

        /// <summary>Maximum JWS submissions after a <see cref="AcmeErrorTypes.BadNonce"/> rejection, including the first attempt.</summary>
        private const int MaxBadNonceRetries = 5;

        /// <summary>Maximum extra attempts after a transport or retryable HTTP failure. The first attempt is not counted.</summary>
        private const int MaxTransientRetries = 4;

        /// <summary>Longest Retry-After delay that is honored. A longer or non-positive delay fails the request instead of waiting.</summary>
        private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(5);

        /// <summary>Factory used to create one <see cref="HttpClient"/> per HTTP call.</summary>
        private readonly IHttpClientFactory _httpClientFactory;

        /// <summary>Named client passed to <see cref="IHttpClientFactory.CreateClient(string)"/>.</summary>
        private readonly string _httpClientName;

        /// <summary>ACME directory URL fetched by GET and cached until invalidated.</summary>
        private readonly Uri _directoryUri;

        /// <summary>Logger for nonce and transport retries.</summary>
        private readonly ILogger _logger;

        /// <summary>Clock used for Retry-After deltas and transport backoff delays.</summary>
        private readonly TimeProvider _time;

        /// <summary>Replay nonces captured from <c>Replay-Nonce</c> response headers. Popped before each JWS.</summary>
        private readonly ConcurrentStack<string> _nonces = new();

        /// <summary>
        /// Cached directory fetch. <see langword="null"/> before the first fetch and after invalidation.
        /// A faulted or canceled task is replaced on the next read.
        /// </summary>
        private Task<AcmeDirectoryResource>? _directory;

        /// <summary>
        /// Stores the HTTP client factory, directory URL, logger, and clock used for every later request.
        /// </summary>
        /// <param name="httpClientFactory">Factory that creates the named ACME client.</param>
        /// <param name="httpClientName">Name passed to <see cref="IHttpClientFactory.CreateClient(string)"/>.</param>
        /// <param name="directoryUri">Absolute ACME directory URL.</param>
        /// <param name="logger">Logger for retry events.</param>
        /// <param name="time">Clock for retry delays.</param>
        internal AcmeHttpTransport(
            IHttpClientFactory httpClientFactory,
            string httpClientName,
            Uri directoryUri,
            ILogger logger,
            TimeProvider time)
        {
            _httpClientFactory = httpClientFactory;
            _httpClientName = httpClientName;
            _directoryUri = directoryUri;
            _logger = logger;
            _time = time;
        }

        /// <summary>Creates a new client for one request. The caller disposes it.</summary>
        /// <returns>The named <see cref="HttpClient"/>.</returns>
        private HttpClient CreateClient() => _httpClientFactory.CreateClient(_httpClientName);

        /// <summary>Returns the cached ACME directory, fetching it on first use.</summary>
        /// <param name="cancellationToken">Cancels the directory GET when a fetch is started. A cached task is returned as-is.</param>
        /// <returns>The directory document. A faulted or canceled cached task is replaced on the next call.</returns>
        internal Task<AcmeDirectoryResource> GetDirectoryAsync(CancellationToken cancellationToken)
        {
            Task<AcmeDirectoryResource>? cached = Volatile.Read(ref _directory);
            if (cached is not null && !cached.IsFaulted && !cached.IsCanceled)
            {
                return cached;
            }

            Task<AcmeDirectoryResource> fetch = FetchDirectoryAsync(cancellationToken);
            Volatile.Write(ref _directory, fetch);
            return fetch;
        }

        /// <summary>Drops the cached directory so the next request fetches it again.</summary>
        private void InvalidateDirectory() => Volatile.Write(ref _directory, null);

        /// <summary>GETs the directory and deserializes it. An empty body throws <see cref="AcmeCaException"/>.</summary>
        /// <param name="cancellationToken">Cancels the GET.</param>
        /// <returns>The directory document.</returns>
        private async Task<AcmeDirectoryResource> FetchDirectoryAsync(CancellationToken cancellationToken)
        {
            AcmeResponse<AcmeDirectoryResource> response = await SendWithRetryAsync(
                () => new HttpRequestMessage(HttpMethod.Get, _directoryUri),
                AcmeJsonContext.Default.AcmeDirectoryResource,
                cancellationToken).ConfigureAwait(false);

            return response.Content ?? throw new AcmeCaException("The ACME directory response was empty.");
        }

        /// <summary>POSTs a JWS-signed payload, retrying on badNonce.</summary>
        /// <typeparam name="T">Deserialized success body. Unused when the body is empty.</typeparam>
        /// <param name="key">Account key that signs the JWS.</param>
        /// <param name="keyId">Account URL (<c>kid</c>). <see langword="null"/> sends the JWK instead, used for <c>newAccount</c>.</param>
        /// <param name="url">Request URL, also the JWS <c>url</c> field.</param>
        /// <param name="payload">JSON payload. An empty string is POST-as-GET.</param>
        /// <param name="typeInfo">Source-generated metadata for <typeparamref name="T"/>.</param>
        /// <param name="cancellationToken">Cancels nonce fetch and the POST.</param>
        /// <returns>The successful response.</returns>
        /// <exception cref="AcmeCaException">Thrown when badNonce persists for <see cref="MaxBadNonceRetries"/> attempts, or the CA rejects the request.</exception>
        internal async Task<AcmeResponse<T>> PostAsync<T>(
            AcmeAccountKey key,
            string? keyId,
            Uri url,
            string payload,
            JsonTypeInfo<T> typeInfo,
            CancellationToken cancellationToken)
        {
            Exception? lastError = null;

            for (int attempt = 0; attempt < MaxBadNonceRetries; attempt++)
            {
                string nonce = await TakeNonceAsync(cancellationToken).ConfigureAwait(false);
                string jws = JsonWebSignature.Encode(key, url, nonce, keyId, payload);

                try
                {
                    return await SendWithRetryAsync(() => CreateJwsRequest(url, jws, accept: null), typeInfo, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (AcmeCaException ex) when (ex.ErrorType == AcmeErrorTypes.BadNonce)
                {
                    lastError = ex;
                    AcmeLogMessages.AcmeBadNonceRetry(_logger, url);
                }
            }

            throw new AcmeCaException(
                $"The certificate authority repeatedly rejected the replay nonce for '{url}'.",
                lastError ?? new AcmeCaException("badNonce"));
        }

        /// <summary>POST-as-GET (empty JWS payload).</summary>
        /// <typeparam name="T">Deserialized success body.</typeparam>
        /// <param name="key">Account key that signs the JWS.</param>
        /// <param name="keyId">Account URL (<c>kid</c>). Required.</param>
        /// <param name="url">Resource URL.</param>
        /// <param name="typeInfo">Source-generated metadata for <typeparamref name="T"/>.</param>
        /// <param name="cancellationToken">Cancels the request.</param>
        /// <returns>The successful response.</returns>
        internal Task<AcmeResponse<T>> PostAsGetAsync<T>(
            AcmeAccountKey key,
            string keyId,
            Uri url,
            JsonTypeInfo<T> typeInfo,
            CancellationToken cancellationToken) =>
            PostAsync(key, keyId, url, string.Empty, typeInfo, cancellationToken);

        /// <summary>POST-as-GET returning the raw body (certificate PEM chain).</summary>
        /// <param name="key">Account key that signs the JWS.</param>
        /// <param name="keyId">Account URL (<c>kid</c>).</param>
        /// <param name="url">Certificate URL.</param>
        /// <param name="accept"><c>Accept</c> media type sent with the POST.</param>
        /// <param name="cancellationToken">Cancels the request.</param>
        /// <returns>The raw body and parsed Link headers. The body is not deserialized as JSON.</returns>
        /// <exception cref="AcmeCaException">Thrown when badNonce persists for <see cref="MaxBadNonceRetries"/> attempts.</exception>
        internal async Task<AcmeRawResponse> PostAsGetRawAsync(
            AcmeAccountKey key,
            string keyId,
            Uri url,
            string accept,
            CancellationToken cancellationToken)
        {
            for (int attempt = 0; attempt < MaxBadNonceRetries; attempt++)
            {
                string nonce = await TakeNonceAsync(cancellationToken).ConfigureAwait(false);
                string jws = JsonWebSignature.Encode(key, url, nonce, keyId, string.Empty);

                try
                {
                    AcmeResponse<object> response = await SendWithRetryAsync<object>(
                        () => CreateJwsRequest(url, jws, accept),
                        typeInfo: null,
                        cancellationToken).ConfigureAwait(false);

                    return new AcmeRawResponse(response.RawBody, response.Links);
                }
                catch (AcmeCaException ex) when (ex.ErrorType == AcmeErrorTypes.BadNonce)
                {
                    AcmeLogMessages.AcmeBadNonceRetry(_logger, url);
                }
            }

            throw new AcmeCaException($"The certificate authority repeatedly rejected the replay nonce for '{url}'.");
        }

        /// <summary>
        /// Builds a POST whose body is the compact JWS and whose content type is exactly <see cref="JoseContentType"/>.
        /// </summary>
        /// <param name="url">Request URL.</param>
        /// <param name="jws">Serialized JWS JSON.</param>
        /// <param name="accept">Optional <c>Accept</c> value. <see langword="null"/> sends no Accept header.</param>
        /// <returns>The request. The caller owns disposal.</returns>
        private static HttpRequestMessage CreateJwsRequest(Uri url, string jws, string? accept)
        {
            var content = new StringContent(jws, Encoding.UTF8);

            // RFC 8555 §6.2 requires exactly "application/jose+json" (no charset parameter).
            content.Headers.ContentType = new MediaTypeHeaderValue(JoseContentType);

            var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };

            if (accept is not null)
            {
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
            }

            return request;
        }

        /// <summary>
        /// Returns a cached replay nonce, or HEAD <c>newNonce</c> when the pool is empty.
        /// A failed HEAD invalidates the directory cache.
        /// </summary>
        /// <param name="cancellationToken">Cancels the directory fetch and the HEAD.</param>
        /// <returns>One nonce string.</returns>
        /// <exception cref="AcmeCaException">Thrown when <c>newNonce</c> is missing or the response has no <c>Replay-Nonce</c>.</exception>
        private async Task<string> TakeNonceAsync(CancellationToken cancellationToken)
        {
            if (_nonces.TryPop(out string? nonce))
            {
                return nonce;
            }

            AcmeDirectoryResource directory = await GetDirectoryAsync(cancellationToken).ConfigureAwait(false);
            Uri newNonce = directory.NewNonce
                ?? throw new AcmeCaException("The ACME directory does not advertise a newNonce endpoint.");

            using var request = new HttpRequestMessage(HttpMethod.Head, newNonce);
            using HttpClient client = CreateClient();

            HttpResponseMessage response;
            try
            {
                response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                       && !cancellationToken.IsCancellationRequested)
            {
                InvalidateDirectory();
                throw;
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    InvalidateDirectory();
                }

                CaptureNonce(response);
            }

            if (_nonces.TryPop(out nonce))
            {
                return nonce;
            }

            throw new AcmeCaException("The certificate authority did not supply a replay nonce.");
        }

        /// <summary>
        /// Sends a request and retries transport failures and HTTP 500/502/503/504.
        /// <see cref="AcmeErrorTypes.BadNonce"/> is not retried here.
        /// HTTP 404 invalidates the directory cache. Caller cancellation is not retried.
        /// </summary>
        /// <typeparam name="T">Deserialized success body.</typeparam>
        /// <param name="requestFactory">Builds a fresh request for each attempt. The method disposes it.</param>
        /// <param name="typeInfo">Deserializer metadata. <see langword="null"/> or an empty body leaves <see cref="AcmeResponse{T}.Content"/> at its default.</param>
        /// <param name="cancellationToken">Cancels the send and any retry delay.</param>
        /// <returns>The successful response, including raw body and link metadata.</returns>
        private async Task<AcmeResponse<T>> SendWithRetryAsync<T>(
            Func<HttpRequestMessage> requestFactory,
            JsonTypeInfo<T>? typeInfo,
            CancellationToken cancellationToken)
        {
            for (int attempt = 0; ; attempt++)
            {
                using HttpRequestMessage request = requestFactory();
                HttpResponseMessage response;

                try
                {
                    using HttpClient client = CreateClient();
                    response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                           && !cancellationToken.IsCancellationRequested)
                {
                    if (attempt >= MaxTransientRetries)
                    {
                        InvalidateDirectory();
                        throw new AcmeCaException(
                            $"The request to '{request.RequestUri}' failed after {attempt + 1} attempts.",
                            ex);
                    }

                    TimeSpan backoff = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                    AcmeLogMessages.AcmeTransportRetry(_logger, backoff, ex);
                    await Task.Delay(backoff, _time, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                using (response)
                {
                    CaptureNonce(response);
                    string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

                    DateTimeOffset? retryAfter = ParseRetryAfter(response);

                    if (response.IsSuccessStatusCode)
                    {
                        T? content = typeInfo is null || body.Length == 0 ? default : Deserialize(body, typeInfo);
                        return new AcmeResponse<T>(
                            response.StatusCode,
                            content,
                            response.Headers.Location,
                            ParseLinks(response),
                            body,
                            retryAfter);
                    }

                    AcmeProblem? problem = TryParseProblem(body);

                    if (response.StatusCode == HttpStatusCode.NotFound)
                    {
                        InvalidateDirectory();
                    }

                    if (attempt < MaxTransientRetries && ShouldRetry(response.StatusCode, problem))
                    {
                        TimeSpan delay = retryAfter is { } retryAt
                            ? retryAt - _time.GetUtcNow()
                            : TimeSpan.FromSeconds(Math.Pow(2, attempt));

                        if (delay > TimeSpan.Zero && delay <= MaxRetryDelay)
                        {
                            AcmeLogMessages.AcmeServerErrorRetry(_logger, (int)response.StatusCode, delay);
                            await Task.Delay(delay, _time, cancellationToken).ConfigureAwait(false);
                            continue;
                        }
                    }

                    throw CreateException(response.StatusCode, problem, retryAfter, request.RequestUri, body);
                }
            }
        }

        /// <summary>
        /// Returns whether the status is 500, 502, 503, or 504 and the problem is not <see cref="AcmeErrorTypes.BadNonce"/>.
        /// </summary>
        /// <param name="statusCode">HTTP status.</param>
        /// <param name="problem">Parsed problem, or <see langword="null"/> when the body was not a problem document.</param>
        /// <returns><see langword="true"/> when another attempt may be made.</returns>
        private static bool ShouldRetry(HttpStatusCode statusCode, AcmeProblem? problem)
        {
            if (problem?.Type == AcmeErrorTypes.BadNonce)
            {
                return false;
            }

            return statusCode is HttpStatusCode.InternalServerError
                or HttpStatusCode.BadGateway
                or HttpStatusCode.ServiceUnavailable
                or HttpStatusCode.GatewayTimeout;
        }

        /// <summary>
        /// Builds <see cref="AcmeCaRateLimitException"/> for HTTP 429 or <see cref="AcmeErrorTypes.RateLimited"/>;
        /// otherwise <see cref="AcmeCaException"/>.
        /// </summary>
        /// <param name="statusCode">HTTP status.</param>
        /// <param name="problem">Parsed problem, or <see langword="null"/>.</param>
        /// <param name="retryAfter">Absolute Retry-After time, or <see langword="null"/>.</param>
        /// <param name="requestUri">Request URI included in the message.</param>
        /// <param name="body">Raw body. Used as detail only when no problem detail exists, truncated to 512 characters.</param>
        /// <returns>The exception to throw. It is not thrown by this method.</returns>
        private static AcmeCaException CreateException(
            HttpStatusCode statusCode,
            AcmeProblem? problem,
            DateTimeOffset? retryAfter,
            Uri? requestUri,
            string body)
        {
            string detail = problem?.Detail ?? (body.Length <= 512 ? body : body[..512]);
            string message = string.Create(
                CultureInfo.InvariantCulture,
                $"The certificate authority rejected the request to '{requestUri}' with status {(int)statusCode}: {detail}");

            if (statusCode == HttpStatusCode.TooManyRequests || problem?.Type == AcmeErrorTypes.RateLimited)
            {
                return new AcmeCaRateLimitException(message, problem?.Detail, retryAfter);
            }

            return new AcmeCaException(message, problem?.Type, problem?.Detail, (int)statusCode);
        }

        /// <summary>Deserializes a success body. A <see cref="JsonException"/> becomes <see cref="AcmeCaException"/>.</summary>
        /// <typeparam name="T">Body type.</typeparam>
        /// <param name="body">Response text.</param>
        /// <param name="typeInfo">Source-generated metadata.</param>
        /// <returns>The deserialized value, which may be <see langword="null"/>.</returns>
        private static T? Deserialize<T>(string body, JsonTypeInfo<T> typeInfo)
        {
            try
            {
                return JsonSerializer.Deserialize(body, typeInfo);
            }
            catch (JsonException ex)
            {
                throw new AcmeCaException("The certificate authority returned a response that could not be parsed.", ex);
            }
        }

        /// <summary>Parses a problem document. An empty body or invalid JSON returns <see langword="null"/>.</summary>
        /// <param name="body">Response text.</param>
        /// <returns>The problem, or <see langword="null"/> when it cannot be read.</returns>
        private static AcmeProblem? TryParseProblem(string body)
        {
            if (body.Length == 0)
            {
                return null;
            }

            try
            {
                return JsonSerializer.Deserialize(body, AcmeJsonContext.Default.AcmeProblem);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>Pushes every non-empty <c>Replay-Nonce</c> header value onto <see cref="_nonces"/>.</summary>
        /// <param name="response">Response that may contain the header.</param>
        private void CaptureNonce(HttpResponseMessage response)
        {
            if (!response.Headers.TryGetValues("Replay-Nonce", out IEnumerable<string>? values))
            {
                return;
            }

            foreach (string value in values)
            {
                if (!string.IsNullOrEmpty(value))
                {
                    _nonces.Push(value);
                }
            }
        }

        /// <summary>
        /// Converts <c>Retry-After</c> to an absolute UTC time using <see cref="_time"/> when the header is a delta.
        /// </summary>
        /// <param name="response">Response that may contain the header.</param>
        /// <returns>The absolute time, or <see langword="null"/> when the header is absent.</returns>
        private DateTimeOffset? ParseRetryAfter(HttpResponseMessage response)
        {
            RetryConditionHeaderValue? header = response.Headers.RetryAfter;
            if (header is null)
            {
                return null;
            }

            if (header.Date is { } date)
            {
                return date;
            }

            return header.Delta is { } delta ? _time.GetUtcNow() + delta : null;
        }

        /// <summary>Parses RFC 8288 <c>Link</c> headers into absolute URL and relation pairs. Malformed entries are skipped.</summary>
        /// <param name="response">Response that may contain Link headers.</param>
        /// <returns>The parsed links. Empty when the header is absent.</returns>
        private static List<AcmeLink> ParseLinks(HttpResponseMessage response)
        {
            if (!response.Headers.TryGetValues("Link", out IEnumerable<string>? values))
            {
                return [];
            }

            var links = new List<AcmeLink>();
            foreach (string value in values)
            {
                foreach (string candidate in SplitLinkHeader(value))
                {
                    int start = candidate.IndexOf('<', StringComparison.Ordinal);
                    int end = candidate.IndexOf('>', StringComparison.Ordinal);
                    if (start < 0 || end <= start)
                    {
                        continue;
                    }

                    if (Uri.TryCreate(candidate[(start + 1)..end], UriKind.Absolute, out Uri? url))
                    {
                        links.Add(new AcmeLink(url, ExtractRelation(candidate[(end + 1)..])));
                    }
                }
            }

            return links;
        }

        /// <summary>Splits a Link header on commas that are outside angle brackets.</summary>
        /// <param name="value">One Link header value, which may contain several links.</param>
        /// <returns>The comma-separated segments, including empty segments when present.</returns>
        private static IEnumerable<string> SplitLinkHeader(string value)
        {
            int depth = 0;
            int start = 0;

            for (int i = 0; i < value.Length; i++)
            {
                switch (value[i])
                {
                    case '<':
                        depth++;
                        break;
                    case '>':
                        depth--;
                        break;
                    case ',' when depth == 0:
                        yield return value[start..i];
                        start = i + 1;
                        break;
                }
            }

            if (start < value.Length)
            {
                yield return value[start..];
            }
        }

        /// <summary>Reads the <c>rel</c> parameter. Returns an empty string when it is absent.</summary>
        /// <param name="parameters">The text after the closing angle bracket of one link.</param>
        /// <returns>The relation token with surrounding quotes removed.</returns>
        private static string ExtractRelation(string parameters)
        {
            foreach (string part in parameters.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!part.StartsWith("rel", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                int equals = part.IndexOf('=', StringComparison.Ordinal);
                if (equals >= 0)
                {
                    return part[(equals + 1)..].Trim().Trim('"');
                }
            }

            return string.Empty;
        }
    }
}
