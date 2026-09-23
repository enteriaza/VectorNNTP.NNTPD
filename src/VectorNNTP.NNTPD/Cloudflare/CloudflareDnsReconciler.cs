using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;
using VectorNNTP.NNTPD.Configuration;
using VectorNNTP.NNTPD.Networking;

namespace VectorNNTP.NNTPD.Cloudflare;

/// <summary>
/// Idempotent reconciler that makes Cloudflare A/AAAA records for a single FQDN match a desired IP set,
/// and removes every DNS record for that exact FQDN on clean-up.
/// </summary>
/// <remarks>
/// <para>
/// Only the exact FQDN is mutated. Unrelated hostnames and record types outside startup A/AAAA
/// reconciliation are untouched during reconcile. Shutdown clean-up deletes all record types for the
/// exact FQDN only (never parent, child/subdomain, or other names).
/// Private (RFC1918 / ULA) addresses in the desired set are published intentionally.
/// </para>
/// <para>
/// Cloudflare does not provide an atomic multi-record transaction. Each reconcile attempt:
/// reads both families, creates all missing desired records across A and AAAA before deleting any stale
/// records, then deletes stale/duplicates, then verifies exact sets (including TTL
/// <see cref="CloudflareManagedDnsPolicy.ManagedTtl"/> and <c>proxied=false</c>).
/// Clean-up lists all types for the exact name, deletes by record id, then verifies none remain.
/// Concurrent reconcile/clean-up calls on the same instance are serialized. Intermediate states remain
/// externally visible. Permanent auth/config failures fail immediately; transient/uncertain/verification
/// failures retry with bounded backoff after re-reading remote state. HTTP 429 retries and reconciler
/// attempt backoffs share a single operation deadline (<c>CloudFlareOperationTimeout</c>, default 2 minutes)
/// linked with the caller token.
/// </para>
/// </remarks>
public sealed class CloudflareDnsReconciler : ICloudflareDnsReconciler
{
    /// <summary>Maximum reconcile attempts within a single <see cref="ReconcileAsync"/> call.</summary>
    internal const int MaxAttempts = 3;

    private readonly ICloudflareDnsClient _client;
    private readonly IOptions<NntpdOptions> _options;
    private readonly ILogger<CloudflareDnsReconciler> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Gets or sets the delay function used for reconciler attempt backoff (tests may replace this).
    /// </summary>
    internal Func<TimeSpan, CancellationToken, Task> DelayAsync { get; set; } =
        static (delay, cancellationToken) => Task.Delay(delay, cancellationToken);

    /// <summary>
    /// Initializes a new instance of the <see cref="CloudflareDnsReconciler"/> class.
    /// </summary>
    public CloudflareDnsReconciler(
        ICloudflareDnsClient client,
        IOptions<NntpdOptions> options,
        ILogger<CloudflareDnsReconciler> logger)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _client = client;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task ReconcileAsync(
        string zoneId,
        string fqdn,
        ResolvedBindAddresses desired,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(zoneId);
        ArgumentException.ThrowIfNullOrWhiteSpace(fqdn);
        ArgumentNullException.ThrowIfNull(desired);

        if (!desired.HasAny)
        {
            throw new InvalidOperationException(
                "Cannot reconcile Cloudflare DNS with an empty bind-address set. " +
                "Configure BindAddress so at least one eligible non-loopback, non-link-local IP is available.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var budget = CloudflareOperationBudget.Begin(
                _options.Value.CloudFlareOperationTimeout,
                cancellationToken,
                out var operationToken);

            var desiredV4 = ToContentSet(desired.IPv4);
            var desiredV6 = ToContentSet(desired.IPv6);

            _logger.LogInformation(
                "Reconciling Cloudflare DNS for {Fqdn}: desired A={ACount}, AAAA={AaaaCount} " +
                "(staged create-all-then-delete; managed TTL={ManagedTtl}, proxied=false; " +
                "non-atomic; success requires verified exact match)",
                fqdn,
                desiredV4.Count,
                desiredV6.Count,
                CloudflareManagedDnsPolicy.ManagedTtl);

            Exception? lastFailure = null;

            for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                operationToken.ThrowIfCancellationRequested();

                try
                {
                    await ReconcileAttemptAsync(zoneId, fqdn, desiredV4, desiredV6, attempt, operationToken)
                        .ConfigureAwait(false);

                    _logger.LogInformation(
                        "Cloudflare DNS reconciliation verified for {Fqdn} on attempt {Attempt}/{MaxAttempts} " +
                        "(A={ACount}, AAAA={AaaaCount})",
                        fqdn,
                        attempt,
                        MaxAttempts,
                        desiredV4.Count,
                        desiredV6.Count);
                    return;
                }
                catch (OperationCanceledException) when (operationToken.IsCancellationRequested)
                {
                    _logger.LogWarning(
                        "Cloudflare DNS reconciliation for {Fqdn} was canceled on attempt {Attempt}. " +
                        "Remote DNS may be in an intermediate state; the next startup will re-read and converge",
                        fqdn,
                        attempt);
                    throw;
                }
                catch (CloudflareDnsException ex) when (ex.IsPermanentFailure)
                {
                    _logger.LogError(
                        ex,
                        "Cloudflare DNS reconciliation for {Fqdn} failed permanently on attempt {Attempt} " +
                        "(operation={Operation}, status={StatusCode}). Not retrying",
                        fqdn,
                        attempt,
                        ex.FailedOperation,
                        ex.StatusCode);
                    throw;
                }
                catch (Exception ex) when (ex is CloudflareDnsException or InvalidOperationException)
                {
                    lastFailure = ex;
                    var uncertain = ex is CloudflareDnsException { IsOutcomeUncertain: true };

                    _logger.LogError(
                        ex,
                        "Cloudflare DNS reconciliation attempt {Attempt}/{MaxAttempts} for {Fqdn} failed " +
                        "(uncertainOutcome={Uncertain}, operation={Operation}). " +
                        "Partial A/AAAA mutations are not rolled back atomically; " +
                        "a subsequent attempt will re-read Cloudflare and converge toward the desired set",
                        attempt,
                        MaxAttempts,
                        fqdn,
                        uncertain,
                        (ex as CloudflareDnsException)?.FailedOperation);

                    if (attempt >= MaxAttempts)
                    {
                        break;
                    }

                    var delay = GetAttemptBackoff(attempt);
                    _logger.LogWarning(
                        "Waiting {DelayMs} ms before Cloudflare DNS reconcile retry {NextAttempt}/{MaxAttempts} for {Fqdn}",
                        delay.TotalMilliseconds,
                        attempt + 1,
                        MaxAttempts,
                        fqdn);
                    await DelayRespectingBudgetAsync(delay, operationToken).ConfigureAwait(false);
                }
            }

            throw new CloudflareDnsException(
                $"Cloudflare DNS reconciliation for '{fqdn}' failed after {MaxAttempts} attempt(s). " +
                "Startup must not proceed: desired A/AAAA sets were not verified. " +
                "Remote DNS may reflect an intermediate non-atomic multi-record state; " +
                "the next successful reconcile will re-read and converge.",
                lastFailure ?? new InvalidOperationException("Unknown reconciliation failure."))
            {
                IsOutcomeUncertain = lastFailure is CloudflareDnsException { IsOutcomeUncertain: true },
                IsPermanentFailure = lastFailure is CloudflareDnsException { IsPermanentFailure: true },
                FailedOperation = "ReconcileAsync",
                StatusCode = (lastFailure as CloudflareDnsException)?.StatusCode,
                CloudflareErrorCodes = (lastFailure as CloudflareDnsException)?.CloudflareErrorCodes ?? [],
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task RemoveAllRecordsForFqdnAsync(
        string zoneId,
        string fqdn,
        CancellationToken cancellationToken,
        TimeSpan? operationTimeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(zoneId);
        ArgumentException.ThrowIfNullOrWhiteSpace(fqdn);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var timeout = operationTimeout ?? _options.Value.CloudFlareOperationTimeout;
            using var budget = CloudflareOperationBudget.Begin(timeout, cancellationToken, out var operationToken);

            _logger.LogInformation(
                "Removing all Cloudflare DNS records for exact FQDN {Fqdn} (all record types; " +
                "parent/child hostnames are out of scope)",
                fqdn);

            Exception? lastFailure = null;

            for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                operationToken.ThrowIfCancellationRequested();

                try
                {
                    await RemoveAllAttemptAsync(zoneId, fqdn, attempt, operationToken).ConfigureAwait(false);

                    _logger.LogInformation(
                        "Cloudflare DNS cleanup verified for {Fqdn} on attempt {Attempt}/{MaxAttempts}: " +
                        "no records remain for the exact name (API view). " +
                        "Recursive resolvers may still serve cached answers until TTLs expire",
                        fqdn,
                        attempt,
                        MaxAttempts);
                    return;
                }
                catch (OperationCanceledException) when (operationToken.IsCancellationRequested)
                {
                    _logger.LogWarning(
                        "Cloudflare DNS cleanup for {Fqdn} was canceled on attempt {Attempt}. " +
                        "Remote DNS may still contain records for this FQDN; cleanup is not claimed successful",
                        fqdn,
                        attempt);
                    throw;
                }
                catch (CloudflareDnsException ex) when (ex.IsPermanentFailure)
                {
                    _logger.LogError(
                        ex,
                        "Cloudflare DNS cleanup for {Fqdn} failed permanently on attempt {Attempt} " +
                        "(operation={Operation}, status={StatusCode}). Not retrying. " +
                        "The FQDN is not claimed removed",
                        fqdn,
                        attempt,
                        ex.FailedOperation,
                        ex.StatusCode);
                    throw;
                }
                catch (Exception ex) when (ex is CloudflareDnsException or InvalidOperationException)
                {
                    lastFailure = ex;
                    var uncertain = ex is CloudflareDnsException { IsOutcomeUncertain: true };

                    _logger.LogError(
                        ex,
                        "Cloudflare DNS cleanup attempt {Attempt}/{MaxAttempts} for {Fqdn} failed " +
                        "(uncertainOutcome={Uncertain}, operation={Operation}). " +
                        "Partial deletions are not treated as success; a subsequent attempt will re-read and continue",
                        attempt,
                        MaxAttempts,
                        fqdn,
                        uncertain,
                        (ex as CloudflareDnsException)?.FailedOperation);

                    if (attempt >= MaxAttempts)
                    {
                        break;
                    }

                    var delay = GetAttemptBackoff(attempt);
                    _logger.LogWarning(
                        "Waiting {DelayMs} ms before Cloudflare DNS cleanup retry {NextAttempt}/{MaxAttempts} for {Fqdn}",
                        delay.TotalMilliseconds,
                        attempt + 1,
                        MaxAttempts,
                        fqdn);
                    await DelayRespectingBudgetAsync(delay, operationToken).ConfigureAwait(false);
                }
            }

            throw new CloudflareDnsException(
                $"Cloudflare DNS cleanup for '{fqdn}' failed after {MaxAttempts} attempt(s). " +
                "The FQDN is not claimed removed; remote DNS may still contain records for this exact name. " +
                "Forced process termination may also leave records behind.",
                lastFailure ?? new InvalidOperationException("Unknown cleanup failure."))
            {
                IsOutcomeUncertain = lastFailure is CloudflareDnsException { IsOutcomeUncertain: true },
                IsPermanentFailure = lastFailure is CloudflareDnsException { IsPermanentFailure: true },
                FailedOperation = "RemoveAllRecordsForFqdnAsync",
                StatusCode = (lastFailure as CloudflareDnsException)?.StatusCode,
                CloudflareErrorCodes = (lastFailure as CloudflareDnsException)?.CloudflareErrorCodes ?? [],
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task RemoveAllAttemptAsync(
        string zoneId,
        string fqdn,
        int attempt,
        CancellationToken cancellationToken)
    {
        var existing = await ListExactFqdnRecordsAsync(zoneId, fqdn, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Cloudflare DNS cleanup attempt {Attempt}: {RecordCount} record(s) for exact FQDN {Fqdn}",
            attempt,
            existing.Count,
            fqdn);

        foreach (var record in existing)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(record.Id))
            {
                throw new CloudflareDnsException(
                    $"Cloudflare DNS cleanup for '{fqdn}' cannot delete a record without an id " +
                    $"(type={record.Type}). Failing safely without inventing identifiers")
                {
                    FailedOperation = "Delete",
                    IsPermanentFailure = true,
                };
            }

            _logger.LogInformation(
                "Deleting {Type} record {RecordId} for exact FQDN {Fqdn} during cleanup",
                record.Type,
                record.Id,
                fqdn);

            await _client.DeleteRecordAsync(zoneId, record.Id, cancellationToken).ConfigureAwait(false);
        }

        var remaining = await ListExactFqdnRecordsAsync(zoneId, fqdn, cancellationToken).ConfigureAwait(false);
        if (remaining.Count > 0)
        {
            var summary = string.Join(
                ", ",
                remaining.Select(static r => $"{r.Type}:{r.Id}").Order(StringComparer.Ordinal));

            throw new CloudflareDnsException(
                $"Cloudflare DNS cleanup verification failed for '{fqdn}': " +
                $"{remaining.Count} record(s) still present after delete attempts [{summary}]. " +
                "Cleanup must not be treated as successful.")
            {
                FailedOperation = "VerifyCleanup",
            };
        }
    }

    private async Task<IReadOnlyList<CloudflareDnsRecord>> ListExactFqdnRecordsAsync(
        string zoneId,
        string fqdn,
        CancellationToken cancellationToken)
    {
        var normalizedExpected = NormalizeFqdnName(fqdn);
        var listed = await _client
            .ListAllRecordsForNameAsync(zoneId, fqdn, cancellationToken)
            .ConfigureAwait(false);

        var exact = new List<CloudflareDnsRecord>(listed.Count);
        foreach (var record in listed)
        {
            if (!TryClassifyExactFqdn(record.Name, normalizedExpected, out var isExact))
            {
                throw new CloudflareDnsException(
                    $"Cloudflare DNS returned an ambiguous record name for cleanup of '{fqdn}' " +
                    $"(record id={record.Id}, type={record.Type}). " +
                    "Failing safely without deleting records outside the exact-FQDN ownership boundary.")
                {
                    FailedOperation = "ListAllRecordsForName",
                    IsPermanentFailure = true,
                };
            }

            if (isExact)
            {
                exact.Add(record);
            }
            else
            {
                // API name filters can be imperfect; never delete non-exact names.
                _logger.LogWarning(
                    "Skipping Cloudflare DNS record {RecordId} (type={Type}) during cleanup of {Fqdn}: " +
                    "name is not an exact match after normalization",
                    record.Id,
                    record.Type,
                    fqdn);
            }
        }

        return exact;
    }

    /// <summary>
    /// Classifies whether a Cloudflare record name is exactly the expected FQDN.
    /// Returns <see langword="false"/> when the name cannot be classified safely.
    /// </summary>
    public static bool TryClassifyExactFqdn(string? recordName, string normalizedExpected, out bool isExactMatch)
    {
        isExactMatch = false;
        if (string.IsNullOrWhiteSpace(recordName) || string.IsNullOrWhiteSpace(normalizedExpected))
        {
            return false;
        }

        var normalized = NormalizeFqdnName(recordName);
        if (normalized.Length == 0)
        {
            return false;
        }

        // Empty labels (e.g. "a..b.example") are treated as ambiguous rather than deletable.
        if (normalized.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        isExactMatch = string.Equals(normalized, normalizedExpected, StringComparison.Ordinal);
        return true;
    }

    private static string NormalizeFqdnName(string fqdn) =>
        fqdn.Trim().TrimEnd('.').ToLowerInvariant();

    private async Task ReconcileAttemptAsync(
        string zoneId,
        string fqdn,
        HashSet<string> desiredV4,
        HashSet<string> desiredV6,
        int attempt,
        CancellationToken cancellationToken)
    {
        // Stage 1: read current state for both families.
        var existingA = await _client
            .ListRecordsAsync(zoneId, fqdn, CloudflareDnsRecordTypes.A, cancellationToken)
            .ConfigureAwait(false);
        var existingAaaa = await _client
            .ListRecordsAsync(zoneId, fqdn, CloudflareDnsRecordTypes.AAAA, cancellationToken)
            .ConfigureAwait(false);

        var planA = BuildPlan(existingA, CloudflareDnsRecordTypes.A, desiredV4);
        var planAaaa = BuildPlan(existingAaaa, CloudflareDnsRecordTypes.AAAA, desiredV6);

        _logger.LogInformation(
            "Cloudflare DNS attempt {Attempt}: A missing={AMissing} stale={AStale} dup={ADup}; " +
            "AAAA missing={AaaaMissing} stale={AaaaStale} dup={AaaaDup}",
            attempt,
            planA.Missing.Count,
            planA.Stale.Count,
            planA.DuplicateExtras.Count,
            planAaaa.Missing.Count,
            planAaaa.Stale.Count,
            planAaaa.DuplicateExtras.Count);

        // Stage 2: create all missing desired records across both families before any deletes.
        await CreateMissingAsync(zoneId, fqdn, CloudflareDnsRecordTypes.A, planA.Missing, cancellationToken)
            .ConfigureAwait(false);
        await CreateMissingAsync(zoneId, fqdn, CloudflareDnsRecordTypes.AAAA, planAaaa.Missing, cancellationToken)
            .ConfigureAwait(false);

        // Stage 3: ensure kept desired records match managed TTL and DNS-only proxy state.
        await EnsureManagedAttributesAsync(zoneId, fqdn, CloudflareDnsRecordTypes.A, planA.KeptDesired, cancellationToken)
            .ConfigureAwait(false);
        await EnsureManagedAttributesAsync(zoneId, fqdn, CloudflareDnsRecordTypes.AAAA, planAaaa.KeptDesired, cancellationToken)
            .ConfigureAwait(false);

        // Stage 4: delete stale, duplicates, and unparseable — only after desired creates completed.
        await DeleteRecordsAsync(zoneId, fqdn, CloudflareDnsRecordTypes.A, planA.RecordsToDelete, cancellationToken)
            .ConfigureAwait(false);
        await DeleteRecordsAsync(zoneId, fqdn, CloudflareDnsRecordTypes.AAAA, planAaaa.RecordsToDelete, cancellationToken)
            .ConfigureAwait(false);

        // Stage 5: re-read and verify exact final sets.
        await VerifyAsync(zoneId, fqdn, desiredV4, desiredV6, cancellationToken).ConfigureAwait(false);
    }

    private async Task CreateMissingAsync(
        string zoneId,
        string fqdn,
        string type,
        IReadOnlyList<string> missingContents,
        CancellationToken cancellationToken)
    {
        foreach (var content in missingContents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _logger.LogInformation(
                "Creating {Type} record for {Fqdn} content {Content}",
                type,
                fqdn,
                content);
            await _client.CreateRecordAsync(
                    zoneId,
                    new CloudflareDnsRecordWriteRequest
                    {
                        Type = type,
                        Name = fqdn,
                        Content = content,
                        Ttl = CloudflareManagedDnsPolicy.ManagedTtl,
                        Proxied = CloudflareManagedDnsPolicy.ManagedProxied,
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task EnsureManagedAttributesAsync(
        string zoneId,
        string fqdn,
        string type,
        IReadOnlyList<CloudflareDnsRecord> keptDesired,
        CancellationToken cancellationToken)
    {
        foreach (var record in keptDesired)
        {
            if (CloudflareManagedDnsPolicy.MatchesManagedAttributes(record))
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var content = NormalizeContent(record.Content, type)
                ?? throw new CloudflareDnsException(
                    $"Cannot update {type} record {record.Id}: content is not a valid IP.")
                {
                    FailedOperation = "Update",
                    IsPermanentFailure = true,
                };

            _logger.LogInformation(
                "Updating {Type} record {RecordId} for {Fqdn} to managed TTL={ManagedTtl} and proxied={Proxied}",
                type,
                record.Id,
                fqdn,
                CloudflareManagedDnsPolicy.ManagedTtl,
                CloudflareManagedDnsPolicy.ManagedProxied);
            await _client.UpdateRecordAsync(
                    zoneId,
                    record.Id,
                    new CloudflareDnsRecordWriteRequest
                    {
                        Type = type,
                        Name = fqdn,
                        Content = content,
                        Ttl = CloudflareManagedDnsPolicy.ManagedTtl,
                        Proxied = CloudflareManagedDnsPolicy.ManagedProxied,
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task DeleteRecordsAsync(
        string zoneId,
        string fqdn,
        string type,
        IReadOnlyList<CloudflareDnsRecord> records,
        CancellationToken cancellationToken)
    {
        foreach (var record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _logger.LogInformation(
                "Deleting {Type} record {RecordId} for {Fqdn} content {Content}",
                type,
                record.Id,
                fqdn,
                record.Content);
            await _client.DeleteRecordAsync(zoneId, record.Id, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task VerifyAsync(
        string zoneId,
        string fqdn,
        HashSet<string> desiredV4,
        HashSet<string> desiredV6,
        CancellationToken cancellationToken)
    {
        var actualA = await _client
            .ListRecordsAsync(zoneId, fqdn, CloudflareDnsRecordTypes.A, cancellationToken)
            .ConfigureAwait(false);
        var actualAaaa = await _client
            .ListRecordsAsync(zoneId, fqdn, CloudflareDnsRecordTypes.AAAA, cancellationToken)
            .ConfigureAwait(false);

        AssertExactSet(fqdn, CloudflareDnsRecordTypes.A, desiredV4, actualA);
        AssertExactSet(fqdn, CloudflareDnsRecordTypes.AAAA, desiredV6, actualAaaa);
    }

    private static void AssertExactSet(
        string fqdn,
        string type,
        HashSet<string> desired,
        IReadOnlyList<CloudflareDnsRecord> actual)
    {
        var actualContents = ToObservedContentSet(actual, type);

        if (!desired.SetEquals(actualContents))
        {
            throw new CloudflareDnsException(
                $"Cloudflare DNS verification failed for '{fqdn}' ({type}): " +
                $"desired=[{string.Join(", ", desired.Order(StringComparer.Ordinal))}] " +
                $"actual=[{string.Join(", ", actualContents.Order(StringComparer.Ordinal))}]. " +
                "Reconciliation must not be treated as successful. " +
                "External modification or incomplete mutation may have occurred.")
            {
                FailedOperation = "Verify",
            };
        }

        if (actual.Count != desired.Count)
        {
            throw new CloudflareDnsException(
                $"Cloudflare DNS verification failed for '{fqdn}' ({type}): " +
                $"duplicate or extra records remain (count {actual.Count} vs desired {desired.Count}). " +
                "Reconciliation must not be treated as successful.")
            {
                FailedOperation = "Verify",
            };
        }

        foreach (var record in actual)
        {
            if (record.Proxied != CloudflareManagedDnsPolicy.ManagedProxied)
            {
                throw new CloudflareDnsException(
                    $"Cloudflare DNS verification failed for '{fqdn}' ({type}): " +
                    $"record {record.Id} proxy state is not DNS-only (proxied=false). " +
                    "Reconciliation must not be treated as successful.")
                {
                    FailedOperation = "Verify",
                };
            }

            if (record.Ttl != CloudflareManagedDnsPolicy.ManagedTtl)
            {
                throw new CloudflareDnsException(
                    $"Cloudflare DNS verification failed for '{fqdn}' ({type}): " +
                    $"record {record.Id} TTL is not {CloudflareManagedDnsPolicy.ManagedTtl}. " +
                    "Reconciliation must not be treated as successful.")
                {
                    FailedOperation = "Verify",
                };
            }

            if (NormalizeContent(record.Content, type) is null)
            {
                throw new CloudflareDnsException(
                    $"Cloudflare DNS verification failed for '{fqdn}' ({type}): " +
                    $"record {record.Id} has unparseable content. " +
                    "Reconciliation must not be treated as successful.")
                {
                    FailedOperation = "Verify",
                };
            }
        }
    }

    private static FamilyPlan BuildPlan(
        IReadOnlyList<CloudflareDnsRecord> existing,
        string type,
        HashSet<string> desiredContents)
    {
        var byContent = new Dictionary<string, List<CloudflareDnsRecord>>(StringComparer.OrdinalIgnoreCase);
        var unparseable = new List<CloudflareDnsRecord>();

        foreach (var record in existing)
        {
            var contentKey = NormalizeContent(record.Content, type);
            if (contentKey is null)
            {
                unparseable.Add(record);
                continue;
            }

            if (!byContent.TryGetValue(contentKey, out var list))
            {
                list = [];
                byContent[contentKey] = list;
            }

            list.Add(record);
        }

        var missing = new List<string>();
        var keptDesired = new List<CloudflareDnsRecord>();
        var duplicateExtras = new List<CloudflareDnsRecord>();
        var stale = new List<CloudflareDnsRecord>();

        foreach (var content in desiredContents)
        {
            if (!byContent.TryGetValue(content, out var records) || records.Count == 0)
            {
                missing.Add(content);
                continue;
            }

            keptDesired.Add(records[0]);
            for (var i = 1; i < records.Count; i++)
            {
                duplicateExtras.Add(records[i]);
            }
        }

        foreach (var (content, records) in byContent)
        {
            if (desiredContents.Contains(content))
            {
                continue;
            }

            stale.AddRange(records);
        }

        var toDelete = new List<CloudflareDnsRecord>();
        toDelete.AddRange(stale);
        toDelete.AddRange(duplicateExtras);
        toDelete.AddRange(unparseable);

        return new FamilyPlan(missing, keptDesired, stale, duplicateExtras, toDelete);
    }

    private async Task DelayRespectingBudgetAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CloudflareOperationBudget.Current?.ThrowIfExpired(cancellationToken);

        if (delay <= TimeSpan.Zero)
        {
            return;
        }

        if (CloudflareOperationBudget.Current is { } budget && delay > budget.Remaining)
        {
            throw new OperationCanceledException(
                "Cloudflare DNS reconciler backoff exceeds the remaining operation budget.",
                cancellationToken);
        }

        await DelayAsync(delay, cancellationToken).ConfigureAwait(false);
    }

    private static TimeSpan GetAttemptBackoff(int failedAttempt)
    {
        // 200ms, 400ms between reconciler attempts (HTTP 429 has its own Retry-After policy).
        var ms = 200 * Math.Pow(2, failedAttempt - 1);
        return TimeSpan.FromMilliseconds(Math.Clamp(ms, 200, 2000));
    }

    private static HashSet<string> ToContentSet(IReadOnlyList<IPAddress> addresses)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var address in addresses)
        {
            set.Add(IpAddressEligibility.ToDnsContent(address));
        }

        return set;
    }

    private static HashSet<string> ToObservedContentSet(IReadOnlyList<CloudflareDnsRecord> records, string type)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in records)
        {
            var content = NormalizeContent(record.Content, type);
            if (content is not null)
            {
                set.Add(content);
            }
        }

        return set;
    }

    private static string? NormalizeContent(string? content, string type)
    {
        if (!IpAddressEligibility.TryParseDnsContent(content, out var address))
        {
            return null;
        }

        if (type == CloudflareDnsRecordTypes.A && address.AddressFamily != AddressFamily.InterNetwork)
        {
            return null;
        }

        if (type == CloudflareDnsRecordTypes.AAAA && address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return null;
        }

        return IpAddressEligibility.ToDnsContent(address);
    }

    private sealed record FamilyPlan(
        IReadOnlyList<string> Missing,
        IReadOnlyList<CloudflareDnsRecord> KeptDesired,
        IReadOnlyList<CloudflareDnsRecord> Stale,
        IReadOnlyList<CloudflareDnsRecord> DuplicateExtras,
        IReadOnlyList<CloudflareDnsRecord> RecordsToDelete);
}
