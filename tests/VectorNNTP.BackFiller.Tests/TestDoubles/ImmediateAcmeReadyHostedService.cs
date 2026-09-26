using Microsoft.Extensions.Hosting;
using VectorNNTP.BackFiller.Hosting;
using VectorNNTP.NNTPD.Acme;

namespace VectorNNTP.BackFiller.Tests.TestDoubles;

/// <summary>
/// Marks ACME ready without contacting Let's Encrypt so host composition tests can start.
/// </summary>
internal sealed class ImmediateAcmeReadyHostedService : IHostedService
{
    private readonly IAcmeCertificateReadiness _readiness;
    private readonly IBackFillerStartupJournal _journal;

    public ImmediateAcmeReadyHostedService(
        IAcmeCertificateReadiness readiness,
        IBackFillerStartupJournal journal)
    {
        ArgumentNullException.ThrowIfNull(readiness);
        ArgumentNullException.ThrowIfNull(journal);
        _readiness = readiness;
        _journal = journal;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _readiness.MarkReady();
        _journal.Record(BackFillerStartupStages.AcmeCertificateReady);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
