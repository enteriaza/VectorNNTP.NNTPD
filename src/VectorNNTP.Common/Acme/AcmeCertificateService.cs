using VectorNNTP.NNTPD.Configuration;

namespace VectorNNTP.NNTPD.Acme;

/// <summary>
/// Application service that ensures ACME account/certificate readiness when TLS is enabled,
/// and periodically renews certificates while running.
/// </summary>
/// <remarks>
/// When <see cref="AcmeCloudflareOptions.IsTlsListenerEnabled"/> is <see langword="false"/>, start is a no-op
/// and no ACME network calls are made. Shutdown does not contact Let's Encrypt.
/// After a usable PFX is ensured or renewed, an immutable TLS certificate context is published
/// for the TLS listener (atomic swap; existing connections are unaffected).
/// </remarks>
public sealed class AcmeCertificateService : IAsyncDisposable
{
    /// <summary>Background renewal check interval (default 6 hours).</summary>
    public static readonly TimeSpan RenewalCheckInterval = TimeSpan.FromHours(6);

    private readonly IOptions<AcmeCloudflareOptions> _options;
    private readonly AcmeComponentFactory _factory;
    private readonly IAcmeCertificatePublisher _certificatePublisher;
    private readonly IAcmeCertificateReadiness _readiness;
    private readonly ILogger<AcmeCertificateService> _logger;
    private readonly CancellationTokenSource _runCts = new();
    private CertificateManager? _manager;
    private Task? _execution;
    private int _started;
    private int _disposed;

    /// <summary>Initializes a new instance of the <see cref="AcmeCertificateService"/> class.</summary>
    public AcmeCertificateService(
        IOptions<AcmeCloudflareOptions> options,
        AcmeComponentFactory factory,
        IAcmeCertificatePublisher certificatePublisher,
        IAcmeCertificateReadiness readiness,
        ILogger<AcmeCertificateService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(certificatePublisher);
        ArgumentNullException.ThrowIfNull(readiness);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options;
        _factory = factory;
        _certificatePublisher = certificatePublisher;
        _readiness = readiness;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "AcmeCertificate";

    /// <inheritdoc />
    public Task? Execution => _execution;

    /// <summary>Gets whether the service initialized ACME components (for tests).</summary>
    internal bool AcmeInitialized => _manager is not null;

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            return;
        }

        var options = _options.Value;
        if (!options.IsTlsListenerEnabled)
        {
            AcmeLogMessages.TlsDisabledIdle(_logger);
            return;
        }

        _manager = _factory.GetOrCreateManager()
            ?? throw new AcmeConfigurationException(
                "manager_missing",
                "TLS is enabled but CertificateManager could not be created");

        AcmeLogMessages.EnsuringCertificate(_logger, options.AcmeDirectoryUrl);

        try
        {
            var material = await _manager.EnsureCertificateAsync(cancellationToken).ConfigureAwait(false);
            _certificatePublisher.PublishFromPfx(material.PfxBytes, options.AcmeCertificatePassword);
            _readiness.MarkReady();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AcmeLogMessages.ProvisioningFailed(_logger, AcmeFailureSanitizer.Sanitize(ex));
            throw;
        }

        _execution = RunRenewalLoopAsync(_runCts.Token);
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _runCts.CancelAsync().ConfigureAwait(false);
        if (_execution is null)
        {
            return;
        }

        try
        {
            await _execution.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected on cancel.
        }
        catch (Exception ex)
        {
            AcmeLogMessages.RenewalLoopStopError(_logger, ex);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        try
        {
            await _runCts.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // Already disposed.
        }

        _runCts.Dispose();
    }

    private async Task RunRenewalLoopAsync(CancellationToken cancellationToken)
    {
        if (_manager is null)
        {
            return;
        }

        var password = _options.Value.AcmeCertificatePassword;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(RenewalCheckInterval, cancellationToken).ConfigureAwait(false);
                var renewed = await _manager.RenewIfDueAsync(cancellationToken).ConfigureAwait(false);
                if (renewed)
                {
                    var material = _manager.CurrentMaterial
                        ?? throw new AcmeCertificateException("no_certificate", "renewed without material");
                    _certificatePublisher.PublishFromPfx(material.PfxBytes, password);
                    AcmeLogMessages.RenewalCompleted(_logger);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                AcmeLogMessages.RenewalCheckFailed(_logger, AcmeFailureSanitizer.Sanitize(ex));
            }
        }
    }
}
