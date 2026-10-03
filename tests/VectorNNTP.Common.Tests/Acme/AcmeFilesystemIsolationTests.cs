using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTP.Common.Tests.TestDoubles;
using VectorNNTP.Common.Acme;
using VectorNNTP.Common.Cloudflare;
using VectorNNTP.Common.Configuration;

namespace VectorNNTP.Common.Tests.Acme
{
    /// <summary>
    /// Proves NNTPD and BackFiller certificate identities can share one physical
    /// <c>certs</c> root through Common ACME paths without colliding.
    /// </summary>
    public sealed class AcmeFilesystemIsolationTests
    {
        private const string NntpdFqdn = "nntpd01.usenet.ninja";
        private const string BackFillerFqdn = "backfiller01.usenet.ninja";
        private const string DirectoryUrl = AcmeCloudflareOptions.DefaultAcmeDirectoryUrl;

        [Fact]
        public void Shared_account_state_is_common_across_identities()
        {
            using var dir = new TempStateDir();
            var first = new AccountStore(dir.Path);
            using var rsa = System.Security.Cryptography.RSA.Create(2048);
            var key = rsa.ExportPkcs8PrivateKey();
            first.Save(new AcmeAccountState(
                "https://acme.example/acme/acct/1",
                DirectoryUrl,
                key));

            var second = new AccountStore(dir.Path);
            var loaded = second.Load();
            Assert.NotNull(loaded);
            Assert.Equal("https://acme.example/acme/acct/1", loaded.AccountUri);
            Assert.True(key.AsSpan().SequenceEqual(loaded.PrivateKeyDer));
            Assert.Equal(AcmePaths.AccountKeyPath(dir.Path), Path.Combine(dir.Path, "account", "private_key.der"));
            Assert.False(File.Exists(Path.Combine(dir.Path, "account", "nntpd01_private_key.der")));
            Assert.False(File.Exists(Path.Combine(dir.Path, "account", "backfiller01_private_key.der")));
        }

        [Fact]
        public void Common_paths_are_fqdn_scoped_under_one_certs_root()
        {
            using var dir = new TempStateDir();
            Assert.Equal(
                Path.Combine(dir.Path, "live", NntpdFqdn, "dns01"),
                AcmePaths.Dns01RecoveryDir(dir.Path, NntpdFqdn));
            Assert.Equal(
                Path.Combine(dir.Path, "journal", BackFillerFqdn + ".json"),
                AcmePaths.TransactionJournalPath(dir.Path, BackFillerFqdn));
            Assert.Equal(
                Path.Combine(dir.Path, "live", NntpdFqdn, "current"),
                AcmePaths.CurrentGenerationPointerPath(dir.Path, NntpdFqdn));
            Assert.Equal(
                Path.Combine(dir.Path, "live", NntpdFqdn, "gens"),
                AcmePaths.GenerationsDir(dir.Path, NntpdFqdn));
            Assert.Equal(
                Path.Combine(dir.Path, "live", BackFillerFqdn, ".issuance.lock"),
                AcmePaths.IssuanceLockPath(dir.Path, BackFillerFqdn));
            Assert.NotEqual(
                AcmePaths.CurrentGenerationPointerPath(dir.Path, NntpdFqdn),
                AcmePaths.CurrentGenerationPointerPath(dir.Path, BackFillerFqdn));
            Assert.DoesNotContain(
                Path.Combine("dns01", "journal"),
                AcmePaths.Dns01RecoveryDir(dir.Path, NntpdFqdn),
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                Path.Combine("journal", "dns01"),
                AcmePaths.Dns01RecoveryDir(dir.Path, NntpdFqdn),
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                Path.Combine("journal", "acme"),
                AcmePaths.TransactionJournalPath(dir.Path, NntpdFqdn),
                StringComparison.Ordinal);
        }

        [Fact]
        public async Task Dns01_recovery_for_one_fqdn_does_not_touch_another()
        {
            using var dir = new TempStateDir();
            var client = new InMemoryCloudflareDnsClient();
            var nntpd = CreateSolver(client, NntpdFqdn, dir.Path);
            var backfiller = CreateSolver(client, BackFillerFqdn, dir.Path);
            var nntpdSpec = new Dns01ChallengeSpec(NntpdFqdn, "nntpd-token");
            var backfillerSpec = new Dns01ChallengeSpec(BackFillerFqdn, "backfiller-token");
            var resolver = new ImmediateTxtResolver();
            resolver.Set(nntpdSpec.RecordName, nntpdSpec.Validation);
            resolver.Set(backfillerSpec.RecordName, backfillerSpec.Validation);

            await nntpd.PlaceAsync([nntpdSpec], CancellationToken.None);
            await backfiller.PlaceAsync([backfillerSpec], CancellationToken.None);

            var nntpdJournal = Directory.GetFiles(AcmePaths.Dns01RecoveryDir(dir.Path, NntpdFqdn), "*.json");
            var backfillerJournal = Directory.GetFiles(AcmePaths.Dns01RecoveryDir(dir.Path, BackFillerFqdn), "*.json");
            Assert.Single(nntpdJournal);
            Assert.Single(backfillerJournal);
            Assert.Equal(2, client.Snapshot().Count(static r => r.Type == CloudflareDnsRecordTypes.TXT));

            var recovered = CreateSolver(client, NntpdFqdn, dir.Path);
            await recovered.RecoverAsync(CancellationToken.None);

            Assert.Empty(Directory.GetFiles(AcmePaths.Dns01RecoveryDir(dir.Path, NntpdFqdn), "*.json"));
            Assert.Single(Directory.GetFiles(AcmePaths.Dns01RecoveryDir(dir.Path, BackFillerFqdn), "*.json"));
            Assert.DoesNotContain(client.Snapshot(), static r => r.Name == "_acme-challenge." + NntpdFqdn);
            Assert.Contains(client.Snapshot(), static r => r.Name == "_acme-challenge." + BackFillerFqdn);
        }

        [Fact]
        public async Task Simultaneous_dns01_requests_for_different_fqdns_are_independent()
        {
            using var dir = new TempStateDir();
            var client = new InMemoryCloudflareDnsClient();
            var nntpd = CreateSolver(client, NntpdFqdn, dir.Path);
            var backfiller = CreateSolver(client, BackFillerFqdn, dir.Path);
            var nntpdSpec = new Dns01ChallengeSpec(NntpdFqdn, "nntpd-token");
            var backfillerSpec = new Dns01ChallengeSpec(BackFillerFqdn, "backfiller-token");

            await Task.WhenAll(
                nntpd.PlaceAsync([nntpdSpec], CancellationToken.None),
                backfiller.PlaceAsync([backfillerSpec], CancellationToken.None));

            Assert.Equal(2, client.Snapshot().Count(static r => r.Type == CloudflareDnsRecordTypes.TXT));
            Assert.True(File.Exists(
                Directory.GetFiles(AcmePaths.Dns01RecoveryDir(dir.Path, NntpdFqdn), "*.json").Single()));
            Assert.True(File.Exists(
                Directory.GetFiles(AcmePaths.Dns01RecoveryDir(dir.Path, BackFillerFqdn), "*.json").Single()));
            using var nntpdDoc = JsonDocument.Parse(
                File.ReadAllText(Directory.GetFiles(AcmePaths.Dns01RecoveryDir(dir.Path, NntpdFqdn), "*.json").Single()));
            Assert.Equal(NntpdFqdn, nntpdDoc.RootElement.GetProperty("fqdn").GetString());
        }

        [Fact]
        public async Task Dns01_crash_recovery_stays_inside_the_requested_fqdn()
        {
            using var dir = new TempStateDir();
            var client = new InMemoryCloudflareDnsClient();
            client.Seed(
                Txt("nntpd-rec", "_acme-challenge." + NntpdFqdn, "nntpd-token"),
                Txt("bf-rec", "_acme-challenge." + BackFillerFqdn, "backfiller-token"));
            WriteDns01Journal(
                dir.Path,
                NntpdFqdn,
                "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                "placed",
                "nntpd-rec",
                "_acme-challenge." + NntpdFqdn,
                "nntpd-token");
            WriteDns01Journal(
                dir.Path,
                BackFillerFqdn,
                "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                "placed",
                "bf-rec",
                "_acme-challenge." + BackFillerFqdn,
                "backfiller-token");

            var solver = CreateSolver(client, NntpdFqdn, dir.Path);
            await solver.RecoverAsync(CancellationToken.None);

            Assert.False(File.Exists(Path.Combine(
                AcmePaths.Dns01RecoveryDir(dir.Path, NntpdFqdn),
                "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.json")));
            Assert.True(File.Exists(Path.Combine(
                AcmePaths.Dns01RecoveryDir(dir.Path, BackFillerFqdn),
                "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb.json")));
            Assert.DoesNotContain(client.Snapshot(), static r => r.Id == "nntpd-rec");
            Assert.Contains(client.Snapshot(), static r => r.Id == "bf-rec");
        }

        [Fact]
        public async Task Issuance_ledger_records_request_success_expiry_generation_and_failure()
        {
            using var dir = new TempStateDir();
            var nntpdNames = CertificateIdentities.ForFqdn(NntpdFqdn);
            var store = new CertificateStore(dir.Path, NntpdFqdn, TestCertificateFactory.Password, nntpdNames, TimeSpan.FromDays(30));
            var issuer = new ScriptedIssuer();
            var manager = new CertificateManager(
                NntpdFqdn,
                dir.Path,
                DirectoryUrl,
                store,
                issuer,
                TestCertificateFactory.Password,
                TimeSpan.FromDays(30),
                NullLogger<CertificateManager>.Instance);

            await manager.EnsureCertificateAsync(CancellationToken.None);
            store.Save(TestCertificateFactory.CreateMaterial(nntpdNames, DateTimeOffset.UtcNow.AddDays(5)));
            issuer.ThrowOnIssue = new AcmeOrderException("finalize_failed", "ca rejected");
            Assert.False(await manager.RenewIfDueAsync(CancellationToken.None));
            issuer.ThrowOnIssue = null;
            Assert.True(await manager.RenewIfDueAsync(CancellationToken.None));

            var journal = new AcmeTransactionJournal(dir.Path, NntpdFqdn);
            var history = journal.LoadHistory();
            Assert.Equal(3, history.Count);
            Assert.Contains(history, static r => r.Status == AcmeTransactionJournal.StatusSucceeded && r.GenerationId is not null);
            Assert.Contains(history, static r => r.Status == AcmeTransactionJournal.StatusFailed && r.FailureCategory == "finalize_failed");
            Assert.All(history, static r => Assert.Equal(DirectoryUrl, r.DirectoryUrl));
            Assert.All(
                history,
                static r => Assert.Contains(
                    AcmeTransactionJournal.EventCertificateRequestStarted,
                    r.Events.Select(static e => e.Type)));

            var success = history.Last(static r => r.Status == AcmeTransactionJournal.StatusSucceeded);
            Assert.False(string.IsNullOrWhiteSpace(success.SerialNumber));
            Assert.False(string.IsNullOrWhiteSpace(success.Thumbprint));
            Assert.NotNull(success.NotAfter);
            Assert.NotNull(success.GenerationId);
            Assert.Equal(store.CurrentGenerationId(), success.GenerationId);
            Assert.Contains(success.Events, static e => e.Type == AcmeTransactionJournal.EventCertificateIssued);
            Assert.Contains(success.Events, static e => e.Type == AcmeTransactionJournal.EventCertificatePersisted);
            Assert.Contains(success.Events, static e => e.Type == AcmeTransactionJournal.EventCertificatePromoted);
            var failed = history.Single(static r => r.Status == AcmeTransactionJournal.StatusFailed);
            Assert.Contains(failed.Events, static e => e.Type == AcmeTransactionJournal.EventCertificateIssuanceFailed);
            Assert.Equal(
                Path.Combine(dir.Path, "journal", NntpdFqdn + ".json"),
                journal.FilePath);
            Assert.True(File.Exists(journal.FilePath));
            Assert.False(Directory.Exists(Path.Combine(dir.Path, "journal", "acme")));
            Assert.Empty(new AcmeTransactionJournal(dir.Path, BackFillerFqdn).LoadHistory());
            Assert.False(File.Exists(AcmePaths.TransactionJournalPath(dir.Path, BackFillerFqdn)));
        }

        [Fact]
        public void Live_generation_gc_and_current_pointer_are_fqdn_scoped()
        {
            using var dir = new TempStateDir();
            var nntpdNames = CertificateIdentities.ForFqdn(NntpdFqdn);
            var backfillerNames = CertificateIdentities.ForFqdn(BackFillerFqdn, includeNewsHostname: false);
            var nntpd = new CertificateStore(dir.Path, NntpdFqdn, TestCertificateFactory.Password, nntpdNames, TimeSpan.FromDays(30));
            var backfiller = new CertificateStore(dir.Path, BackFillerFqdn, TestCertificateFactory.Password, backfillerNames, TimeSpan.FromDays(30));

            nntpd.Save(TestCertificateFactory.CreateMaterial(nntpdNames, DateTimeOffset.UtcNow.AddDays(60)));
            backfiller.Save(TestCertificateFactory.CreateMaterial(backfillerNames, DateTimeOffset.UtcNow.AddDays(60)));
            var nntpdCurrent = File.ReadAllText(AcmePaths.CurrentGenerationPointerPath(dir.Path, NntpdFqdn)).Trim();
            var backfillerCurrent = File.ReadAllText(AcmePaths.CurrentGenerationPointerPath(dir.Path, BackFillerFqdn)).Trim();
            var backfillerGenDir = AcmePaths.GenerationDir(dir.Path, BackFillerFqdn, backfillerCurrent);

            nntpd.Save(TestCertificateFactory.CreateMaterial(nntpdNames, DateTimeOffset.UtcNow.AddDays(90)));
            nntpd.Recover();

            Assert.True(Directory.Exists(backfillerGenDir));
            Assert.Equal(backfillerCurrent, File.ReadAllText(AcmePaths.CurrentGenerationPointerPath(dir.Path, BackFillerFqdn)).Trim());
            Assert.NotEqual(nntpdCurrent, File.ReadAllText(AcmePaths.CurrentGenerationPointerPath(dir.Path, NntpdFqdn)).Trim());
            Assert.Contains("news.usenet.ninja", nntpd.Load()!.Domains);
            Assert.DoesNotContain("news.usenet.ninja", backfiller.Load()!.Domains);
            Assert.Single(Directory.GetDirectories(AcmePaths.GenerationsDir(dir.Path, BackFillerFqdn)));
        }

        [Fact]
        public async Task Issuance_lock_is_exclusive_per_fqdn_and_independent_across_fqdns()
        {
            using var dir = new TempStateDir();
            await using var nntpdLock = await AcmeIssuanceLock.AcquireAsync(dir.Path, NntpdFqdn, CancellationToken.None);
            await using var backfillerLock = await AcmeIssuanceLock.AcquireAsync(dir.Path, BackFillerFqdn, CancellationToken.None);
            Assert.Equal(AcmePaths.IssuanceLockPath(dir.Path, NntpdFqdn), Path.Combine(dir.Path, "live", NntpdFqdn, ".issuance.lock"));
            Assert.NotEqual(
                AcmePaths.IssuanceLockPath(dir.Path, NntpdFqdn),
                AcmePaths.IssuanceLockPath(dir.Path, BackFillerFqdn));

            using var held = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => AcmeIssuanceLock.AcquireAsync(dir.Path, NntpdFqdn, held.Token));
        }

        [Fact]
        public async Task Same_physical_certs_root_keeps_nntpd_and_backfiller_state_apart()
        {
            using var dir = new TempStateDir();
            var nntpdNames = CertificateIdentities.ForFqdn(NntpdFqdn);
            var backfillerNames = CertificateIdentities.ForFqdn(BackFillerFqdn, includeNewsHostname: false);
            var nntpdStore = new CertificateStore(dir.Path, NntpdFqdn, TestCertificateFactory.Password, nntpdNames, TimeSpan.FromDays(30));
            var backfillerStore = new CertificateStore(dir.Path, BackFillerFqdn, TestCertificateFactory.Password, backfillerNames, TimeSpan.FromDays(30));
            var nntpdManager = new CertificateManager(
                NntpdFqdn,
                dir.Path,
                DirectoryUrl,
                nntpdStore,
                new ScriptedIssuer(),
                TestCertificateFactory.Password,
                TimeSpan.FromDays(30),
                NullLogger<CertificateManager>.Instance,
                includeNewsHostname: true);
            var backfillerManager = new CertificateManager(
                BackFillerFqdn,
                dir.Path,
                DirectoryUrl,
                backfillerStore,
                new ScriptedIssuer(),
                TestCertificateFactory.Password,
                TimeSpan.FromDays(30),
                NullLogger<CertificateManager>.Instance,
                includeNewsHostname: false);

            await Task.WhenAll(
                nntpdManager.EnsureCertificateAsync(CancellationToken.None),
                backfillerManager.EnsureCertificateAsync(CancellationToken.None));

            Assert.Equal(
                Path.Combine(dir.Path, "live", NntpdFqdn, "current"),
                AcmePaths.CurrentGenerationPointerPath(dir.Path, NntpdFqdn));
            Assert.Equal(
                Path.Combine(dir.Path, "live", BackFillerFqdn, "current"),
                AcmePaths.CurrentGenerationPointerPath(dir.Path, BackFillerFqdn));
            Assert.NotEqual(
                File.ReadAllText(AcmePaths.CurrentGenerationPointerPath(dir.Path, NntpdFqdn)),
                File.ReadAllText(AcmePaths.CurrentGenerationPointerPath(dir.Path, BackFillerFqdn)));
            Assert.True(Directory.Exists(AcmePaths.GenerationsDir(dir.Path, NntpdFqdn)));
            Assert.True(Directory.Exists(AcmePaths.GenerationsDir(dir.Path, BackFillerFqdn)));
            Assert.NotEmpty(new AcmeTransactionJournal(dir.Path, NntpdFqdn).LoadHistory());
            Assert.NotEmpty(new AcmeTransactionJournal(dir.Path, BackFillerFqdn).LoadHistory());
            Assert.True(File.Exists(AcmePaths.TransactionJournalPath(dir.Path, NntpdFqdn)));
            Assert.True(File.Exists(AcmePaths.TransactionJournalPath(dir.Path, BackFillerFqdn)));
            Assert.False(Directory.Exists(Path.Combine(dir.Path, "live", "gens")));
            Assert.False(File.Exists(Path.Combine(dir.Path, "live", "current")));
            Assert.False(Directory.Exists(Path.Combine(dir.Path, "journal", "acme")));
            Assert.False(Directory.Exists(Path.Combine(dir.Path, "journal", "dns01")));
            Assert.False(Directory.Exists(Path.Combine(dir.Path, "dns01")));
            using var nntpdCert = nntpdManager.CreateTlsCertificate();
            using var backfillerCert = backfillerManager.CreateTlsCertificate();
            var nntpdSans = nntpdCert.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single()
                .EnumerateDnsNames().Select(static n => n.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
            var backfillerSans = backfillerCert.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single()
                .EnumerateDnsNames().Select(static n => n.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
            Assert.Contains(NntpdFqdn, nntpdSans);
            Assert.Contains(CertificateIdentities.NewsHostname, nntpdSans);
            Assert.Equal([BackFillerFqdn], backfillerSans);
            Assert.Equal(AcmePaths.AccountKeyPath(dir.Path), Path.Combine(dir.Path, "account", "private_key.der"));
        }

        [Fact]
        public void Production_layout_never_creates_global_live_gens_or_current()
        {
            using var dir = new TempStateDir();
            AcmePaths.EnsureStateLayout(dir.Path);
            Assert.False(Directory.Exists(Path.Combine(dir.Path, "live")));
            Assert.False(Directory.Exists(Path.Combine(dir.Path, "journal", "acme")));
            Assert.False(Directory.Exists(Path.Combine(dir.Path, "journal", "dns01")));
            Assert.False(Directory.Exists(Path.Combine(dir.Path, "dns01")));

            AcmePaths.EnsureCertificateIdentityLayout(dir.Path, NntpdFqdn);
            var names = CertificateIdentities.ForFqdn(NntpdFqdn);
            var store = new CertificateStore(dir.Path, NntpdFqdn, TestCertificateFactory.Password, names, TimeSpan.FromDays(30));
            store.Save(TestCertificateFactory.CreateMaterial(names, DateTimeOffset.UtcNow.AddDays(60)));

            Assert.True(Directory.Exists(AcmePaths.GenerationsDir(dir.Path, NntpdFqdn)));
            Assert.True(File.Exists(AcmePaths.CurrentGenerationPointerPath(dir.Path, NntpdFqdn)));
            Assert.True(Directory.Exists(AcmePaths.Dns01RecoveryDir(dir.Path, NntpdFqdn)));
            Assert.False(Directory.Exists(Path.Combine(dir.Path, "live", "gens")));
            Assert.False(File.Exists(Path.Combine(dir.Path, "live", "current")));
            Assert.False(File.Exists(Path.Combine(dir.Path, ".issuance.lock")));
            Assert.False(Directory.Exists(Path.Combine(dir.Path, "journal", "acme")));
            Assert.False(Directory.Exists(Path.Combine(dir.Path, "journal", "dns01")));
            Assert.False(Directory.Exists(Path.Combine(dir.Path, "dns01")));
        }

        [Fact]
        public async Task Dns01_history_is_permanent_and_recovery_state_is_transient()
        {
            using var dir = new TempStateDir();
            var client = new InMemoryCloudflareDnsClient();
            var journal = new AcmeTransactionJournal(dir.Path, NntpdFqdn);
            journal.BeginTransaction([NntpdFqdn], DirectoryUrl, DateTimeOffset.UtcNow);
            var spec = new Dns01ChallengeSpec(NntpdFqdn, "nntpd-token");
            var resolver = new ImmediateTxtResolver();
            resolver.Set(spec.RecordName, spec.Validation);
            var recorded = new Dns01Solver(
                client,
                "zone-test",
                resolver,
                NntpdFqdn,
                dir.Path,
                TimeSpan.FromSeconds(2),
                TimeSpan.FromMilliseconds(10),
                journal);

            await recorded.PlaceAsync([spec], CancellationToken.None);
            Assert.Single(Directory.GetFiles(AcmePaths.Dns01RecoveryDir(dir.Path, NntpdFqdn), "*.json"));
            await recorded.WaitPropagatedAsync([spec], CancellationToken.None);
            await recorded.CleanupAsync(CancellationToken.None);

            Assert.Empty(Directory.GetFiles(AcmePaths.Dns01RecoveryDir(dir.Path, NntpdFqdn), "*.json"));
            Assert.True(File.Exists(journal.FilePath));
            Assert.False(Directory.Exists(Path.Combine(dir.Path, "journal", "dns01")));
            var transaction = Assert.Single(journal.LoadHistory());
            Assert.Contains(
                transaction.Events,
                static e => e.Type == AcmeTransactionJournal.EventDnsChallengeCreated
                            && e.RecordName == "_acme-challenge." + NntpdFqdn
                            && e.ZoneId == "zone-test"
                            && e.RecordId is not null
                            && e.TxtContent == "nntpd-token");
            Assert.Contains(
                transaction.Events,
                static e => e.Type == AcmeTransactionJournal.EventDnsChallengePropagated
                            && e.RecordName == "_acme-challenge." + NntpdFqdn);
            Assert.Contains(
                transaction.Events,
                static e => e.Type == AcmeTransactionJournal.EventDnsChallengeRemoved
                            && e.RecordName == "_acme-challenge." + NntpdFqdn
                            && e.TxtContent == "nntpd-token");
            Assert.Empty(new AcmeTransactionJournal(dir.Path, BackFillerFqdn).LoadHistory());
        }

        [Fact]
        public async Task Orphan_dns_recovery_attaches_to_the_original_transaction_when_id_is_persisted()
        {
            using var dir = new TempStateDir();
            var journal = new AcmeTransactionJournal(dir.Path, NntpdFqdn);
            var originalId = journal.BeginTransaction([NntpdFqdn], DirectoryUrl, DateTimeOffset.UtcNow);
            var client = new InMemoryCloudflareDnsClient();
            var solver = new Dns01Solver(
                client,
                "zone-test",
                new ImmediateTxtResolver(),
                NntpdFqdn,
                dir.Path,
                TimeSpan.FromSeconds(2),
                TimeSpan.FromMilliseconds(10),
                journal);
            var spec = new Dns01ChallengeSpec(NntpdFqdn, "nntpd-token");
            await solver.PlaceAsync([spec], CancellationToken.None);
            Assert.Contains(client.Snapshot(), static r => r.Type == CloudflareDnsRecordTypes.TXT);

            var recoveredJournal = new AcmeTransactionJournal(dir.Path, NntpdFqdn);
            var unrelated = recoveredJournal.BeginTransaction([NntpdFqdn], DirectoryUrl, DateTimeOffset.UtcNow);
            var recovered = new Dns01Solver(
                client,
                "zone-test",
                new ImmediateTxtResolver(),
                NntpdFqdn,
                dir.Path,
                historyJournal: recoveredJournal);
            await recovered.RecoverAsync(CancellationToken.None);

            Assert.Empty(Directory.GetFiles(AcmePaths.Dns01RecoveryDir(dir.Path, NntpdFqdn), "*.json"));
            Assert.DoesNotContain(client.Snapshot(), static r => r.Type == CloudflareDnsRecordTypes.TXT);
            var history = recoveredJournal.LoadHistory();
            Assert.Equal(2, history.Count);
            var original = history.Single(item => item.Id == originalId);
            var later = history.Single(item => item.Id == unrelated);
            Assert.Contains(
                original.Events,
                static e => e.Type == AcmeTransactionJournal.EventDnsChallengeCreated);
            Assert.Contains(
                original.Events,
                e => e.Type == AcmeTransactionJournal.EventDnsChallengeRecoveredAndRemoved
                     && e.RecordName == spec.RecordName
                     && e.ZoneId == "zone-test"
                     && e.TxtContent == "nntpd-token"
                     && e.RecoveryEntryId is not null);
            Assert.DoesNotContain(
                later.Events,
                static e => e.Type == AcmeTransactionJournal.EventDnsChallengeRecoveredAndRemoved);
            Assert.Empty(recoveredJournal.LoadUnattributedEvents());
        }

        [Fact]
        public async Task Orphan_dns_recovery_without_transaction_id_is_not_attached_to_a_later_request()
        {
            using var dir = new TempStateDir();
            var client = new InMemoryCloudflareDnsClient();
            client.Seed(Txt("orphan-rec", "_acme-challenge." + NntpdFqdn, "orphan-token"));
            WriteDns01Journal(
                dir.Path,
                NntpdFqdn,
                "cccccccccccccccccccccccccccccccc",
                "placed",
                "orphan-rec",
                "_acme-challenge." + NntpdFqdn,
                "orphan-token");

            var journal = new AcmeTransactionJournal(dir.Path, NntpdFqdn);
            var laterId = journal.BeginTransaction([NntpdFqdn], DirectoryUrl, DateTimeOffset.UtcNow);
            var solver = new Dns01Solver(
                client,
                "zone-test",
                new ImmediateTxtResolver(),
                NntpdFqdn,
                dir.Path,
                historyJournal: journal);
            await solver.RecoverAsync(CancellationToken.None);

            var later = Assert.Single(journal.LoadHistory());
            Assert.Equal(laterId, later.Id);
            Assert.DoesNotContain(
                later.Events,
                static e => e.Type == AcmeTransactionJournal.EventDnsChallengeRecoveredAndRemoved);
            var recovered = Assert.Single(journal.LoadUnattributedEvents());
            Assert.Equal(AcmeTransactionJournal.EventDnsChallengeRecoveredAndRemoved, recovered.Type);
            Assert.Equal("_acme-challenge." + NntpdFqdn, recovered.RecordName);
            Assert.Equal("orphan-rec", recovered.RecordId);
            Assert.Equal("cccccccccccccccccccccccccccccccc", recovered.RecoveryEntryId);
            Assert.DoesNotContain(client.Snapshot(), static r => r.Id == "orphan-rec");
        }

        private static Dns01Solver CreateSolver(ICloudflareDnsClient client, string fqdn, string stateDir) =>
            new(
                client,
                "zone-test",
                new ImmediateTxtResolver(),
                fqdn,
                stateDir,
                propagationTimeout: TimeSpan.FromSeconds(2),
                propagationInterval: TimeSpan.FromMilliseconds(10));

        private static void WriteDns01Journal(
            string stateDir,
            string fqdn,
            string entryId,
            string phase,
            string recordId,
            string name,
            string content)
        {
            AcmePaths.EnsureCertificateIdentityLayout(stateDir, fqdn);
            var payload = """
                {
                  "version": 1,
                  "entry_id": "%ENTRY%",
                  "phase": "%PHASE%",
                  "zone_id": "zone-test",
                  "name": "%NAME%",
                  "content": "%CONTENT%",
                  "record_id": "%RECORD%",
                  "fqdn": "%FQDN%"
                }
                """
                .Replace("%ENTRY%", entryId, StringComparison.Ordinal)
                .Replace("%PHASE%", phase, StringComparison.Ordinal)
                .Replace("%NAME%", name, StringComparison.Ordinal)
                .Replace("%CONTENT%", content, StringComparison.Ordinal)
                .Replace("%RECORD%", recordId, StringComparison.Ordinal)
                .Replace("%FQDN%", fqdn, StringComparison.Ordinal);
            File.WriteAllText(Path.Combine(AcmePaths.Dns01RecoveryDir(stateDir, fqdn), entryId + ".json"), payload);
        }

        private static CloudflareDnsRecord Txt(string id, string name, string content) =>
            new()
            {
                Id = id,
                Type = CloudflareDnsRecordTypes.TXT,
                Name = name,
                Content = content,
                Ttl = Dns01Solver.ChallengeTtlSeconds,
                Proxied = false,
            };

        private sealed class ScriptedIssuer : ICertificateIssuer
        {
            public Exception? ThrowOnIssue { get; set; }

            public Task<CertificateMaterial> IssueAsync(
                IReadOnlyList<string> domains,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (ThrowOnIssue is not null)
                {
                    throw ThrowOnIssue;
                }

                return Task.FromResult(TestCertificateFactory.CreateMaterial(domains, DateTimeOffset.UtcNow.AddDays(90)));
            }
        }
    }
}
