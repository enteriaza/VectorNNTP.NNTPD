using VectorNNTP.BackFiller.Hosting;
using VectorNNTP.NNTPD.Acme;
using VectorNNTP.NNTPD.Core;

namespace VectorNNTP.BackFiller.Acme;

/// <summary>
/// BackFiller lifecycle wrapper around the shared ACME certificate implementation.
/// </summary>
/// <remarks>
/// Delegates issuance, renewal, and persistence to Common
/// <see cref="AcmeCertificateService"/>. BackFiller-specific fail-fast readiness
/// and startup-journal recording stay on this adapter.
/// </remarks>
internal sealed class AcmeCertificateApplicationService : IApplicationService, IAsyncDisposable
{
    private readonly AcmeCertificateService _inner;
    private readonly IAcmeCertificateReadiness _readiness;
    private readonly IBackFillerStartupJournal _journal;

    /// <summary>Initializes a new wrapper.</summary>
    public AcmeCertificateApplicationService(
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
    public string Name => _inner.Name;

    /// <inheritdoc />
    public Task? Execution => _inner.Execution;

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
