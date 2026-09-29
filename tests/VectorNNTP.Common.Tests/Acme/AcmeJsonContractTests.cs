using System.Security.Cryptography;
using System.Text.Json;
using VectorNNTP.Common.Tests.TestDoubles;
using VectorNNTP.NNTPD.Acme;
using VectorNNTP.NNTPD.Configuration;

namespace VectorNNTP.Common.Tests.Acme;

/// <summary>
/// Locks the on-disk ACME JSON contracts used by AccountStore, Dns01Solver, and AcmeTransactionJournal.
/// </summary>
public sealed class AcmeJsonContractTests
{
    [Fact]
    public void AccountRegistration_Serialize_MatchesHistoricalShape()
    {
        var json = JsonSerializer.Serialize(
            new AcmeAccountRegistrationPayload
            {
                AccountUri = "https://acme.example/acme/acct/1",
                DirectoryUrl = "https://dir.example/directory",
                RegistrationBody = "",
            },
            AcmeJsonSerializerContext.Default.AcmeAccountRegistrationPayload);

        Assert.Equal(
            """
            {
              "account_uri": "https://acme.example/acme/acct/1",
              "directory_url": "https://dir.example/directory",
              "registration_body": ""
            }
            """.ReplaceLineEndings("\n"),
            json.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void AccountPending_Serialize_MatchesHistoricalShape()
    {
        var json = JsonSerializer.Serialize(
            new AcmeAccountPendingPayload
            {
                Version = 1,
                DirectoryUrl = "https://dir.example/directory",
            },
            AcmeJsonSerializerContext.Default.AcmeAccountPendingPayload);

        Assert.Equal(
            """
            {
              "version": 1,
              "directory_url": "https://dir.example/directory"
            }
            """.ReplaceLineEndings("\n"),
            json.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void AccountStore_Save_WritesRegistrationJsonReadableByLoad()
    {
        using var dir = new TempStateDir();
        var store = new AccountStore(dir.Path);
        using var rsa = RSA.Create(2048);
        var key = rsa.ExportPkcs8PrivateKey();

        store.Save(new AcmeAccountState(
            "https://acme.example/acme/acct/1",
            AcmeCloudflareOptions.DefaultAcmeDirectoryUrl,
            key,
            "body"));

        var meta = File.ReadAllText(AcmePaths.AccountMetaPath(dir.Path));
        Assert.Contains("\"account_uri\":", meta, StringComparison.Ordinal);
        Assert.Contains("\"directory_url\":", meta, StringComparison.Ordinal);
        Assert.Contains("\"registration_body\":", meta, StringComparison.Ordinal);
        Assert.EndsWith(Environment.NewLine, meta);

        var loaded = store.Load();
        Assert.NotNull(loaded);
        Assert.Equal("https://acme.example/acme/acct/1", loaded.AccountUri);
        Assert.Equal(AcmeCloudflareOptions.DefaultAcmeDirectoryUrl, loaded.DirectoryUrl);
        Assert.Equal("body", loaded.RegistrationBody);
    }

    [Fact]
    public void AccountStore_LoadsLegacySnakeCaseRegistrationFile()
    {
        using var dir = new TempStateDir();
        AcmePaths.EnsureStateLayout(dir.Path);
        using var rsa = RSA.Create(2048);
        var key = rsa.ExportPkcs8PrivateKey();
        File.WriteAllBytes(AcmePaths.AccountKeyPath(dir.Path), key);
        File.WriteAllText(
            AcmePaths.AccountMetaPath(dir.Path),
            """
            {
              "account_uri": "https://acme.example/acme/acct/legacy",
              "directory_url": "https://dir.example/directory",
              "registration_body": "legacy-body"
            }
            """ + Environment.NewLine);

        var loaded = new AccountStore(dir.Path).Load();
        Assert.NotNull(loaded);
        Assert.Equal("https://acme.example/acme/acct/legacy", loaded.AccountUri);
        Assert.Equal("legacy-body", loaded.RegistrationBody);
    }

    [Fact]
    public void AccountStore_PendingMeta_WrittenOnFailedRegistration()
    {
        using var dir = new TempStateDir();
        var store = new AccountStore(dir.Path);
        using var rsa = RSA.Create(2048);
        var key = rsa.ExportPkcs8PrivateKey();

        Assert.Throws<AcmeAccountException>(() =>
            store.EnsureRegistered(
                "https://dir.example/directory",
                () => key,
                _ => throw new InvalidOperationException("boom")));

        var pending = File.ReadAllText(AcmePaths.AccountPendingMetaPath(dir.Path));
        Assert.Equal(
            """
            {
              "version": 1,
              "directory_url": "https://dir.example/directory"
            }
            """.ReplaceLineEndings("\n") + Environment.NewLine.ReplaceLineEndings("\n"),
            pending.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Dns01ChallengeJournal_Serialize_WritesNullOptionalFields()
    {
        var json = JsonSerializer.Serialize(
            new Dns01ChallengeJournalPayload
            {
                Version = 1,
                EntryId = "e1",
                Phase = "creating",
                ZoneId = "z",
                Name = "n",
                Content = "c",
                RecordId = null,
                Fqdn = "f.example",
                TransactionId = null,
            },
            AcmeJsonSerializerContext.Default.Dns01ChallengeJournalPayload);

        Assert.Equal(
            """
            {
              "version": 1,
              "entry_id": "e1",
              "phase": "creating",
              "zone_id": "z",
              "name": "n",
              "content": "c",
              "record_id": null,
              "fqdn": "f.example",
              "transaction_id": null
            }
            """.ReplaceLineEndings("\n"),
            json.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void AcmeTransactionJournal_Serialize_MatchesHistoricalWireShape()
    {
        var started = DateTimeOffset.Parse("2024-01-02T03:04:05.6789012Z").ToUniversalTime();
        var payload = new AcmeJournalWireDocument
        {
            Version = 1,
            Fqdn = "nntpd01.usenet.ninja",
            Transactions =
            [
                new AcmeJournalWireTransaction
                {
                    Id = "abc",
                    StartedAt = started.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
                    CompletedAt = null,
                    Identifiers = ["nntpd01.usenet.ninja"],
                    DirectoryUrl = "https://dir.example/directory",
                    Status = AcmeTransactionJournal.StatusInProgress,
                    Events =
                    [
                        new AcmeJournalWireEvent
                        {
                            Type = AcmeTransactionJournal.EventCertificateRequestStarted,
                            At = started.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
                            RecordName = null,
                            ZoneId = null,
                            RecordId = null,
                            TxtContent = null,
                            GenerationId = null,
                            NotBefore = null,
                            NotAfter = null,
                            FailureCategory = null,
                            FailureDetail = null,
                            RecoveryEntryId = null,
                        },
                    ],
                    SerialNumber = null,
                    Thumbprint = null,
                    NotBefore = null,
                    NotAfter = null,
                    GenerationId = null,
                    FailureCategory = null,
                    FailureDetail = null,
                },
            ],
            UnattributedEvents = [],
        };

        var json = JsonSerializer.Serialize(payload, AcmeJsonSerializerContext.Default.AcmeJournalWireDocument);
        Assert.Equal(
            """
            {
              "version": 1,
              "fqdn": "nntpd01.usenet.ninja",
              "transactions": [
                {
                  "id": "abc",
                  "started_at": "2024-01-02T03:04:05.6789012\u002B00:00",
                  "completed_at": null,
                  "identifiers": [
                    "nntpd01.usenet.ninja"
                  ],
                  "directory_url": "https://dir.example/directory",
                  "status": "in_progress",
                  "events": [
                    {
                      "type": "certificate_request_started",
                      "at": "2024-01-02T03:04:05.6789012\u002B00:00",
                      "record_name": null,
                      "zone_id": null,
                      "record_id": null,
                      "txt_content": null,
                      "generation_id": null,
                      "not_before": null,
                      "not_after": null,
                      "failure_category": null,
                      "failure_detail": null,
                      "recovery_entry_id": null
                    }
                  ],
                  "serial_number": null,
                  "thumbprint": null,
                  "not_before": null,
                  "not_after": null,
                  "generation_id": null,
                  "failure_category": null,
                  "failure_detail": null
                }
              ],
              "unattributed_events": []
            }
            """.ReplaceLineEndings("\n"),
            json.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void AcmeTransactionJournal_RoundTrip_PreservesHistory()
    {
        using var dir = new TempStateDir();
        const string fqdn = "nntpd01.usenet.ninja";
        var journal = new AcmeTransactionJournal(dir.Path, fqdn);
        var id = journal.BeginTransaction([fqdn], "https://dir.example/directory", DateTimeOffset.UtcNow);
        journal.CompleteFailure(id, "test_failure", "detail", DateTimeOffset.UtcNow);

        var raw = File.ReadAllText(journal.FilePath);
        Assert.Contains("\"version\": 1", raw, StringComparison.Ordinal);
        Assert.Contains("\"unattributed_events\":", raw, StringComparison.Ordinal);
        Assert.EndsWith(Environment.NewLine, raw);

        var reloaded = new AcmeTransactionJournal(dir.Path, fqdn).LoadHistory();
        var tx = Assert.Single(reloaded);
        Assert.Equal(id, tx.Id);
        Assert.Equal(AcmeTransactionJournal.StatusFailed, tx.Status);
        Assert.Equal("test_failure", tx.FailureCategory);
    }

    [Fact]
    public void AcmeTransactionJournal_LoadsLegacyDictionaryShapedFile()
    {
        using var dir = new TempStateDir();
        const string fqdn = "nntpd01.usenet.ninja";
        AcmePaths.EnsureStateLayout(dir.Path);
        var path = AcmePaths.TransactionJournalPath(dir.Path, fqdn);
        File.WriteAllText(
            path,
            """
            {
              "version": 1,
              "fqdn": "nntpd01.usenet.ninja",
              "transactions": [
                {
                  "id": "legacyid",
                  "started_at": "2024-01-02T03:04:05.6789012+00:00",
                  "completed_at": null,
                  "identifiers": [ "nntpd01.usenet.ninja" ],
                  "directory_url": "https://dir.example/directory",
                  "status": "in_progress",
                  "events": [
                    {
                      "type": "certificate_request_started",
                      "at": "2024-01-02T03:04:05.6789012+00:00",
                      "record_name": null,
                      "zone_id": null,
                      "record_id": null,
                      "txt_content": null,
                      "generation_id": null,
                      "not_before": null,
                      "not_after": null,
                      "failure_category": null,
                      "failure_detail": null,
                      "recovery_entry_id": null
                    }
                  ],
                  "serial_number": null,
                  "thumbprint": null,
                  "not_before": null,
                  "not_after": null,
                  "generation_id": null,
                  "failure_category": null,
                  "failure_detail": null
                }
              ],
              "unattributed_events": []
            }
            """ + Environment.NewLine);

        var history = new AcmeTransactionJournal(dir.Path, fqdn).LoadHistory();
        var tx = Assert.Single(history);
        Assert.Equal("legacyid", tx.Id);
        Assert.Equal(AcmeTransactionJournal.StatusInProgress, tx.Status);
        Assert.Equal(fqdn, Assert.Single(tx.Identifiers));
        Assert.Contains(tx.Events, static e => e.Type == AcmeTransactionJournal.EventCertificateRequestStarted);
    }
}
