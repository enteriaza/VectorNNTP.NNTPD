using VectorNNTP.BackFiller.Hosting;
using VectorNNTP.NNTPD.Acme;
using VectorNNTP.NNTPD.Core;

namespace VectorNNTP.BackFiller.Tests.TestDoubles;

/// <summary>
/// Marks ACME ready without contacting Let's Encrypt so host composition tests can start.
/// </summary>
internal sealed class ImmediateAcmeReadyApplicationService : IApplicationService
{
    private readonly IAcmeCertificateReadiness _readiness;
    private readonly IBackFillerStartupJournal _journal;

    public ImmediateAcmeReadyApplicationService(
        IAcmeCertificateReadiness readiness,
        IBackFillerStartupJournal journal)
    {
        ArgumentNullException.ThrowIfNull(readiness);
        ArgumentNullException.ThrowIfNull(journal);
        _readiness = readiness;
        _journal = journal;
    }

    public string Name => "AcmeCertificate";

    public Task? Execution => null;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _readiness.MarkReady();
        _journal.Record(BackFillerStartupStages.AcmeCertificateReady);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
