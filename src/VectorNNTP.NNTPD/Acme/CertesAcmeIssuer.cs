using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Certes;
using Certes.Acme;
using Certes.Acme.Resource;
using Microsoft.Extensions.Options;
using VectorNNTP.NNTPD.Configuration;

namespace VectorNNTP.NNTPD.Acme;

/// <summary>
/// ACME v2 issuer using Certes with DNS-01 via <see cref="Dns01Solver"/>.
/// </summary>
/// <remarks>
/// Certes was selected as a maintained ACME v2 client with DNS-01 support, account key
/// import/export (including DER), order finalization, and .NET Standard 2.0 compatibility
/// suitable for <c>net10.0</c>. Protocol PEM may appear transiently inside Certes; the
/// persisted TLS credential is PKCS#12/PFX. The ACME account key remains PKCS#8 DER.
/// After DNS-01 challenges are triggered, the issuer polls until the ACME order is
/// <c>ready</c> (or fails) before calling <c>Generate</c>/finalize.
/// </remarks>
public sealed class CertesAcmeIssuer : ICertificateIssuer
{
    /// <summary>Named <see cref="IHttpClientFactory"/> client for ACME HTTP.</summary>
    public const string HttpClientName = "AcmeDirectory";

    private const int KeySizeBits = 2048;

    private readonly IOptions<NntpdOptions> _options;
    private readonly AccountStore _accountStore;
    private readonly Dns01Solver _dnsSolver;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<CertesAcmeIssuer> _logger;
    private readonly TimeSpan _readinessTimeout;
    private readonly TimeSpan _readinessInterval;

    /// <summary>Initializes a new instance of the <see cref="CertesAcmeIssuer"/> class.</summary>
    public CertesAcmeIssuer(
        IOptions<NntpdOptions> options,
        AccountStore accountStore,
        Dns01Solver dnsSolver,
        IHttpClientFactory httpClientFactory,
        ILogger<CertesAcmeIssuer> logger)
        : this(
            options,
            accountStore,
            dnsSolver,
            httpClientFactory,
            logger,
            readinessTimeout: null,
            readinessInterval: null)
    {
    }

    /// <summary>Test constructor with injectable readiness poll timing.</summary>
    internal CertesAcmeIssuer(
        IOptions<NntpdOptions> options,
        AccountStore accountStore,
        Dns01Solver dnsSolver,
        IHttpClientFactory httpClientFactory,
        ILogger<CertesAcmeIssuer> logger,
        TimeSpan? readinessTimeout,
        TimeSpan? readinessInterval)
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
        var accountKey = KeyFactory.FromDer(account.PrivateKeyDer);
        var http = new AcmeHttpClient(directoryUri, _httpClientFactory.CreateClient(HttpClientName));
        var acme = new AcmeContext(directoryUri, accountKey, http);

        _ = await acme.Account().ConfigureAwait(false);

        IKey certKey = KeyFactory.NewKey(KeyAlgorithm.RS256);
        IOrderContext order;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            order = await acme.NewOrder(domains.ToArray()).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new AcmeOrderException("new_order_failed", AcmeProblemDiagnostics.FormatException(ex));
        }

        var authzs = (await order.Authorizations().ConfigureAwait(false)).ToList();
        var specs = new List<Dns01ChallengeSpec>();
        var challenges = new List<IChallengeContext>();

        foreach (var authz in authzs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var resource = await authz.Resource().ConfigureAwait(false);
            var domain = resource.Identifier?.Value
                ?? throw new AcmeOrderException("no_dns01_challenge", "missing identifier");
            var dns = await authz.Dns().ConfigureAwait(false)
                ?? throw new AcmeOrderException("no_dns01_challenge", domain);
            var dnsTxt = accountKey.DnsTxt(dns.Token);
            specs.Add(new Dns01ChallengeSpec(domain, dnsTxt));
            challenges.Add(dns);
        }

        try
        {
            await _dnsSolver.PlaceAsync(specs, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("ACME DNS-01 TXT records placed for {DomainCount} identifier(s)", specs.Count);

            await _dnsSolver.WaitPropagatedAsync(specs, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("ACME DNS-01 authoritative visibility confirmed");

            foreach (var challenge in challenges)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _ = await challenge.Validate().ConfigureAwait(false);
            }

            _logger.LogInformation(
                "ACME DNS-01 challenges triggered; waiting for authorization (timeout={TimeoutSeconds}s)",
                (int)_readinessTimeout.TotalSeconds);

            await WaitForOrderReadyAsync(order, authzs, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("ACME order ready for finalization");

            cancellationToken.ThrowIfCancellationRequested();
            CertificateChain certChain;
            try
            {
                // Certes only retries while status == processing; LE staging often needs more than the default (1).
                certChain = await order.Generate(
                        new CsrInfo { CommonName = domains[0] },
                        certKey,
                        preferredChain: null,
                        retryCount: 30)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is Certes.AcmeException or AcmeRequestException)
            {
                var orderStatus = "unknown";
                try
                {
                    var resource = await order.Resource().ConfigureAwait(false);
                    orderStatus = resource.Status?.ToString() ?? "unknown";
                }
                catch (Exception statusEx) when (statusEx is not OperationCanceledException)
                {
                    // Best-effort status for diagnostics.
                }

                throw new AcmeOrderException(
                    "finalize_failed",
                    $"{AcmeProblemDiagnostics.FormatException(ex)} order_status={orderStatus}");
            }

            _logger.LogInformation("ACME certificate finalized");

            await CleanupDnsAsync(cancellationToken).ConfigureAwait(false);

            var pfxBytes = BuildPfx(certChain, certKey, password);
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
        IOrderContext order,
        IReadOnlyList<IAuthorizationContext> authorizations,
        CancellationToken cancellationToken)
    {
        await AcmeOrderReadiness.WaitUntilReadyAsync(
                async ct => await CaptureOrderViewAsync(order, authorizations, ct).ConfigureAwait(false),
                _readinessTimeout,
                _readinessInterval,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<AcmeOrderReadiness.OrderView> CaptureOrderViewAsync(
        IOrderContext order,
        IReadOnlyList<IAuthorizationContext> authorizations,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var orderResource = await order.Resource().ConfigureAwait(false);
        var authzViews = new List<AcmeOrderReadiness.AuthorizationView>(authorizations.Count);
        foreach (var authz in authorizations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var resource = await authz.Resource().ConfigureAwait(false);
            var dnsChallenge = resource.Challenges?
                .FirstOrDefault(static c =>
                    string.Equals(c.Type, ChallengeTypes.Dns01, StringComparison.OrdinalIgnoreCase));
            var error = dnsChallenge?.Error
                ?? resource.Challenges?.Select(static c => c.Error).FirstOrDefault(static e => e is not null);
            int? errorStatus = null;
            if (error is not null && error.Status != default)
            {
                errorStatus = (int)error.Status;
            }

            authzViews.Add(
                new AcmeOrderReadiness.AuthorizationView(
                    resource.Identifier?.Value,
                    resource.Status?.ToString() ?? string.Empty,
                    error?.Type,
                    error?.Detail,
                    errorStatus));
        }

        return new AcmeOrderReadiness.OrderView(
            orderResource.Status?.ToString() ?? string.Empty,
            authzViews);
    }

    private AcmeAccountState EnsureAccount(NntpdOptions options, CancellationToken cancellationToken)
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

    private (string AccountUri, string RegistrationBody) RegisterAccount(NntpdOptions options, byte[] keyDer)
    {
        try
        {
            var directoryUri = new Uri(options.AcmeDirectoryUrl.Trim(), UriKind.Absolute);
            var accountKey = KeyFactory.FromDer(keyDer);
            var http = new AcmeHttpClient(directoryUri, _httpClientFactory.CreateClient(HttpClientName));
            var acme = new AcmeContext(directoryUri, accountKey, http);
            var account = acme.NewAccount(options.AcmeEmail.Trim(), termsOfServiceAgreed: true)
                .GetAwaiter()
                .GetResult();
            var location = account.Location?.ToString()
                ?? throw new AcmeAccountException("registration_failed", "empty account_uri");
            _logger.LogInformation("ACME account registered (uri configured).");
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

    private static byte[] BuildPfx(CertificateChain chain, IKey certKey, string password)
    {
        var leafPem = chain.Certificate.ToPem();
        if (string.IsNullOrWhiteSpace(leafPem))
        {
            throw new AcmeOrderException("empty_certificate", "missing_leaf");
        }

        using var leaf = X509Certificate2.CreateFromPem(leafPem);
        using var rsa = RSA.Create();
        rsa.ImportPkcs8PrivateKey(certKey.ToDer(), out _);
        using var leafWithKey = leaf.CopyWithPrivateKey(rsa);

        var intermediates = new List<X509Certificate2>();
        try
        {
            foreach (var issuer in chain.Issuers)
            {
                var issuerPem = issuer.ToPem();
                if (string.IsNullOrWhiteSpace(issuerPem))
                {
                    continue;
                }

                intermediates.Add(X509Certificate2.CreateFromPem(issuerPem));
            }

            return PfxCrypto.ExportPfx(leafWithKey, intermediates, password);
        }
        finally
        {
            foreach (var intermediate in intermediates)
            {
                intermediate.Dispose();
            }
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
