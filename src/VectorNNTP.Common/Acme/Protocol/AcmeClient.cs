using System.Text;
using System.Text.Json;

namespace VectorNNTP.Common.Acme.Protocol
{
    /// <summary>
    /// ACME v2 protocol client for directory, account, order, authorization, challenge, finalize, and certificate download.
    /// </summary>
    internal sealed class AcmeClient
    {
        private const string PemChainContentType = "application/pem-certificate-chain";

        private static readonly TimeSpan MaxPollDelay = TimeSpan.FromSeconds(30);

        private readonly AcmeHttpTransport _http;
        private readonly AcmeAccountKey _accountKey;
        private readonly ILogger _logger;
        private readonly TimeProvider _time;

        private string? _keyId;

        /// <summary>Initializes a new instance of the <see cref="AcmeClient"/> class.</summary>
        internal AcmeClient(AcmeHttpTransport http, AcmeAccountKey accountKey, ILogger logger, TimeProvider time)
        {
            _http = http;
            _accountKey = accountKey;
            _logger = logger;
            _time = time;
        }

        /// <summary>Gets the account key used for JWS.</summary>
        private AcmeAccountKey AccountKey => _accountKey;

        /// <summary>Gets the bound account URL (kid), or throws if not registered/bound.</summary>
        internal string KeyId => _keyId ?? throw new InvalidOperationException("The ACME account has not been registered yet.");

        /// <summary>
        /// Binds a previously persisted ACME account URL (kid) without calling newAccount.
        /// </summary>
        internal void BindExistingAccount(string accountUrl)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(accountUrl);
            _keyId = accountUrl.Trim();
        }

        /// <summary>Fetches the ACME directory.</summary>
        private Task<AcmeDirectoryResource> GetDirectoryAsync(CancellationToken cancellationToken) =>
            _http.GetDirectoryAsync(cancellationToken);

        /// <summary>POST-as-GET for an order resource.</summary>
        internal async Task<AcmeOrderResource> GetOrderAsync(Uri orderUrl, CancellationToken cancellationToken)
        {
            AcmeResponse<AcmeOrderResource> response = await _http.PostAsGetAsync(
                _accountKey, KeyId, orderUrl, AcmeJsonContext.Default.AcmeOrderResource, cancellationToken)
                .ConfigureAwait(false);

            return response.Content ?? throw new AcmeCaException("The certificate authority returned an empty order.");
        }

        /// <summary>
        /// Registers a new account (or reuses one for this key) and returns the account Location URL.
        /// </summary>
        internal async Task<string> RegisterAccountAsync(
            IReadOnlyList<string> contacts,
            bool termsOfServiceAgreed,
            ExternalAccountBinding? externalAccountBinding,
            CancellationToken cancellationToken)
        {
            AcmeDirectoryResource directory = await _http.GetDirectoryAsync(cancellationToken).ConfigureAwait(false);
            Uri newAccount = directory.NewAccount
                ?? throw new AcmeCaException("The ACME directory does not advertise a newAccount endpoint.");

            string payload = BuildAccountPayload(_accountKey, contacts, termsOfServiceAgreed, externalAccountBinding, newAccount);

            AcmeResponse<AcmeAccountResource> response = await _http.PostAsync(
                _accountKey,
                keyId: null,
                newAccount,
                payload,
                AcmeJsonContext.Default.AcmeAccountResource,
                cancellationToken).ConfigureAwait(false);

            Uri location = response.Location
                ?? throw new AcmeCaException("The certificate authority did not return an account URL.");

            _keyId = location.AbsoluteUri;
            AcmeLogMessages.AcmeProtocolAccountRegistered(_logger, _keyId);
            return _keyId;
        }

        /// <summary>Creates a new order for the given identifiers.</summary>
        internal async Task<AcmeOrder> CreateOrderAsync(
            IReadOnlyList<AcmeIdentifier> identifiers,
            CancellationToken cancellationToken)
        {
            AcmeDirectoryResource directory = await _http.GetDirectoryAsync(cancellationToken).ConfigureAwait(false);
            Uri newOrder = directory.NewOrder
                ?? throw new AcmeCaException("The ACME directory does not advertise a newOrder endpoint.");

            string payload = BuildOrderPayload(identifiers);

            AcmeResponse<AcmeOrderResource> response = await _http.PostAsync(
                _accountKey,
                KeyId,
                newOrder,
                payload,
                AcmeJsonContext.Default.AcmeOrderResource,
                cancellationToken).ConfigureAwait(false);

            AcmeOrderResource order = response.Content
                ?? throw new AcmeCaException("The certificate authority returned an empty order.");
            Uri location = response.Location
                ?? throw new AcmeCaException("The certificate authority did not return an order URL.");

            return new AcmeOrder(location, order);
        }

        /// <summary>POST-as-GET for an authorization resource.</summary>
        internal async Task<AcmeAuthorizationResource> GetAuthorizationAsync(Uri authorizationUrl, CancellationToken cancellationToken)
        {
            AcmeResponse<AcmeAuthorizationResource> response = await _http.PostAsGetAsync(
                _accountKey, KeyId, authorizationUrl, AcmeJsonContext.Default.AcmeAuthorizationResource, cancellationToken)
                .ConfigureAwait(false);

            return response.Content ?? throw new AcmeCaException("The certificate authority returned an empty authorization.");
        }

        /// <summary>Submits a challenge for validation (empty JSON object payload).</summary>
        internal async Task SubmitChallengeAsync(Uri challengeUrl, CancellationToken cancellationToken) =>
            await _http.PostAsync(
                _accountKey, KeyId, challengeUrl, "{}", AcmeJsonContext.Default.AcmeChallengeResource, cancellationToken)
                .ConfigureAwait(false);

        /// <summary>Finalizes an order with a PKCS#10 CSR.</summary>
        internal async Task<AcmeOrderResource> FinalizeOrderAsync(
            Uri finalizeUrl,
            byte[] certificateSigningRequest,
            CancellationToken cancellationToken)
        {
            string payload = BuildFinalizePayload(certificateSigningRequest);

            AcmeResponse<AcmeOrderResource> response = await _http.PostAsync(
                _accountKey, KeyId, finalizeUrl, payload, AcmeJsonContext.Default.AcmeOrderResource, cancellationToken)
                .ConfigureAwait(false);

            return response.Content ?? throw new AcmeCaException("The certificate authority returned an empty order.");
        }

        /// <summary>Polls the order until status is <c>valid</c> or <c>invalid</c>.</summary>
        internal async Task<AcmeOrderResource> WaitForOrderAsync(
            Uri orderUrl,
            TimeSpan timeout,
            TimeSpan pollInterval,
            CancellationToken cancellationToken)
        {
            DateTimeOffset deadline = _time.GetUtcNow() + timeout;

            while (true)
            {
                AcmeResponse<AcmeOrderResource> response = await _http.PostAsGetAsync(
                    _accountKey, KeyId, orderUrl, AcmeJsonContext.Default.AcmeOrderResource, cancellationToken)
                    .ConfigureAwait(false);
                AcmeOrderResource order = response.Content
                    ?? throw new AcmeCaException("The certificate authority returned an empty order.");

                switch (order.Status)
                {
                    case AcmeStatus.Valid:
                        return order;
                    case AcmeStatus.Invalid:
                        throw new AcmeCaException(
                            $"The certificate authority rejected the order: {order.Error?.Detail ?? "no detail supplied"}",
                            order.Error?.Type,
                            order.Error?.Detail,
                            order.Error?.Status);
                    default:
                        break;
                }

                if (_time.GetUtcNow() >= deadline)
                {
                    throw new AcmeCaException("Timed out waiting for the certificate authority to issue the certificate.");
                }

                await Task.Delay(PollDelay(response.RetryAfter, pollInterval), _time, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        /// <summary>Downloads the PEM certificate chain (RFC 8555 §7.4.2).</summary>
        internal async Task<AcmeCertificate> DownloadCertificateAsync(Uri certificateUrl, CancellationToken cancellationToken)
        {
            AcmeRawResponse response = await _http.PostAsGetRawAsync(
                _accountKey, KeyId, certificateUrl, PemChainContentType, cancellationToken)
                .ConfigureAwait(false);

            var alternates = new List<Uri>();
            foreach (AcmeLink link in response.Links)
            {
                if (string.Equals(link.Relation, "alternate", StringComparison.OrdinalIgnoreCase))
                {
                    alternates.Add(link.Url);
                }
            }

            return new AcmeCertificate(response.Body, alternates);
        }

        private TimeSpan PollDelay(DateTimeOffset? retryAfter, TimeSpan pollInterval)
        {
            if (retryAfter is not { } at)
            {
                return pollInterval;
            }

            TimeSpan requested = at - _time.GetUtcNow();
            if (requested < pollInterval)
            {
                return pollInterval;
            }

            return requested > MaxPollDelay ? MaxPollDelay : requested;
        }

        private static string BuildAccountPayload(
            AcmeAccountKey accountKey,
            IReadOnlyList<string> contacts,
            bool termsOfServiceAgreed,
            ExternalAccountBinding? externalAccountBinding,
            Uri newAccountUrl)
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WriteBoolean("termsOfServiceAgreed", termsOfServiceAgreed);

                if (contacts.Count > 0)
                {
                    writer.WriteStartArray("contact");
                    foreach (string contact in contacts)
                    {
                        writer.WriteStringValue(contact);
                    }

                    writer.WriteEndArray();
                }

                writer.WriteEndObject();
            }

            if (externalAccountBinding is null)
            {
                return Encoding.UTF8.GetString(stream.ToArray());
            }

            string body = Encoding.UTF8.GetString(stream.ToArray());
            string binding = JsonWebSignature.EncodeHmac(
                externalAccountBinding.GetHmacKey(),
                externalAccountBinding.KeyId,
                newAccountUrl,
                accountKey.Jwk);

            return body[..^1] + ",\"externalAccountBinding\":" + binding + "}";
        }

        private static string BuildOrderPayload(IReadOnlyList<AcmeIdentifier> identifiers)
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WriteStartArray("identifiers");

                foreach (AcmeIdentifier identifier in identifiers)
                {
                    writer.WriteStartObject();
                    writer.WriteString("type", identifier.Type);
                    writer.WriteString("value", identifier.Value);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }

        private static string BuildFinalizePayload(byte[] certificateSigningRequest)
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WriteString("csr", Base64Url.Encode(certificateSigningRequest));
                writer.WriteEndObject();
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }
    }
}
