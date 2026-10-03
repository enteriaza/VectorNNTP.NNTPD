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
        private const string JoseContentType = "application/jose+json";
        private const int MaxBadNonceRetries = 5;
        private const int MaxTransientRetries = 4;

        private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(5);

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly string _httpClientName;
        private readonly Uri _directoryUri;
        private readonly ILogger _logger;
        private readonly TimeProvider _time;
        private readonly ConcurrentStack<string> _nonces = new();

        private Task<AcmeDirectoryResource>? _directory;

        /// <summary>Initializes a new instance of the <see cref="AcmeHttpTransport"/> class.</summary>
        public AcmeHttpTransport(
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

        private HttpClient CreateClient() => _httpClientFactory.CreateClient(_httpClientName);

        /// <summary>Returns the cached ACME directory, fetching it on first use.</summary>
        public Task<AcmeDirectoryResource> GetDirectoryAsync(CancellationToken cancellationToken)
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

        private void InvalidateDirectory() => Volatile.Write(ref _directory, null);

        private async Task<AcmeDirectoryResource> FetchDirectoryAsync(CancellationToken cancellationToken)
        {
            AcmeResponse<AcmeDirectoryResource> response = await SendWithRetryAsync(
                () => new HttpRequestMessage(HttpMethod.Get, _directoryUri),
                AcmeJsonContext.Default.AcmeDirectoryResource,
                cancellationToken).ConfigureAwait(false);

            return response.Content ?? throw new AcmeCaException("The ACME directory response was empty.");
        }

        /// <summary>POSTs a JWS-signed payload, retrying on badNonce.</summary>
        public async Task<AcmeResponse<T>> PostAsync<T>(
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
        public Task<AcmeResponse<T>> PostAsGetAsync<T>(
            AcmeAccountKey key,
            string keyId,
            Uri url,
            JsonTypeInfo<T> typeInfo,
            CancellationToken cancellationToken) =>
            PostAsync(key, keyId, url, string.Empty, typeInfo, cancellationToken);

        /// <summary>POST-as-GET returning the raw body (certificate PEM chain).</summary>
        public async Task<AcmeRawResponse> PostAsGetRawAsync(
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

        private static IReadOnlyList<AcmeLink> ParseLinks(HttpResponseMessage response)
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
