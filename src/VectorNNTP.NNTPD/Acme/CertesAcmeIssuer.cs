using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Certes;
using Certes.Acme;
using Microsoft.Extensions.Logging;
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

    /// <summary>Initializes a new instance of the <see cref="CertesAcmeIssuer"/> class.</summary>
    public CertesAcmeIssuer(
        IOptions<NntpdOptions> options,
        AccountStore accountStore,
        Dns01Solver dnsSolver,
        IHttpClientFactory httpClientFactory,
        ILogger<CertesAcmeIssuer> logger)
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
            throw new AcmeOrderException("new_order_failed", ex.GetType().Name);
        }

        var authzs = await order.Authorizations().ConfigureAwait(false);
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
            await _dnsSolver.WaitPropagatedAsync(specs, cancellationToken).ConfigureAwait(false);

            foreach (var challenge in challenges)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _ = await challenge.Validate().ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var certChain = await order.Generate(
                    new CsrInfo { CommonName = domains[0] },
                    certKey)
                .ConfigureAwait(false);

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
            throw new AcmeOrderException("challenge_failed", ex.GetType().Name);
        }
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
            throw new AcmeAccountException("registration_failed", ex.GetType().Name);
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
            throw new AcmeOrderException("txt_cleanup_failed", ex.GetType().Name);
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
            // Ignore cleanup cancel during best-effort.
        }
        catch
        {
            // Best-effort.
        }
    }
}
