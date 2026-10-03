using VectorNNTP.Common.Tests.TestDoubles;
using VectorNNTP.Common.Acme;
using VectorNNTP.Common.Configuration;

namespace VectorNNTP.Common.Tests.Acme
{
    /// <summary>
    /// Proves the persistent ACME journal is one accumulating file per FQDN.
    /// </summary>
    public sealed class AcmeTransactionJournalTests
    {
        private const string Fqdn = "nntpd01.usenet.ninja";
        private const string OtherFqdn = "backfiller01.usenet.ninja";
        private const string DirectoryUrl = AcmeCloudflareOptions.DefaultAcmeDirectoryUrl;

        [Fact]
        public void One_journal_file_per_fqdn_accumulates_transactions()
        {
            using var dir = new TempStateDir();
            var journal = new AcmeTransactionJournal(dir.Path, Fqdn);
            var first = journal.BeginTransaction([Fqdn], DirectoryUrl, DateTimeOffset.UtcNow);
            journal.CompleteFailure(first, "first_failure", "first", DateTimeOffset.UtcNow);
            var second = journal.BeginTransaction([Fqdn, CertificateIdentities.NewsHostname], DirectoryUrl, DateTimeOffset.UtcNow);
            journal.CompleteFailure(second, "second_failure", "second", DateTimeOffset.UtcNow);

            Assert.Equal(Path.Combine(dir.Path, "journal", Fqdn + ".json"), journal.FilePath);
            Assert.True(File.Exists(journal.FilePath));
            Assert.Single(Directory.GetFiles(AcmePaths.JournalRoot(dir.Path), "*.json"));

            var reloaded = new AcmeTransactionJournal(dir.Path, Fqdn).LoadHistory();
            Assert.Equal(2, reloaded.Count);
            Assert.Equal(first, reloaded[0].Id);
            Assert.Equal(second, reloaded[1].Id);
            Assert.Equal(AcmeTransactionJournal.StatusFailed, reloaded[0].Status);
            Assert.Equal("first_failure", reloaded[0].FailureCategory);
            Assert.Equal("second_failure", reloaded[1].FailureCategory);
            Assert.Contains(reloaded[0].Events, static e => e.Type == AcmeTransactionJournal.EventCertificateRequestStarted);
            Assert.Contains(reloaded[0].Events, static e => e.Type == AcmeTransactionJournal.EventCertificateIssuanceFailed);
            Assert.False(Directory.Exists(Path.Combine(dir.Path, "journal", "acme")));
            Assert.DoesNotContain(
                Directory.GetFiles(AcmePaths.JournalRoot(dir.Path), "*.json"),
                static path => !string.Equals(Path.GetFileName(path), Fqdn + ".json", StringComparison.Ordinal));
        }

        [Fact]
        public void Distinct_fqdns_use_distinct_journal_files()
        {
            using var dir = new TempStateDir();
            var nntpd = new AcmeTransactionJournal(dir.Path, Fqdn);
            var backfiller = new AcmeTransactionJournal(dir.Path, OtherFqdn);
            nntpd.BeginTransaction([Fqdn], DirectoryUrl, DateTimeOffset.UtcNow);
            backfiller.BeginTransaction([OtherFqdn], DirectoryUrl, DateTimeOffset.UtcNow);

            Assert.NotEqual(nntpd.FilePath, backfiller.FilePath);
            Assert.Single(nntpd.LoadHistory());
            Assert.Single(backfiller.LoadHistory());
            Assert.Equal(Fqdn, nntpd.LoadHistory()[0].Identifiers.Single());
            Assert.Equal(OtherFqdn, backfiller.LoadHistory()[0].Identifiers.Single());
        }

        [Fact]
        public void Successful_certificate_fields_are_retained()
        {
            using var dir = new TempStateDir();
            var names = CertificateIdentities.ForFqdn(Fqdn);
            var material = TestCertificateFactory.CreateMaterial(names, DateTimeOffset.UtcNow.AddDays(45));
            var journal = new AcmeTransactionJournal(dir.Path, Fqdn);
            var id = journal.BeginTransaction(names, DirectoryUrl, DateTimeOffset.UtcNow);
            journal.RecordCertificateIssued(material);
            journal.RecordCertificatePersisted("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", material);
            journal.RecordCertificatePromoted("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            journal.CompleteSuccess(
                id,
                material,
                "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                TestCertificateFactory.Password,
                DateTimeOffset.UtcNow);

            var transaction = Assert.Single(new AcmeTransactionJournal(dir.Path, Fqdn).LoadHistory());
            Assert.Equal(AcmeTransactionJournal.StatusSucceeded, transaction.Status);
            Assert.False(string.IsNullOrWhiteSpace(transaction.SerialNumber));
            Assert.False(string.IsNullOrWhiteSpace(transaction.Thumbprint));
            Assert.NotNull(transaction.NotAfter);
            Assert.True(transaction.NotAfter > DateTimeOffset.UtcNow);
            Assert.Equal("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", transaction.GenerationId);
            Assert.Equal(
                new[]
                {
                    AcmeTransactionJournal.EventCertificateRequestStarted,
                    AcmeTransactionJournal.EventCertificateIssued,
                    AcmeTransactionJournal.EventCertificatePersisted,
                    AcmeTransactionJournal.EventCertificatePromoted,
                },
                transaction.Events.Select(static e => e.Type).ToArray());
        }

        [Fact]
        public async Task Same_fqdn_journal_writes_wait_for_an_independent_lock_holder()
        {
            using var dir = new TempStateDir();
            var journal = new AcmeTransactionJournal(dir.Path, Fqdn);
            using var held = AcmeExclusiveFileLock.Acquire(journal.LockPath);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => Task.Run(
                    () => journal.BeginTransaction([Fqdn], DirectoryUrl, DateTimeOffset.UtcNow, cts.Token),
                    cts.Token));
            Assert.False(File.Exists(journal.FilePath));

            held.Dispose();
            var id = journal.BeginTransaction([Fqdn], DirectoryUrl, DateTimeOffset.UtcNow);
            Assert.Equal(id, Assert.Single(journal.LoadHistory()).Id);
        }

        [Fact]
        public void Different_fqdn_journal_locks_are_independent()
        {
            using var dir = new TempStateDir();
            var nntpd = new AcmeTransactionJournal(dir.Path, Fqdn);
            var backfiller = new AcmeTransactionJournal(dir.Path, OtherFqdn);
            using var nntpdLock = AcmeExclusiveFileLock.Acquire(nntpd.LockPath);
            var backfillerId = backfiller.BeginTransaction([OtherFqdn], DirectoryUrl, DateTimeOffset.UtcNow);

            Assert.Equal(backfillerId, Assert.Single(backfiller.LoadHistory()).Id);
            Assert.False(File.Exists(nntpd.FilePath));
            Assert.NotEqual(nntpd.LockPath, backfiller.LockPath);
        }

        [Fact]
        public void Three_transactions_remain_cumulative_in_one_file()
        {
            using var dir = new TempStateDir();
            var journal = new AcmeTransactionJournal(dir.Path, Fqdn);
            var first = journal.BeginTransaction([Fqdn], DirectoryUrl, DateTimeOffset.UtcNow);
            journal.CompleteFailure(first, "one", "one", DateTimeOffset.UtcNow);
            var second = journal.BeginTransaction([Fqdn], DirectoryUrl, DateTimeOffset.UtcNow);
            journal.CompleteFailure(second, "two", "two", DateTimeOffset.UtcNow);
            var third = journal.BeginTransaction([Fqdn], DirectoryUrl, DateTimeOffset.UtcNow);
            journal.CompleteFailure(third, "three", "three", DateTimeOffset.UtcNow);

            var history = new AcmeTransactionJournal(dir.Path, Fqdn).LoadHistory();
            Assert.Equal([first, second, third], history.Select(static item => item.Id).ToArray());
            Assert.Single(Directory.GetFiles(AcmePaths.JournalRoot(dir.Path), "*.json"));
        }
    }
}
