using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace VectorNNTP.Common.Acme
{
    /// <summary>
    /// Persistent ACME transaction journal for one certificate FQDN.
    /// </summary>
    /// <remarks>
    /// The journal is one file, <c>journal/{fqdn}.json</c>. Each certificate request
    /// is a transaction that accumulates events for the complete ACME lifecycle.
    /// Historical transactions are retained indefinitely. Same-FQDN writers are
    /// serialized in-process and across processes by an exclusive lock on
    /// <c>{journalPath}.lock</c>. Distinct FQDN files do not share a lock.
    /// Writes use <see cref="AtomicFile"/> so a crash cannot leave a partial journal.
    /// </remarks>
    public sealed class AcmeTransactionJournal
    {
        /// <summary>Current journal schema version.</summary>
        public const int SchemaVersion = 1;

        /// <summary>Transaction has started and has not finished.</summary>
        public const string StatusInProgress = "in_progress";

        /// <summary>Issuance completed and a certificate was persisted.</summary>
        public const string StatusSucceeded = "succeeded";

        /// <summary>Issuance failed.</summary>
        public const string StatusFailed = "failed";

        /// <summary>A certificate request was recorded.</summary>
        public const string EventCertificateRequestStarted = "certificate_request_started";

        /// <summary>A DNS-01 TXT record was created in Cloudflare.</summary>
        public const string EventDnsChallengeCreated = "dns_challenge_created";

        /// <summary>Authoritative resolvers reported the challenge TXT.</summary>
        public const string EventDnsChallengePropagated = "dns_challenge_propagated";

        /// <summary>ACME challenge validation was triggered.</summary>
        public const string EventAcmeValidationStarted = "acme_validation_started";

        /// <summary>The ACME order reached the ready state.</summary>
        public const string EventAcmeValidationSucceeded = "acme_validation_succeeded";

        /// <summary>A DNS-01 TXT record was deleted.</summary>
        public const string EventDnsChallengeRemoved = "dns_challenge_removed";

        /// <summary>An orphaned DNS-01 TXT record was deleted during recovery.</summary>
        public const string EventDnsChallengeRecoveredAndRemoved = "dns_challenge_recovered_and_removed";

        /// <summary>The issuer returned certificate material.</summary>
        public const string EventCertificateIssued = "certificate_issued";

        /// <summary>Certificate bytes were written to a generation directory.</summary>
        public const string EventCertificatePersisted = "certificate_persisted";

        /// <summary>The FQDN current pointer was switched to the new generation.</summary>
        public const string EventCertificatePromoted = "certificate_promoted";

        /// <summary>Issuance failed.</summary>
        public const string EventCertificateIssuanceFailed = "certificate_issuance_failed";

        private static readonly ConcurrentDictionary<string, object> FileGates = new(StringComparer.OrdinalIgnoreCase);

        private readonly string _fqdn;
        private readonly string _journalPath;
        private readonly string _journalLockPath;
        private string? _activeTransactionId;

        /// <summary>Initializes a new instance of the <see cref="AcmeTransactionJournal"/> class.</summary>
        /// <param name="stateDir">Shared ACME state root.</param>
        /// <param name="fqdn">Certificate FQDN that owns this journal file.</param>
        public AcmeTransactionJournal(string stateDir, string fqdn)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(stateDir);
            _fqdn = CertificateIdentities.NormalizeFqdn(fqdn);
            AcmePaths.EnsureStateLayout(stateDir);
            _journalPath = AcmePaths.TransactionJournalPath(stateDir, _fqdn);
            _journalLockPath = _journalPath + ".lock";
        }

        /// <summary>Gets the FQDN this journal is scoped to.</summary>
        public string Fqdn => _fqdn;

        /// <summary>Gets the persistent journal file path.</summary>
        public string FilePath => _journalPath;

        /// <summary>Gets the exclusive lock file used for same-FQDN journal writes.</summary>
        public string LockPath => _journalLockPath;

        /// <summary>Gets the transaction id that subsequent events attach to, if any.</summary>
        public string? ActiveTransactionId => _activeTransactionId;

        /// <summary>Records a new certificate request and returns its transaction id.</summary>
        /// <param name="identifiers">Requested identifiers/SANs.</param>
        /// <param name="directoryUrl">ACME directory URL used for the request.</param>
        /// <param name="startedAt">Request timestamp.</param>
        /// <param name="cancellationToken">Cancels waiting for the journal write lock.</param>
        /// <returns>The new transaction id.</returns>
        public string BeginTransaction(
            IReadOnlyList<string> identifiers,
            string directoryUrl,
            DateTimeOffset startedAt,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(identifiers);
            ArgumentException.ThrowIfNullOrWhiteSpace(directoryUrl);

            var transactionId = Guid.NewGuid().ToString("N");
            var started = startedAt.ToUniversalTime();
            Mutate(
                document =>
                {
                    document.Transactions.Add(new AcmeJournalTransaction
                    {
                        Id = transactionId,
                        StartedAt = started,
                        Identifiers = [.. identifiers],
                        DirectoryUrl = directoryUrl.Trim(),
                        Status = StatusInProgress,
                        Events =
                        [
                            new AcmeJournalEvent
                            {
                                Type = EventCertificateRequestStarted,
                                At = started,
                            },
                        ],
                    });
                },
                cancellationToken);
            _activeTransactionId = transactionId;
            return transactionId;
        }

        /// <summary>Records that a DNS-01 TXT record was created.</summary>
        /// <param name="recordName">Challenge record name.</param>
        /// <param name="zoneId">Cloudflare zone id.</param>
        /// <param name="recordId">Cloudflare record id.</param>
        /// <param name="txtContent">TXT RDATA.</param>
        public void RecordDnsChallengeCreated(
            string recordName,
            string zoneId,
            string recordId,
            string txtContent) =>
            AppendEvent(new AcmeJournalEvent
            {
                Type = EventDnsChallengeCreated,
                At = DateTimeOffset.UtcNow,
                RecordName = recordName,
                ZoneId = zoneId,
                RecordId = recordId,
                TxtContent = txtContent,
            });

        /// <summary>Records that a DNS-01 TXT value became visible.</summary>
        /// <param name="recordName">Challenge record name.</param>
        public void RecordDnsChallengePropagated(string recordName) =>
            AppendEvent(new AcmeJournalEvent
            {
                Type = EventDnsChallengePropagated,
                At = DateTimeOffset.UtcNow,
                RecordName = recordName,
            });

        /// <summary>Records that ACME challenge validation was triggered.</summary>
        public void RecordAcmeValidationStarted() =>
            AppendEvent(new AcmeJournalEvent
            {
                Type = EventAcmeValidationStarted,
                At = DateTimeOffset.UtcNow,
            });

        /// <summary>Records that the ACME order reached the ready state.</summary>
        public void RecordAcmeValidationSucceeded() =>
            AppendEvent(new AcmeJournalEvent
            {
                Type = EventAcmeValidationSucceeded,
                At = DateTimeOffset.UtcNow,
            });

        /// <summary>Records that a DNS-01 TXT record was deleted.</summary>
        /// <param name="recordName">Challenge record name.</param>
        /// <param name="zoneId">Cloudflare zone id.</param>
        /// <param name="recordId">Cloudflare record id.</param>
        /// <param name="txtContent">TXT RDATA.</param>
        public void RecordDnsChallengeRemoved(
            string recordName,
            string zoneId,
            string recordId,
            string txtContent) =>
            AppendEvent(new AcmeJournalEvent
            {
                Type = EventDnsChallengeRemoved,
                At = DateTimeOffset.UtcNow,
                RecordName = recordName,
                ZoneId = zoneId,
                RecordId = recordId,
                TxtContent = txtContent,
            });

        /// <summary>
        /// Records that recovery deleted an orphaned DNS-01 TXT record.
        /// </summary>
        /// <param name="recoveryEntryId">Recovery file id.</param>
        /// <param name="recordName">Challenge record name.</param>
        /// <param name="zoneId">Cloudflare zone id.</param>
        /// <param name="recordId">Cloudflare record id when known.</param>
        /// <param name="txtContent">TXT RDATA.</param>
        /// <param name="originalTransactionId">
        /// Transaction id persisted on the recovery file. When this id exists in the
        /// journal, the event is attached to that transaction. When it is missing or
        /// unknown, the event is stored as unattributed history and is not attached to
        /// the active transaction.
        /// </param>
        public void RecordDnsChallengeRecoveredAndRemoved(
            string recoveryEntryId,
            string recordName,
            string zoneId,
            string? recordId,
            string txtContent,
            string? originalTransactionId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(recoveryEntryId);
            ArgumentException.ThrowIfNullOrWhiteSpace(recordName);

            var journalEvent = new AcmeJournalEvent
            {
                Type = EventDnsChallengeRecoveredAndRemoved,
                At = DateTimeOffset.UtcNow,
                RecordName = recordName,
                ZoneId = zoneId,
                RecordId = recordId,
                TxtContent = txtContent,
                RecoveryEntryId = recoveryEntryId,
            };

            Mutate(document =>
            {
                if (!string.IsNullOrWhiteSpace(originalTransactionId))
                {
                    var owner = document.Transactions.FirstOrDefault(
                        item => string.Equals(item.Id, originalTransactionId, StringComparison.Ordinal));
                    if (owner is not null)
                    {
                        owner.Events.Add(journalEvent);
                        return;
                    }
                }

                document.UnattributedEvents.Add(journalEvent);
            });
        }

        /// <summary>Records that the issuer returned certificate material.</summary>
        /// <param name="material">Issued certificate material.</param>
        public void RecordCertificateIssued(CertificateMaterial material)
        {
            ArgumentNullException.ThrowIfNull(material);
            AppendEvent(new AcmeJournalEvent
            {
                Type = EventCertificateIssued,
                At = DateTimeOffset.UtcNow,
                NotBefore = material.NotBefore.ToUniversalTime(),
                NotAfter = material.NotAfter.ToUniversalTime(),
            });
        }

        /// <summary>Records that certificate bytes were written to a generation directory.</summary>
        /// <param name="generationId">Live generation id.</param>
        /// <param name="material">Persisted certificate material.</param>
        public void RecordCertificatePersisted(string generationId, CertificateMaterial material)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(generationId);
            ArgumentNullException.ThrowIfNull(material);
            AppendEvent(new AcmeJournalEvent
            {
                Type = EventCertificatePersisted,
                At = DateTimeOffset.UtcNow,
                GenerationId = generationId,
                NotBefore = material.NotBefore.ToUniversalTime(),
                NotAfter = material.NotAfter.ToUniversalTime(),
            });
        }

        /// <summary>Records that the FQDN current pointer was updated.</summary>
        /// <param name="generationId">Promoted generation id.</param>
        public void RecordCertificatePromoted(string generationId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(generationId);
            AppendEvent(new AcmeJournalEvent
            {
                Type = EventCertificatePromoted,
                At = DateTimeOffset.UtcNow,
                GenerationId = generationId,
            });
        }

        /// <summary>Completes a request with certificate fields taken from the issued PFX.</summary>
        /// <param name="transactionId">Transaction started by <see cref="BeginTransaction"/>.</param>
        /// <param name="material">Issued certificate material.</param>
        /// <param name="generationId">Live generation that contains the certificate.</param>
        /// <param name="pfxPassword">PKCS#12 password used to inspect serial and thumbprint.</param>
        /// <param name="completedAt">Completion timestamp.</param>
        public void CompleteSuccess(
            string transactionId,
            CertificateMaterial material,
            string generationId,
            string pfxPassword,
            DateTimeOffset completedAt)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(transactionId);
            ArgumentNullException.ThrowIfNull(material);
            ArgumentException.ThrowIfNullOrWhiteSpace(generationId);
            ArgumentNullException.ThrowIfNull(pfxPassword);

            var inspected = InspectCertificate(material.PfxBytes, pfxPassword);
            var completed = completedAt.ToUniversalTime();
            Mutate(document =>
            {
                var transaction = RequireTransaction(document, transactionId);
                transaction.CompletedAt = completed;
                transaction.Status = StatusSucceeded;
                transaction.SerialNumber = inspected.SerialNumber;
                transaction.Thumbprint = inspected.Thumbprint;
                transaction.NotBefore = inspected.NotBefore ?? material.NotBefore.ToUniversalTime();
                transaction.NotAfter = inspected.NotAfter ?? material.NotAfter.ToUniversalTime();
                transaction.GenerationId = generationId;
                transaction.FailureCategory = null;
                transaction.FailureDetail = null;
            });
            if (string.Equals(_activeTransactionId, transactionId, StringComparison.Ordinal))
            {
                _activeTransactionId = null;
            }
        }

        /// <summary>Completes a request with sanitized failure information.</summary>
        /// <param name="transactionId">Transaction started by <see cref="BeginTransaction"/>.</param>
        /// <param name="failureCategory">Failure category.</param>
        /// <param name="failureDetail">Sanitized failure detail.</param>
        /// <param name="completedAt">Completion timestamp.</param>
        public void CompleteFailure(
            string transactionId,
            string failureCategory,
            string failureDetail,
            DateTimeOffset completedAt)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(transactionId);
            ArgumentException.ThrowIfNullOrWhiteSpace(failureCategory);

            var completed = completedAt.ToUniversalTime();
            var detail = string.IsNullOrWhiteSpace(failureDetail) ? failureCategory : failureDetail;
            Mutate(document =>
            {
                var transaction = RequireTransaction(document, transactionId);
                transaction.CompletedAt = completed;
                transaction.Status = StatusFailed;
                transaction.FailureCategory = failureCategory;
                transaction.FailureDetail = detail;
                transaction.Events.Add(new AcmeJournalEvent
                {
                    Type = EventCertificateIssuanceFailed,
                    At = completed,
                    FailureCategory = failureCategory,
                    FailureDetail = detail,
                });
            });
            if (string.Equals(_activeTransactionId, transactionId, StringComparison.Ordinal))
            {
                _activeTransactionId = null;
            }
        }

        /// <summary>Loads historical transactions for this FQDN only, oldest first.</summary>
        /// <returns>The persisted transactions, or an empty list when the file does not exist.</returns>
        public IReadOnlyList<AcmeJournalTransaction> LoadHistory()
        {
            return WithGate(() => LoadDocument().Transactions);
        }

        /// <summary>
        /// Loads recovery events that could not be attached to an original transaction.
        /// </summary>
        /// <returns>Unattributed events in write order.</returns>
        public IReadOnlyList<AcmeJournalEvent> LoadUnattributedEvents()
        {
            return WithGate(() => LoadDocument().UnattributedEvents);
        }

        private void AppendEvent(AcmeJournalEvent journalEvent)
        {
            var transactionId = _activeTransactionId;
            if (transactionId is null)
            {
                return;
            }

            Mutate(document => RequireTransaction(document, transactionId).Events.Add(journalEvent));
        }

        private void Mutate(Action<AcmeJournalDocument> mutate, CancellationToken cancellationToken = default)
        {
            WithGate(
                () =>
                {
                    var document = LoadDocument();
                    mutate(document);
                    Persist(document);
                    return 0;
                },
                cancellationToken);
        }

        private T WithGate<T>(Func<T> action, CancellationToken cancellationToken = default)
        {
            var gate = FileGates.GetOrAdd(Path.GetFullPath(_journalPath), static _ => new object());
            lock (gate)
            {
                using var fileLock = AcmeExclusiveFileLock.Acquire(_journalLockPath, cancellationToken);
                return action();
            }
        }

        private AcmeJournalDocument LoadDocument()
        {
            if (!File.Exists(_journalPath))
            {
                return new AcmeJournalDocument
                {
                    Version = SchemaVersion,
                    Fqdn = _fqdn,
                };
            }

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(_journalPath));
                var root = doc.RootElement;
                var version = root.GetProperty("version").GetInt32();
                if (version != SchemaVersion)
                {
                    throw new AcmeStorageException("malformed_journal", $"unsupported_version={version}");
                }

                var fqdn = root.GetProperty("fqdn").GetString() ?? string.Empty;
                if (!string.Equals(fqdn, _fqdn, StringComparison.Ordinal))
                {
                    throw new AcmeStorageException("malformed_journal", "fqdn mismatch");
                }

                var document = new AcmeJournalDocument
                {
                    Version = version,
                    Fqdn = fqdn,
                };
                if (root.TryGetProperty("transactions", out var transactionsElement)
                    && transactionsElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in transactionsElement.EnumerateArray())
                    {
                        document.Transactions.Add(ParseTransaction(item));
                    }
                }

                if (root.TryGetProperty("unattributed_events", out var unattributedElement)
                    && unattributedElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in unattributedElement.EnumerateArray())
                    {
                        document.UnattributedEvents.Add(ParseEvent(item));
                    }
                }

                return document;
            }
            catch (AcmeStorageException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or FormatException)
            {
                throw new AcmeStorageException("malformed_journal", ex.GetType().Name);
            }
        }

        private void Persist(AcmeJournalDocument document)
        {
            var transactions = new List<AcmeJournalWireTransaction>(document.Transactions.Count);
            foreach (var transaction in document.Transactions)
            {
                transactions.Add(SerializeTransaction(transaction));
            }

            var unattributed = new List<AcmeJournalWireEvent>(document.UnattributedEvents.Count);
            foreach (var journalEvent in document.UnattributedEvents)
            {
                unattributed.Add(SerializeEvent(journalEvent));
            }

            var payload = new AcmeJournalWireDocument
            {
                Version = document.Version,
                Fqdn = document.Fqdn,
                Transactions = transactions,
                UnattributedEvents = unattributed,
            };

            AtomicFile.WriteText(
                _journalPath,
                JsonSerializer.Serialize(payload, AcmeJsonSerializerContext.Default.AcmeJournalWireDocument)
                + Environment.NewLine);
        }

        private static AcmeJournalWireTransaction SerializeTransaction(AcmeJournalTransaction transaction)
        {
            var events = new List<AcmeJournalWireEvent>(transaction.Events.Count);
            foreach (var journalEvent in transaction.Events)
            {
                events.Add(SerializeEvent(journalEvent));
            }

            return new AcmeJournalWireTransaction
            {
                Id = transaction.Id,
                StartedAt = FormatTimestamp(transaction.StartedAt),
                CompletedAt = transaction.CompletedAt is { } completed ? FormatTimestamp(completed) : null,
                Identifiers = transaction.Identifiers is List<string> list
                    ? list
                    : [.. transaction.Identifiers],
                DirectoryUrl = transaction.DirectoryUrl,
                Status = transaction.Status,
                Events = events,
                SerialNumber = transaction.SerialNumber,
                Thumbprint = transaction.Thumbprint,
                NotBefore = transaction.NotBefore is { } notBefore ? FormatTimestamp(notBefore) : null,
                NotAfter = transaction.NotAfter is { } notAfter ? FormatTimestamp(notAfter) : null,
                GenerationId = transaction.GenerationId,
                FailureCategory = transaction.FailureCategory,
                FailureDetail = transaction.FailureDetail,
            };
        }

        private static AcmeJournalWireEvent SerializeEvent(AcmeJournalEvent journalEvent) =>
            new()
            {
                Type = journalEvent.Type,
                At = FormatTimestamp(journalEvent.At),
                RecordName = journalEvent.RecordName,
                ZoneId = journalEvent.ZoneId,
                RecordId = journalEvent.RecordId,
                TxtContent = journalEvent.TxtContent,
                GenerationId = journalEvent.GenerationId,
                NotBefore = journalEvent.NotBefore is { } notBefore ? FormatTimestamp(notBefore) : null,
                NotAfter = journalEvent.NotAfter is { } notAfter ? FormatTimestamp(notAfter) : null,
                FailureCategory = journalEvent.FailureCategory,
                FailureDetail = journalEvent.FailureDetail,
                RecoveryEntryId = journalEvent.RecoveryEntryId,
            };

        private AcmeJournalTransaction ParseTransaction(JsonElement root)
        {
            var identifiers = new List<string>();
            if (root.TryGetProperty("identifiers", out var identifiersElement)
                && identifiersElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in identifiersElement.EnumerateArray())
                {
                    var value = item.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        identifiers.Add(value);
                    }
                }
            }

            var events = new List<AcmeJournalEvent>();
            if (root.TryGetProperty("events", out var eventsElement)
                && eventsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in eventsElement.EnumerateArray())
                {
                    events.Add(ParseEvent(item));
                }
            }

            return new AcmeJournalTransaction
            {
                Id = root.GetProperty("id").GetString() ?? string.Empty,
                StartedAt = ParseTimestamp(root, "started_at") ?? DateTimeOffset.MinValue,
                CompletedAt = ParseTimestamp(root, "completed_at"),
                Identifiers = identifiers,
                DirectoryUrl = root.GetProperty("directory_url").GetString() ?? string.Empty,
                Status = root.GetProperty("status").GetString() ?? string.Empty,
                Events = events,
                SerialNumber = ReadOptionalString(root, "serial_number"),
                Thumbprint = ReadOptionalString(root, "thumbprint"),
                NotBefore = ParseTimestamp(root, "not_before"),
                NotAfter = ParseTimestamp(root, "not_after"),
                GenerationId = ReadOptionalString(root, "generation_id"),
                FailureCategory = ReadOptionalString(root, "failure_category"),
                FailureDetail = ReadOptionalString(root, "failure_detail"),
            };
        }

        private static AcmeJournalEvent ParseEvent(JsonElement root) =>
            new()
            {
                Type = root.GetProperty("type").GetString() ?? string.Empty,
                At = ParseTimestamp(root, "at") ?? DateTimeOffset.MinValue,
                RecordName = ReadOptionalString(root, "record_name"),
                ZoneId = ReadOptionalString(root, "zone_id"),
                RecordId = ReadOptionalString(root, "record_id"),
                TxtContent = ReadOptionalString(root, "txt_content"),
                GenerationId = ReadOptionalString(root, "generation_id"),
                NotBefore = ParseTimestamp(root, "not_before"),
                NotAfter = ParseTimestamp(root, "not_after"),
                FailureCategory = ReadOptionalString(root, "failure_category"),
                FailureDetail = ReadOptionalString(root, "failure_detail"),
                RecoveryEntryId = ReadOptionalString(root, "recovery_entry_id"),
            };

        private AcmeJournalTransaction RequireTransaction(AcmeJournalDocument document, string transactionId)
        {
            var transaction = document.Transactions.FirstOrDefault(
                item => string.Equals(item.Id, transactionId, StringComparison.Ordinal));
            if (transaction is null)
            {
                throw new AcmeStorageException("missing_journal_transaction", transactionId);
            }

            if (!string.Equals(document.Fqdn, _fqdn, StringComparison.Ordinal))
            {
                throw new AcmeStorageException("malformed_journal", "fqdn mismatch");
            }

            return transaction;
        }

        private static string FormatTimestamp(DateTimeOffset value) =>
            value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);

        private static DateTimeOffset? ParseTimestamp(JsonElement root, string name)
        {
            if (!root.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            var raw = element.GetString();
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            return DateTimeOffset.Parse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
                .ToUniversalTime();
        }

        private static string? ReadOptionalString(JsonElement root, string name)
        {
            if (!root.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            var value = element.GetString();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        private static CertificateInspection InspectCertificate(byte[] pfxBytes, string password)
        {
            X509Certificate2? cert = null;
            try
            {
                cert = PfxCrypto.LoadCertificate(pfxBytes, password);
                return new CertificateInspection(
                    cert.SerialNumber,
                    cert.Thumbprint,
                    cert.NotBefore.ToUniversalTime(),
                    cert.NotAfter.ToUniversalTime());
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new AcmeCertificateException("journal_certificate_inspect_failed", ex.GetType().Name);
            }
            finally
            {
                cert?.Dispose();
            }
        }

        private readonly record struct CertificateInspection(
            string? SerialNumber,
            string? Thumbprint,
            DateTimeOffset? NotBefore,
            DateTimeOffset? NotAfter);
    }

    /// <summary>On-disk ACME journal document for one FQDN.</summary>
    public sealed class AcmeJournalDocument
    {
        /// <summary>Gets or sets the journal schema version.</summary>
        public int Version { get; set; }

        /// <summary>Gets or sets the certificate FQDN.</summary>
        public string Fqdn { get; set; } = string.Empty;

        /// <summary>Gets the historical transactions in write order.</summary>
        public List<AcmeJournalTransaction> Transactions { get; } = [];

        /// <summary>
        /// Gets recovery events that could not be attached to an original transaction.
        /// </summary>
        public List<AcmeJournalEvent> UnattributedEvents { get; } = [];
    }

    /// <summary>One ACME request and its lifecycle events.</summary>
    public sealed class AcmeJournalTransaction
    {
        /// <summary>Gets or sets the transaction id.</summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>Gets or sets when the request started.</summary>
        public DateTimeOffset StartedAt { get; set; }

        /// <summary>Gets or sets when the request finished, if finished.</summary>
        public DateTimeOffset? CompletedAt { get; set; }

        /// <summary>Gets or sets the requested identifiers/SANs.</summary>
        public IReadOnlyList<string> Identifiers { get; set; } = [];

        /// <summary>Gets or sets the ACME directory URL used for the request.</summary>
        public string DirectoryUrl { get; set; } = string.Empty;

        /// <summary>Gets or sets the request status.</summary>
        public string Status { get; set; } = string.Empty;

        /// <summary>Gets the lifecycle events in write order.</summary>
        public List<AcmeJournalEvent> Events { get; set; } = [];

        /// <summary>Gets or sets the issued certificate serial number when known.</summary>
        public string? SerialNumber { get; set; }

        /// <summary>Gets or sets the issued certificate thumbprint when known.</summary>
        public string? Thumbprint { get; set; }

        /// <summary>Gets or sets the issued certificate not-before when known.</summary>
        public DateTimeOffset? NotBefore { get; set; }

        /// <summary>Gets or sets the issued certificate not-after when known.</summary>
        public DateTimeOffset? NotAfter { get; set; }

        /// <summary>Gets or sets the live generation id that contains the certificate.</summary>
        public string? GenerationId { get; set; }

        /// <summary>Gets or sets the failure category when issuance failed.</summary>
        public string? FailureCategory { get; set; }

        /// <summary>Gets or sets sanitized failure detail when issuance failed.</summary>
        public string? FailureDetail { get; set; }
    }

    /// <summary>One historical ACME lifecycle event.</summary>
    public sealed class AcmeJournalEvent
    {
        /// <summary>Gets or sets the event type.</summary>
        public string Type { get; set; } = string.Empty;

        /// <summary>Gets or sets when the event occurred.</summary>
        public DateTimeOffset At { get; set; }

        /// <summary>Gets or sets the DNS-01 record name when applicable.</summary>
        public string? RecordName { get; set; }

        /// <summary>Gets or sets the Cloudflare zone id when applicable.</summary>
        public string? ZoneId { get; set; }

        /// <summary>Gets or sets the Cloudflare record id when applicable.</summary>
        public string? RecordId { get; set; }

        /// <summary>Gets or sets the TXT RDATA when applicable.</summary>
        public string? TxtContent { get; set; }

        /// <summary>Gets or sets the live generation id when applicable.</summary>
        public string? GenerationId { get; set; }

        /// <summary>Gets or sets the certificate not-before when applicable.</summary>
        public DateTimeOffset? NotBefore { get; set; }

        /// <summary>Gets or sets the certificate not-after when applicable.</summary>
        public DateTimeOffset? NotAfter { get; set; }

        /// <summary>Gets or sets the failure category when applicable.</summary>
        public string? FailureCategory { get; set; }

        /// <summary>Gets or sets sanitized failure detail when applicable.</summary>
        public string? FailureDetail { get; set; }

        /// <summary>Gets or sets the DNS-01 recovery file id when applicable.</summary>
        public string? RecoveryEntryId { get; set; }
    }
}
