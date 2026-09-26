namespace VectorNNTP.NNTPD.Cloudflare;

/// <summary>Source-generated Cloudflare DNS client, reconciler, and reconciliation-service log messages.</summary>
internal static partial class CloudflareLogMessages
{
    [LoggerMessage(
        EventId = 1800,
        Level = LogLevel.Warning,
        Message = "Cloudflare DNS {Method} {Path} canceled during a mutation; remote outcome is uncertain")]
    public static partial void MutationCanceledUncertain(ILogger logger, string Method, string Path);

    [LoggerMessage(
        EventId = 1801,
        Level = LogLevel.Warning,
        Message = "Cloudflare DNS rate limited (HTTP 429) for {Method} {Path}. Retrying after {DelayMs} ms (attempt {Attempt}/{MaxAttempts})")]
    public static partial void RateLimited(
        ILogger logger,
        string Method,
        string Path,
        double DelayMs,
        int Attempt,
        int MaxAttempts);

    [LoggerMessage(
        EventId = 1802,
        Level = LogLevel.Information,
        Message = "Reconciling Cloudflare DNS for {Fqdn}: desired A={ACount}, AAAA={AaaaCount} (staged create-all-then-delete; managed TTL={ManagedTtl}, proxied=false; non-atomic; success requires verified exact match)")]
    public static partial void Reconciling(
        ILogger logger,
        string Fqdn,
        int ACount,
        int AaaaCount,
        int ManagedTtl);

    [LoggerMessage(
        EventId = 1803,
        Level = LogLevel.Information,
        Message = "Cloudflare DNS reconciliation verified for {Fqdn} on attempt {Attempt}/{MaxAttempts} (A={ACount}, AAAA={AaaaCount})")]
    public static partial void ReconciliationVerified(
        ILogger logger,
        string Fqdn,
        int Attempt,
        int MaxAttempts,
        int ACount,
        int AaaaCount);

    [LoggerMessage(
        EventId = 1804,
        Level = LogLevel.Warning,
        Message = "Cloudflare DNS reconciliation for {Fqdn} was canceled on attempt {Attempt}. Remote DNS may be in an intermediate state; the next startup will re-read and converge")]
    public static partial void ReconciliationCanceled(ILogger logger, string Fqdn, int Attempt);

    [LoggerMessage(
        EventId = 1805,
        Level = LogLevel.Error,
        Message = "Cloudflare DNS reconciliation for {Fqdn} failed permanently on attempt {Attempt} (operation={Operation}, status={StatusCode}). Not retrying")]
    public static partial void ReconciliationPermanentFailure(
        ILogger logger,
        Exception exception,
        string Fqdn,
        int Attempt,
        string? Operation,
        int? StatusCode);

    [LoggerMessage(
        EventId = 1806,
        Level = LogLevel.Error,
        Message = "Cloudflare DNS reconciliation attempt {Attempt}/{MaxAttempts} for {Fqdn} failed (uncertainOutcome={Uncertain}, operation={Operation}). Partial A/AAAA mutations are not rolled back atomically; a subsequent attempt will re-read Cloudflare and converge toward the desired set")]
    public static partial void ReconciliationAttemptFailed(
        ILogger logger,
        Exception exception,
        int Attempt,
        int MaxAttempts,
        string Fqdn,
        bool Uncertain,
        string? Operation);

    [LoggerMessage(
        EventId = 1807,
        Level = LogLevel.Warning,
        Message = "Waiting {DelayMs} ms before Cloudflare DNS reconcile retry {NextAttempt}/{MaxAttempts} for {Fqdn}")]
    public static partial void ReconciliationRetryWait(
        ILogger logger,
        double DelayMs,
        int NextAttempt,
        int MaxAttempts,
        string Fqdn);

    [LoggerMessage(
        EventId = 1808,
        Level = LogLevel.Information,
        Message = "Removing all Cloudflare DNS records for exact FQDN {Fqdn} (all record types; parent/child hostnames are out of scope)")]
    public static partial void RemovingAllRecords(ILogger logger, string Fqdn);

    [LoggerMessage(
        EventId = 1809,
        Level = LogLevel.Information,
        Message = "Cloudflare DNS cleanup verified for {Fqdn} on attempt {Attempt}/{MaxAttempts}: no records remain for the exact name (API view). Recursive resolvers may still serve cached answers until TTLs expire")]
    public static partial void CleanupVerified(ILogger logger, string Fqdn, int Attempt, int MaxAttempts);

    [LoggerMessage(
        EventId = 1810,
        Level = LogLevel.Warning,
        Message = "Cloudflare DNS cleanup for {Fqdn} was canceled on attempt {Attempt}. Remote DNS may still contain records for this FQDN; cleanup is not claimed successful")]
    public static partial void CleanupCanceled(ILogger logger, string Fqdn, int Attempt);

    [LoggerMessage(
        EventId = 1811,
        Level = LogLevel.Error,
        Message = "Cloudflare DNS cleanup for {Fqdn} failed permanently on attempt {Attempt} (operation={Operation}, status={StatusCode}). Not retrying. The FQDN is not claimed removed")]
    public static partial void CleanupPermanentFailure(
        ILogger logger,
        Exception exception,
        string Fqdn,
        int Attempt,
        string? Operation,
        int? StatusCode);

    [LoggerMessage(
        EventId = 1812,
        Level = LogLevel.Error,
        Message = "Cloudflare DNS cleanup attempt {Attempt}/{MaxAttempts} for {Fqdn} failed (uncertainOutcome={Uncertain}, operation={Operation}). Partial deletions are not treated as success; a subsequent attempt will re-read and continue")]
    public static partial void CleanupAttemptFailed(
        ILogger logger,
        Exception exception,
        int Attempt,
        int MaxAttempts,
        string Fqdn,
        bool Uncertain,
        string? Operation);

    [LoggerMessage(
        EventId = 1813,
        Level = LogLevel.Warning,
        Message = "Waiting {DelayMs} ms before Cloudflare DNS cleanup retry {NextAttempt}/{MaxAttempts} for {Fqdn}")]
    public static partial void CleanupRetryWait(
        ILogger logger,
        double DelayMs,
        int NextAttempt,
        int MaxAttempts,
        string Fqdn);

    [LoggerMessage(
        EventId = 1814,
        Level = LogLevel.Information,
        Message = "Cloudflare DNS cleanup attempt {Attempt}: {RecordCount} record(s) for exact FQDN {Fqdn}")]
    public static partial void CleanupAttempt(ILogger logger, int Attempt, int RecordCount, string Fqdn);

    [LoggerMessage(
        EventId = 1815,
        Level = LogLevel.Information,
        Message = "Deleting {Type} record {RecordId} for exact FQDN {Fqdn} during cleanup")]
    public static partial void DeletingRecordDuringCleanup(ILogger logger, string Type, string RecordId, string Fqdn);

    [LoggerMessage(
        EventId = 1816,
        Level = LogLevel.Warning,
        Message = "Skipping Cloudflare DNS record {RecordId} (type={Type}) during cleanup of {Fqdn}: name is not an exact match after normalization")]
    public static partial void SkippingNonExactCleanupRecord(
        ILogger logger,
        string RecordId,
        string Type,
        string Fqdn);

    [LoggerMessage(
        EventId = 1817,
        Level = LogLevel.Information,
        Message = "Cloudflare DNS attempt {Attempt}: A missing={AMissing} stale={AStale} dup={ADup}; AAAA missing={AaaaMissing} stale={AaaaStale} dup={AaaaDup}")]
    public static partial void ReconcileAttemptPlan(
        ILogger logger,
        int Attempt,
        int AMissing,
        int AStale,
        int ADup,
        int AaaaMissing,
        int AaaaStale,
        int AaaaDup);

    [LoggerMessage(
        EventId = 1818,
        Level = LogLevel.Information,
        Message = "Creating {Type} record for {Fqdn} content {Content}")]
    public static partial void CreatingRecord(ILogger logger, string Type, string Fqdn, string Content);

    [LoggerMessage(
        EventId = 1819,
        Level = LogLevel.Information,
        Message = "Updating {Type} record {RecordId} for {Fqdn} to managed TTL={ManagedTtl} and proxied={Proxied}")]
    public static partial void UpdatingRecordManagedAttributes(
        ILogger logger,
        string Type,
        string RecordId,
        string Fqdn,
        int ManagedTtl,
        bool Proxied);

    [LoggerMessage(
        EventId = 1820,
        Level = LogLevel.Information,
        Message = "Deleting {Type} record {RecordId} for {Fqdn} content {Content}")]
    public static partial void DeletingRecord(
        ILogger logger,
        string Type,
        string RecordId,
        string Fqdn,
        string Content);

    [LoggerMessage(
        EventId = 1821,
        Level = LogLevel.Information,
        Message = "Starting Cloudflare DNS reconciliation for {Fqdn} in zone {ZoneId}")]
    public static partial void StartingReconciliation(ILogger logger, string Fqdn, string ZoneId);

    [LoggerMessage(
        EventId = 1822,
        Level = LogLevel.Information,
        Message = "Cloudflare DNS reconciliation completed for {Fqdn} with {AddressCount} address(es)")]
    public static partial void ReconciliationCompleted(ILogger logger, string Fqdn, int AddressCount);

    [LoggerMessage(
        EventId = 1823,
        Level = LogLevel.Information,
        Message = "Cloudflare DNS cleanup skipped: FQDN ownership was not active (reconcile never completed successfully in this process)")]
    public static partial void CleanupSkippedOwnershipInactive(ILogger logger);

    [LoggerMessage(
        EventId = 1824,
        Level = LogLevel.Information,
        Message = "Stopping Cloudflare DNS reconciliation: removing all records for exact FQDN {Fqdn}")]
    public static partial void StoppingReconciliation(ILogger logger, string Fqdn);

    [LoggerMessage(
        EventId = 1825,
        Level = LogLevel.Information,
        Message = "Cloudflare DNS cleanup completed for {Fqdn}: Cloudflare API reports no remaining records for the exact name. Recursive DNS caches may still return prior answers until TTLs expire")]
    public static partial void CleanupCompleted(ILogger logger, string Fqdn);

    [LoggerMessage(
        EventId = 1826,
        Level = LogLevel.Warning,
        Message = "Cloudflare DNS reconciliation did not complete successfully for {Fqdn}. Attempting authoritative cleanup of the exact FQDN before failing startup (cancellation={Canceled})")]
    public static partial void PostFailureCleanupAttempt(
        ILogger logger,
        Exception? exception,
        string Fqdn,
        bool Canceled);

    [LoggerMessage(
        EventId = 1827,
        Level = LogLevel.Information,
        Message = "Post-failure Cloudflare DNS cleanup verified for {Fqdn}: no exact-name records remain")]
    public static partial void PostFailureCleanupVerified(ILogger logger, string Fqdn);

    [LoggerMessage(
        EventId = 1828,
        Level = LogLevel.Error,
        Message = "Post-failure Cloudflare DNS cleanup for {Fqdn} timed out or was canceled (budget={CleanupBudget}). Records may remain; cleanup is not claimed successful. The next successful startup will re-read and reconcile")]
    public static partial void PostFailureCleanupTimedOut(
        ILogger logger,
        Exception exception,
        string Fqdn,
        TimeSpan CleanupBudget);

    [LoggerMessage(
        EventId = 1829,
        Level = LogLevel.Error,
        Message = "Post-failure Cloudflare DNS cleanup for {Fqdn} did not verify removal. Records may remain; the next successful startup will re-read and reconcile. Cleanup is not claimed successful")]
    public static partial void PostFailureCleanupFailed(ILogger logger, Exception exception, string Fqdn);
}
