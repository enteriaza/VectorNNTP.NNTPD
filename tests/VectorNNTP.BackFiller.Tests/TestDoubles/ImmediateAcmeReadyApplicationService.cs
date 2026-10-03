using VectorNNTP.BackFiller.Hosting;
using VectorNNTP.NNTPD.Acme;
using VectorNNTP.NNTPD.Core;

namespace VectorNNTP.BackFiller.Tests.TestDoubles
{
    /// <summary>
    /// Marks ACME ready without contacting Let's Encrypt so host composition tests can start.
    /// Publishes a test certificate through the shared Common TLS publisher.
    /// </summary>
    internal sealed class ImmediateAcmeReadyApplicationService : IApplicationService
    {
        private readonly IAcmeCertificateReadiness _readiness;
        private readonly IAcmeCertificatePublisher _publisher;
        private readonly IBackFillerStartupJournal _journal;

        public ImmediateAcmeReadyApplicationService(
            IAcmeCertificateReadiness readiness,
            IAcmeCertificatePublisher publisher,
            IBackFillerStartupJournal journal)
        {
            ArgumentNullException.ThrowIfNull(readiness);
            ArgumentNullException.ThrowIfNull(publisher);
            ArgumentNullException.ThrowIfNull(journal);
            _readiness = readiness;
            _publisher = publisher;
            _journal = journal;
        }

        public string Name => "AcmeCertificate";

        public Task? Execution => null;

        public Task StartAsync(CancellationToken cancellationToken)
        {
            using var certificate = TestListenerCertificates.CreateSelfSigned();
            _publisher.PublishFromPfx(certificate.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pfx, "test"), "test");
            _readiness.MarkReady();
            _journal.Record(BackFillerStartupStages.AcmeCertificateReady);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
