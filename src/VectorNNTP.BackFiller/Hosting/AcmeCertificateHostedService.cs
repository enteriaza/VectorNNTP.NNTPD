using VectorNNTP.NNTPD.Acme;

namespace VectorNNTP.BackFiller.Hosting;

/// <summary>
/// Generic Host wrapper around the shared ACME certificate implementation.
/// </summary>
/// <remarks>
/// <see cref="StartAsync"/> does not return until a usable certificate is loaded
/// or issued. Failure fails host startup. The TLS listener must not start first.
/// </remarks>
public sealed class AcmeCertificateHostedService : IHostedService, IAsyncDisposable
{
    private readonly AcmeCertificateService _inner;
    private readonly IAcmeCertificateReadiness _readiness;
    private readonly IBackFillerStartupJournal _journal;

    /// <summary>Initializes a new wrapper.</summary>
    public AcmeCertificateHostedService(
        AcmeCertificateService inner,
        IAcmeCertificateReadiness readiness,
        IBackFillerStartupJournal journal)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(readiness);
        ArgumentNullException.ThrowIfNull(journal);
        _inner = inner;
        _readiness = readiness;
        _journal = journal;
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _inner.StartAsync(cancellationToken).ConfigureAwait(false);
        if (!_readiness.IsReady)
        {
            throw new InvalidOperationException(
                "ACME certificate acquisition completed without publishing a usable certificate. BackFiller cannot start.");
        }

        _journal.Record(BackFillerStartupStages.AcmeCertificateReady);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => _inner.StopAsync(cancellationToken);

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _inner.DisposeAsync();
}
