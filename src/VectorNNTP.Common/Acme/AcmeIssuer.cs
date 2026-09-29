using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Options;
using VectorNNTP.NNTPD.Acme.Protocol;
using VectorNNTP.NNTPD.Configuration;

namespace VectorNNTP.NNTPD.Acme;

/// <summary>
/// ACME v2 issuer using the owned protocol client with DNS-01 via <see cref="Dns01Solver"/>.
/// </summary>
/// <remarks>
/// Account keys remain PKCS#8 DER on disk. Persisted TLS credentials remain PKCS#12/PFX via BCL.
/// After DNS-01 challenges are triggered, the issuer polls until the ACME order is
/// <c>ready</c> (or fails) before finalize.
/// </remarks>
public sealed class AcmeIssuer : ICertificateIssuer
{
    /// <summary>Named <see cref="IHttpClientFactory"/> client for ACME HTTP.</summary>
    public const string HttpClientName = "AcmeDirectory";

    private const int KeySizeBits = 2048;

    private readonly IOptions<AcmeCloudflareOptions> _options;
    private readonly AccountStore _accountStore;
    private readonly Dns01Solver _dnsSolver;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AcmeIssuer> _logger;
    private readonly AcmeTransactionJournal? _journal;
    private readonly TimeSpan _readinessTimeout;
    private readonly TimeSpan _readinessInterval;

    /// <summary>Initializes a new instance of the <see cref="AcmeIssuer"/> class.</summary>
    public AcmeIssuer(
        IOptions<AcmeCloudflareOptions> options,
        AccountStore accountStore,
        Dns01Solver dnsSolver,
        IHttpClientFactory httpClientFactory,
        ILogger<AcmeIssuer> logger,
        AcmeTransactionJournal? journal = null)
        : this(
            options,
            accountStore,
            dnsSolver,
            httpClientFactory,
            logger,
            readinessTimeout: null,
            readinessInterval: null,
            journal: journal)
    {
    }

    /// <summary>Test constructor with injectable readiness poll timing.</summary>
    internal AcmeIssuer(
        IOptions<AcmeCloudflareOptions> options,
        AccountStore accountStore,
        Dns01Solver dnsSolver,
        IHttpClientFactory httpClientFactory,
        ILogger<AcmeIssuer> logger,
        TimeSpan? readinessTimeout,
        TimeSpan? readinessInterval,
        AcmeTransactionJournal? journal = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(accountStore);
        ArgumentNullException.ThrowIfNull(dnsSolver);
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options;
        _accountStore = accountStore;
        _dnsSolver = dnsSolver;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _journal = journal;
        _readinessTimeout = readinessTimeout ?? AcmeOrderReadiness.DefaultTimeout;
        _readinessInterval = readinessInterval ?? AcmeOrderReadiness.DefaultInterval;
    }

    /// <inheritdoc />
    public async Task<CertificateMaterial> IssueAsync(
        IReadOnlyList<string> domains,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domains);
        if (domains.Count == 0)
        {
            throw new AcmeOrderException("missing_identities", "no domains");
        }

        var options = _options.Value;
        var renewalThreshold = TimeSpan.FromDays(options.AcmeRenewalThresholdDays);
        var password = options.AcmeCertificatePassword;
        var account = EnsureAccount(options, cancellationToken);
        var directoryUri = new Uri(options.AcmeDirectoryUrl.Trim(), UriKind.Absolute);

        using AcmeAccountKey accountKey = AcmeAccountKey.ImportPkcs8Der(account.PrivateKeyDer);
        var http = new AcmeHttpTransport(
            _httpClientFactory,
            HttpClientName,
            directoryUri,
            _logger,
            TimeProvider.System);
        var acme = new AcmeClient(http, accountKey, _logger, TimeProvider.System);
        acme.BindExistingAccount(account.AccountUri);

        using AcmeCertificateKey certKey = AcmeCertificateKey.CreateRsa(KeySizeBits);
        AcmeOrder order;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var identifiers = domains
                .Select(static d => new AcmeIdentifier { Type = AcmeIdentifierTypes.Dns, Value = d })
                .ToArray();
            order = await acme.CreateOrderAsync(identifiers, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new AcmeOrderException("new_order_failed", AcmeProblemDiagnostics.FormatException(ex));
        }

        if (order.Resource.Authorizations is null || order.Resource.Authorizations.Count == 0)
        {
            throw new AcmeOrderException("no_dns01_challenge", "missing authorizations");
        }

        var authzUrls = order.Resource.Authorizations.ToArray();
        var specs = new List<Dns01ChallengeSpec>(authzUrls.Length);
        var challengeUrls = new List<Uri>(authzUrls.Length);

        try
        {
            foreach (Uri authzUrl in authzUrls)
            {
                cancellationToken.ThrowIfCancellationRequested();
                AcmeAuthorizationResource resource = await acme.GetAuthorizationAsync(authzUrl, cancellationToken)
                    .ConfigureAwait(false);
                var domain = resource.Identifier?.Value
                    ?? throw new AcmeOrderException("no_dns01_challenge", "missing identifier");
                AcmeChallengeResource dns = resource.Challenges?.FirstOrDefault(static c =>
                        string.Equals(c.Type, "dns-01", StringComparison.OrdinalIgnoreCase))
                    ?? throw new AcmeOrderException("no_dns01_challenge", domain);
                if (string.IsNullOrWhiteSpace(dns.Token) || dns.Url is null)
                {
                    throw new AcmeOrderException("no_dns01_challenge", domain);
                }

                specs.Add(new Dns01ChallengeSpec(domain, accountKey.GetDnsRecordValue(dns.Token)));
                challengeUrls.Add(dns.Url);
            }

            await _dnsSolver.PlaceAsync(specs, cancellationToken).ConfigureAwait(false);
            AcmeLogMessages.Dns01RecordsPlaced(_logger, specs.Count);

            await _dnsSolver.WaitPropagatedAsync(specs, cancellationToken).ConfigureAwait(false);
            AcmeLogMessages.Dns01VisibilityConfirmed(_logger);

            foreach (Uri challengeUrl in challengeUrls)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await acme.SubmitChallengeAsync(challengeUrl, cancellationToken).ConfigureAwait(false);
            }

            _journal?.RecordAcmeValidationStarted();
            AcmeLogMessages.Dns01ChallengesTriggered(_logger, (int)_readinessTimeout.TotalSeconds);

            await WaitForOrderReadyAsync(acme, order.Location, authzUrls, cancellationToken).ConfigureAwait(false);
            _journal?.RecordAcmeValidationSucceeded();
            AcmeLogMessages.OrderReady(_logger);

            cancellationToken.ThrowIfCancellationRequested();
            string certificatePem;
            try
            {
                Uri finalizeUrl = order.Resource.Finalize
                    ?? throw new AcmeOrderException("finalize_failed", "missing finalize url");
                byte[] csr = CsrBuilder.CreateDnsSigningRequest(domains, certKey);

                // Refresh finalize URL from the ready order resource when present.
                AcmeOrderResource readyOrder = await acme.GetOrderAsync(order.Location, cancellationToken)
                    .ConfigureAwait(false);
                finalizeUrl = readyOrder.Finalize ?? finalizeUrl;

                _ = await acme.FinalizeOrderAsync(finalizeUrl, csr, cancellationToken).ConfigureAwait(false);
                AcmeOrderResource validOrder = await acme.WaitForOrderAsync(
                        order.Location,
                        timeout: TimeSpan.FromMinutes(3),
                        pollInterval: TimeSpan.FromSeconds(2),
                        cancellationToken)
                    .ConfigureAwait(false);
                Uri certificateUrl = validOrder.Certificate
                    ?? throw new AcmeOrderException("finalize_failed", "missing certificate url");
                AcmeCertificate certificate = await acme.DownloadCertificateAsync(certificateUrl, cancellationToken)
                    .ConfigureAwait(false);
                certificatePem = certificate.Pem;
            }
            catch (Exception ex) when (ex is AcmeCaException or CryptographicException)
            {
                var orderStatus = "unknown";
                try
                {
                    AcmeOrderResource resource = await acme.GetOrderAsync(order.Location, CancellationToken.None)
                        .ConfigureAwait(false);
                    orderStatus = resource.Status ?? "unknown";
                }
                catch (Exception statusEx) when (statusEx is not OperationCanceledException)
                {
                    // Best-effort status for diagnostics.
                }

                throw new AcmeOrderException(
                    "finalize_failed",
                    $"{AcmeProblemDiagnostics.FormatException(ex)} order_status={orderStatus}");
            }

            AcmeLogMessages.CertificateFinalized(_logger);

            await CleanupDnsAsync(cancellationToken).ConfigureAwait(false);

            var pfxBytes = BuildPfx(certificatePem, certKey, password);
            return CertificateValidator.RequireValidPfx(
                pfxBytes,
                password,
                domains,
                renewalThreshold);
        }
        catch (OperationCanceledException)
        {
            await BestEffortDnsCleanupAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (AcmeException)
        {
            await BestEffortDnsCleanupAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await BestEffortDnsCleanupAsync(cancellationToken).ConfigureAwait(false);
            throw new AcmeOrderException("challenge_failed", AcmeProblemDiagnostics.FormatException(ex));
        }
    }

    private async Task WaitForOrderReadyAsync(
        AcmeClient acme,
        Uri orderUrl,
        IReadOnlyList<Uri> authorizationUrls,
        CancellationToken cancellationToken)
    {
        await AcmeOrderReadiness.WaitUntilReadyAsync(
                async ct => await CaptureOrderViewAsync(acme, orderUrl, authorizationUrls, ct).ConfigureAwait(false),
                _readinessTimeout,
                _readinessInterval,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<AcmeOrderReadiness.OrderView> CaptureOrderViewAsync(
        AcmeClient acme,
        Uri orderUrl,
        IReadOnlyList<Uri> authorizationUrls,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AcmeOrderResource orderResource = await acme.GetOrderAsync(orderUrl, cancellationToken).ConfigureAwait(false);
        var authzViews = new List<AcmeOrderReadiness.AuthorizationView>(authorizationUrls.Count);
        foreach (Uri authzUrl in authorizationUrls)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AcmeAuthorizationResource resource = await acme.GetAuthorizationAsync(authzUrl, cancellationToken)
                .ConfigureAwait(false);
            AcmeChallengeResource? dnsChallenge = resource.Challenges?
                .FirstOrDefault(static c =>
                    string.Equals(c.Type, "dns-01", StringComparison.OrdinalIgnoreCase));
            AcmeProblem? error = dnsChallenge?.Error
                ?? resource.Challenges?.Select(static c => c.Error).FirstOrDefault(static e => e is not null);

            authzViews.Add(
                new AcmeOrderReadiness.AuthorizationView(
                    resource.Identifier?.Value,
                    resource.Status ?? string.Empty,
                    error?.Type,
                    error?.Detail,
                    error?.Status));
        }

        return new AcmeOrderReadiness.OrderView(
            orderResource.Status ?? string.Empty,
            authzViews);
    }

    private AcmeAccountState EnsureAccount(AcmeCloudflareOptions options, CancellationToken cancellationToken)
    {
        return _accountStore.EnsureRegistered(
            options.AcmeDirectoryUrl.Trim(),
            GenerateAccountKeyDer,
            keyDer => RegisterAccount(options, keyDer),
            cancellationToken);
    }

    private static byte[] GenerateAccountKeyDer()
    {
        using var rsa = RSA.Create(KeySizeBits);
        return rsa.ExportPkcs8PrivateKey();
    }

    private (string AccountUri, string RegistrationBody) RegisterAccount(AcmeCloudflareOptions options, byte[] keyDer)
    {
        try
        {
            var directoryUri = new Uri(options.AcmeDirectoryUrl.Trim(), UriKind.Absolute);
            using AcmeAccountKey accountKey = AcmeAccountKey.ImportPkcs8Der(keyDer);
            var http = new AcmeHttpTransport(
                _httpClientFactory,
                HttpClientName,
                directoryUri,
                _logger,
                TimeProvider.System);
            var acme = new AcmeClient(http, accountKey, _logger, TimeProvider.System);
            string email = options.AcmeEmail.Trim();
            IReadOnlyList<string> contacts = string.IsNullOrWhiteSpace(email)
                ? []
                : ["mailto:" + email];
            string location = acme.RegisterAccountAsync(
                    contacts,
                    termsOfServiceAgreed: true,
                    externalAccountBinding: null,
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            if (string.IsNullOrWhiteSpace(location))
            {
                throw new AcmeAccountException("registration_failed", "empty account_uri");
            }

            AcmeLogMessages.AccountRegistered(_logger);
            return (location, string.Empty);
        }
        catch (AcmeAccountException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new AcmeAccountException("registration_failed", AcmeProblemDiagnostics.FormatException(ex));
        }
    }

    private static byte[] BuildPfx(string certificateChainPem, AcmeCertificateKey certKey, string password)
    {
        if (string.IsNullOrWhiteSpace(certificateChainPem))
        {
            throw new AcmeOrderException("empty_certificate", "missing_leaf");
        }

        var chain = new X509Certificate2Collection();
        chain.ImportFromPem(certificateChainPem);
        if (chain.Count == 0)
        {
            throw new AcmeOrderException("empty_certificate", "missing_leaf");
        }

        string keyPem = certKey.ExportPem();
        using X509Certificate2 leafPem = chain[0];
        using X509Certificate2 leafWithKey = X509Certificate2.CreateFromPem(
            leafPem.ExportCertificatePem(),
            keyPem);

        var intermediates = new List<X509Certificate2>();
        try
        {
            for (var i = 1; i < chain.Count; i++)
            {
                intermediates.Add(chain[i]);
            }

            return PfxCrypto.ExportPfx(leafWithKey, intermediates, password);
        }
        finally
        {
            // leafPem/leafWithKey disposed via using; intermediates were borrowed from the collection.
        }
    }

    private async Task CleanupDnsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _dnsSolver.CleanupAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new AcmeOrderException("txt_cleanup_failed", AcmeProblemDiagnostics.FormatException(ex));
        }
    }

    private async Task BestEffortDnsCleanupAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _dnsSolver.CleanupAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Ignore clean-up cancel during best-effort.
        }
        catch
        {
            // Best-effort.
        }
    }
}
