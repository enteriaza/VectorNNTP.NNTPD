namespace VectorNNTP.Common.Cloudflare
{
    /// <summary>Source-generated Cloudflare DNS client, reconciler, and reconciliation-service log messages.</summary>
    internal static partial class CloudflareLogMessages
    {
        /// <summary>
        /// Emitted when a mutating Cloudflare request is canceled by the caller or the operation budget
        /// after the request may already have been sent. The cancellation is still propagated.
        /// </summary>
        /// <param name="logger">DNS client logger.</param>
        /// <param name="Method">HTTP method of the mutation.</param>
        /// <param name="Path">Relative Cloudflare API path of the mutation.</param>
        [LoggerMessage(
            EventId = 1800,
            Level = LogLevel.Warning,
            Message = "Cloudflare DNS {Method} {Path} canceled during a mutation; remote outcome is uncertain")]
        internal static partial void MutationCanceledUncertain(ILogger logger, string Method, string Path);

        /// <summary>
        /// Emitted when Cloudflare returns HTTP 429 and another attempt will be made after a delay.
        /// Not emitted on the final attempt that exhausts the retry limit.
        /// </summary>
        /// <param name="logger">DNS client logger.</param>
        /// <param name="Method">HTTP method that was rate limited.</param>
        /// <param name="Path">Relative Cloudflare API path that was rate limited.</param>
        /// <param name="DelayMs">Wait before the next attempt, in milliseconds, after Retry-After capping.</param>
        /// <param name="Attempt">One-based count of rate-limit waits so far.</param>
        /// <param name="MaxAttempts">Maximum number of rate-limit waits, not the total number of HTTP sends.</param>
        [LoggerMessage(
            EventId = 1801,
            Level = LogLevel.Warning,
            Message = "Cloudflare DNS rate limited (HTTP 429) for {Method} {Path}. Retrying after {DelayMs} ms (attempt {Attempt}/{MaxAttempts})")]
        internal static partial void RateLimited(
            ILogger logger,
            string Method,
            string Path,
            double DelayMs,
            int Attempt,
            int MaxAttempts);

        /// <summary>
        /// Emitted once per reconcile call after the desired A and AAAA content sets are built and before attempts begin.
        /// </summary>
        /// <param name="logger">Reconciler logger.</param>
        /// <param name="Fqdn">Exact FQDN being reconciled.</param>
        /// <param name="ACount">Desired IPv4 contents.</param>
        /// <param name="AaaaCount">Desired IPv6 contents.</param>
        /// <param name="ManagedTtl">TTL in seconds that managed A/AAAA records must have.</param>
        [LoggerMessage(
            EventId = 1802,
            Level = LogLevel.Information,
            Message = "Reconciling Cloudflare DNS for {Fqdn}: desired A={ACount}, AAAA={AaaaCount} (staged create-all-then-delete; managed TTL={ManagedTtl}, proxied=false; non-atomic; success requires verified exact match)")]
        internal static partial void Reconciling(
            ILogger logger,
            string Fqdn,
            int ACount,
            int AaaaCount,
            int ManagedTtl);

        /// <summary>Emitted when a reconcile attempt's post-mutation re-read matches the desired A and AAAA sets.</summary>
        /// <param name="logger">Reconciler logger.</param>
        /// <param name="Fqdn">Exact FQDN that was verified.</param>
        /// <param name="Attempt">One-based attempt that verified.</param>
        /// <param name="MaxAttempts">Maximum attempts for this reconcile call.</param>
        /// <param name="ACount">Verified IPv4 content count.</param>
        /// <param name="AaaaCount">Verified IPv6 content count.</param>
        [LoggerMessage(
            EventId = 1803,
            Level = LogLevel.Information,
            Message = "Cloudflare DNS reconciliation verified for {Fqdn} on attempt {Attempt}/{MaxAttempts} (A={ACount}, AAAA={AaaaCount})")]
        internal static partial void ReconciliationVerified(
            ILogger logger,
            string Fqdn,
            int Attempt,
            int MaxAttempts,
            int ACount,
            int AaaaCount);

        /// <summary>
        /// Emitted when the operation token cancels during a reconcile attempt.
        /// The cancellation is rethrown and success is not claimed.
        /// </summary>
        /// <param name="logger">Reconciler logger.</param>
        /// <param name="Fqdn">Exact FQDN whose reconcile was canceled.</param>
        /// <param name="Attempt">One-based attempt that was canceled.</param>
        [LoggerMessage(
            EventId = 1804,
            Level = LogLevel.Warning,
            Message = "Cloudflare DNS reconciliation for {Fqdn} was canceled on attempt {Attempt}. Remote DNS may be in an intermediate state; the next startup will re-read and converge")]
        internal static partial void ReconciliationCanceled(ILogger logger, string Fqdn, int Attempt);

        /// <summary>
        /// Emitted when a reconcile attempt throws <see cref="CloudflareDnsException"/> with permanent failure.
        /// The exception is rethrown and no further attempt is made.
        /// </summary>
        /// <param name="logger">Reconciler logger.</param>
        /// <param name="exception">The permanent API or data failure.</param>
        /// <param name="Fqdn">Exact FQDN being reconciled.</param>
        /// <param name="Attempt">One-based attempt that failed permanently.</param>
        /// <param name="Operation">Failed operation name from the exception, when set.</param>
        /// <param name="StatusCode">HTTP status from the exception, when set.</param>
        [LoggerMessage(
            EventId = 1805,
            Level = LogLevel.Error,
            Message = "Cloudflare DNS reconciliation for {Fqdn} failed permanently on attempt {Attempt} (operation={Operation}, status={StatusCode}). Not retrying")]
        internal static partial void ReconciliationPermanentFailure(
            ILogger logger,
            Exception exception,
            string Fqdn,
            int Attempt,
            string? Operation,
            int? StatusCode);

        /// <summary>
        /// Emitted when a reconcile attempt throws a non-permanent <see cref="CloudflareDnsException"/>
        /// or <see cref="InvalidOperationException"/>. A later attempt may re-read and continue until the attempt limit.
        /// </summary>
        /// <param name="logger">Reconciler logger.</param>
        /// <param name="exception">The attempt failure.</param>
        /// <param name="Attempt">One-based attempt that failed.</param>
        /// <param name="MaxAttempts">Maximum attempts for this reconcile call.</param>
        /// <param name="Fqdn">Exact FQDN being reconciled.</param>
        /// <param name="Uncertain"><see langword="true"/> when the failure is a mutation whose remote outcome is uncertain.</param>
        /// <param name="Operation">Failed operation name when <paramref name="exception"/> is a <see cref="CloudflareDnsException"/>.</param>
        [LoggerMessage(
            EventId = 1806,
            Level = LogLevel.Error,
            Message = "Cloudflare DNS reconciliation attempt {Attempt}/{MaxAttempts} for {Fqdn} failed (uncertainOutcome={Uncertain}, operation={Operation}). Partial A/AAAA mutations are not rolled back atomically; a subsequent attempt will re-read Cloudflare and converge toward the desired set")]
        internal static partial void ReconciliationAttemptFailed(
            ILogger logger,
            Exception exception,
            int Attempt,
            int MaxAttempts,
            string Fqdn,
            bool Uncertain,
            string? Operation);

        /// <summary>Emitted before the bounded backoff between reconcile attempts, when another attempt remains.</summary>
        /// <param name="logger">Reconciler logger.</param>
        /// <param name="DelayMs">Backoff delay in milliseconds.</param>
        /// <param name="NextAttempt">One-based number of the attempt that will follow the wait.</param>
        /// <param name="MaxAttempts">Maximum attempts for this reconcile call.</param>
        /// <param name="Fqdn">Exact FQDN being reconciled.</param>
        [LoggerMessage(
            EventId = 1807,
            Level = LogLevel.Warning,
            Message = "Waiting {DelayMs} ms before Cloudflare DNS reconcile retry {NextAttempt}/{MaxAttempts} for {Fqdn}")]
        internal static partial void ReconciliationRetryWait(
            ILogger logger,
            double DelayMs,
            int NextAttempt,
            int MaxAttempts,
            string Fqdn);

        /// <summary>Emitted once per exact-FQDN cleanup call, before delete attempts begin.</summary>
        /// <param name="logger">Reconciler logger.</param>
        /// <param name="Fqdn">Exact FQDN whose records will be removed.</param>
        [LoggerMessage(
            EventId = 1808,
            Level = LogLevel.Information,
            Message = "Removing all Cloudflare DNS records for exact FQDN {Fqdn} (all record types; parent/child hostnames are out of scope)")]
        internal static partial void RemovingAllRecords(ILogger logger, string Fqdn);

        /// <summary>Emitted when a cleanup attempt re-reads the exact name and finds no remaining records.</summary>
        /// <param name="logger">Reconciler logger.</param>
        /// <param name="Fqdn">Exact FQDN that was verified empty in the Cloudflare API view.</param>
        /// <param name="Attempt">One-based attempt that verified.</param>
        /// <param name="MaxAttempts">Maximum attempts for this cleanup call.</param>
        [LoggerMessage(
            EventId = 1809,
            Level = LogLevel.Information,
            Message = "Cloudflare DNS cleanup verified for {Fqdn} on attempt {Attempt}/{MaxAttempts}: no records remain for the exact name (API view). Recursive resolvers may still serve cached answers until TTLs expire")]
        internal static partial void CleanupVerified(ILogger logger, string Fqdn, int Attempt, int MaxAttempts);

        /// <summary>
        /// Emitted when the operation token cancels during a cleanup attempt.
        /// The cancellation is rethrown and removal is not claimed.
        /// </summary>
        /// <param name="logger">Reconciler logger.</param>
        /// <param name="Fqdn">Exact FQDN whose cleanup was canceled.</param>
        /// <param name="Attempt">One-based attempt that was canceled.</param>
        [LoggerMessage(
            EventId = 1810,
            Level = LogLevel.Warning,
            Message = "Cloudflare DNS cleanup for {Fqdn} was canceled on attempt {Attempt}. Remote DNS may still contain records for this FQDN; cleanup is not claimed successful")]
        internal static partial void CleanupCanceled(ILogger logger, string Fqdn, int Attempt);

        /// <summary>
        /// Emitted when a cleanup attempt throws <see cref="CloudflareDnsException"/> with permanent failure.
        /// The exception is rethrown and no further attempt is made.
        /// </summary>
        /// <param name="logger">Reconciler logger.</param>
        /// <param name="exception">The permanent API or data failure.</param>
        /// <param name="Fqdn">Exact FQDN being cleaned up.</param>
        /// <param name="Attempt">One-based attempt that failed permanently.</param>
        /// <param name="Operation">Failed operation name from the exception, when set.</param>
        /// <param name="StatusCode">HTTP status from the exception, when set.</param>
        [LoggerMessage(
            EventId = 1811,
            Level = LogLevel.Error,
            Message = "Cloudflare DNS cleanup for {Fqdn} failed permanently on attempt {Attempt} (operation={Operation}, status={StatusCode}). Not retrying. The FQDN is not claimed removed")]
        internal static partial void CleanupPermanentFailure(
            ILogger logger,
            Exception exception,
            string Fqdn,
            int Attempt,
            string? Operation,
            int? StatusCode);

        /// <summary>
        /// Emitted when a cleanup attempt throws a non-permanent <see cref="CloudflareDnsException"/>
        /// or <see cref="InvalidOperationException"/>. A later attempt may re-read and continue until the attempt limit.
        /// </summary>
        /// <param name="logger">Reconciler logger.</param>
        /// <param name="exception">The attempt failure.</param>
        /// <param name="Attempt">One-based attempt that failed.</param>
        /// <param name="MaxAttempts">Maximum attempts for this cleanup call.</param>
        /// <param name="Fqdn">Exact FQDN being cleaned up.</param>
        /// <param name="Uncertain"><see langword="true"/> when the failure is a mutation whose remote outcome is uncertain.</param>
        /// <param name="Operation">Failed operation name when <paramref name="exception"/> is a <see cref="CloudflareDnsException"/>.</param>
        [LoggerMessage(
            EventId = 1812,
            Level = LogLevel.Error,
            Message = "Cloudflare DNS cleanup attempt {Attempt}/{MaxAttempts} for {Fqdn} failed (uncertainOutcome={Uncertain}, operation={Operation}). Partial deletions are not treated as success; a subsequent attempt will re-read and continue")]
        internal static partial void CleanupAttemptFailed(
            ILogger logger,
            Exception exception,
            int Attempt,
            int MaxAttempts,
            string Fqdn,
            bool Uncertain,
            string? Operation);

        /// <summary>Emitted before the bounded backoff between cleanup attempts, when another attempt remains.</summary>
        /// <param name="logger">Reconciler logger.</param>
        /// <param name="DelayMs">Backoff delay in milliseconds.</param>
        /// <param name="NextAttempt">One-based number of the attempt that will follow the wait.</param>
        /// <param name="MaxAttempts">Maximum attempts for this cleanup call.</param>
        /// <param name="Fqdn">Exact FQDN being cleaned up.</param>
        [LoggerMessage(
            EventId = 1813,
            Level = LogLevel.Warning,
            Message = "Waiting {DelayMs} ms before Cloudflare DNS cleanup retry {NextAttempt}/{MaxAttempts} for {Fqdn}")]
        internal static partial void CleanupRetryWait(
            ILogger logger,
            double DelayMs,
            int NextAttempt,
            int MaxAttempts,
            string Fqdn);

        /// <summary>Emitted after a cleanup attempt lists exact-name records and before those records are deleted.</summary>
        /// <param name="logger">Reconciler logger.</param>
        /// <param name="Attempt">One-based cleanup attempt.</param>
        /// <param name="RecordCount">Exact-name records about to be deleted. Zero means the name is already empty.</param>
        /// <param name="Fqdn">Exact FQDN being cleaned up.</param>
        [LoggerMessage(
            EventId = 1814,
            Level = LogLevel.Information,
            Message = "Cloudflare DNS cleanup attempt {Attempt}: {RecordCount} record(s) for exact FQDN {Fqdn}")]
        internal static partial void CleanupAttempt(ILogger logger, int Attempt, int RecordCount, string Fqdn);

        /// <summary>Emitted immediately before each exact-name record delete during cleanup.</summary>
        /// <param name="logger">Reconciler logger.</param>
        /// <param name="Type">Cloudflare record type.</param>
        /// <param name="RecordId">Cloudflare record identifier.</param>
        /// <param name="Fqdn">Exact FQDN being cleaned up.</param>
        [LoggerMessage(
            EventId = 1815,
            Level = LogLevel.Information,
            Message = "Deleting {Type} record {RecordId} for exact FQDN {Fqdn} during cleanup")]
        internal static partial void DeletingRecordDuringCleanup(ILogger logger, string Type, string RecordId, string Fqdn);

        /// <summary>
        /// Emitted when a list result for cleanup has a classifiable name that is not the exact FQDN.
        /// That record is not deleted.
        /// </summary>
        /// <param name="logger">Reconciler logger.</param>
        /// <param name="RecordId">Cloudflare record identifier that was skipped.</param>
        /// <param name="Type">Cloudflare record type that was skipped.</param>
        /// <param name="Fqdn">Exact FQDN that was requested.</param>
        [LoggerMessage(
            EventId = 1816,
            Level = LogLevel.Warning,
            Message = "Skipping Cloudflare DNS record {RecordId} (type={Type}) during cleanup of {Fqdn}: name is not an exact match after normalization")]
        internal static partial void SkippingNonExactCleanupRecord(
            ILogger logger,
            string RecordId,
            string Type,
            string Fqdn);

        /// <summary>Emitted after both address families have been read and planned, before creates.</summary>
        /// <param name="logger">Reconciler logger.</param>
        /// <param name="Attempt">One-based reconcile attempt.</param>
        /// <param name="AMissing">Desired IPv4 contents with no current record.</param>
        /// <param name="AStale">IPv4 records whose content is not desired.</param>
        /// <param name="ADup">Extra IPv4 records beyond the first for a desired content.</param>
        /// <param name="AaaaMissing">Desired IPv6 contents with no current record.</param>
        /// <param name="AaaaStale">IPv6 records whose content is not desired.</param>
        /// <param name="AaaaDup">Extra IPv6 records beyond the first for a desired content.</param>
        [LoggerMessage(
            EventId = 1817,
            Level = LogLevel.Information,
            Message = "Cloudflare DNS attempt {Attempt}: A missing={AMissing} stale={AStale} dup={ADup}; AAAA missing={AaaaMissing} stale={AaaaStale} dup={AaaaDup}")]
        internal static partial void ReconcileAttemptPlan(
            ILogger logger,
            int Attempt,
            int AMissing,
            int AStale,
            int ADup,
            int AaaaMissing,
            int AaaaStale,
            int AaaaDup);

        /// <summary>Emitted immediately before each missing A or AAAA record is created.</summary>
        /// <param name="logger">Reconciler logger.</param>
        /// <param name="Type">Record type, <c>A</c> or <c>AAAA</c>.</param>
        /// <param name="Fqdn">Exact FQDN being reconciled.</param>
        /// <param name="Content">Desired address content written to the record.</param>
        [LoggerMessage(
            EventId = 1818,
            Level = LogLevel.Information,
            Message = "Creating {Type} record for {Fqdn} content {Content}")]
        internal static partial void CreatingRecord(ILogger logger, string Type, string Fqdn, string Content);

        /// <summary>
        /// Emitted immediately before a kept desired record is updated because its TTL or proxy flag is not the managed value.
        /// </summary>
        /// <param name="logger">Reconciler logger.</param>
        /// <param name="Type">Record type, <c>A</c> or <c>AAAA</c>.</param>
        /// <param name="RecordId">Cloudflare record identifier being updated.</param>
        /// <param name="Fqdn">Exact FQDN being reconciled.</param>
        /// <param name="ManagedTtl">TTL in seconds written by the update.</param>
        /// <param name="Proxied">Proxy flag written by the update. Managed records are not proxied.</param>
        [LoggerMessage(
            EventId = 1819,
            Level = LogLevel.Information,
            Message = "Updating {Type} record {RecordId} for {Fqdn} to managed TTL={ManagedTtl} and proxied={Proxied}")]
        internal static partial void UpdatingRecordManagedAttributes(
            ILogger logger,
            string Type,
            string RecordId,
            string Fqdn,
            int ManagedTtl,
            bool Proxied);

        /// <summary>Emitted immediately before a stale, duplicate, or unparseable A/AAAA record is deleted.</summary>
        /// <param name="logger">Reconciler logger.</param>
        /// <param name="Type">Record type, <c>A</c> or <c>AAAA</c>.</param>
        /// <param name="RecordId">Cloudflare record identifier being deleted.</param>
        /// <param name="Fqdn">Exact FQDN being reconciled.</param>
        /// <param name="Content">Record content reported by the preceding list.</param>
        [LoggerMessage(
            EventId = 1820,
            Level = LogLevel.Information,
            Message = "Deleting {Type} record {RecordId} for {Fqdn} content {Content}")]
        internal static partial void DeletingRecord(
            ILogger logger,
            string Type,
            string RecordId,
            string Fqdn,
            string Content);

        /// <summary>
        /// Emitted by the reconciliation service after the generated FQDN and zone id are present and before bind addresses are resolved.
        /// </summary>
        /// <param name="logger">Reconciliation-service logger.</param>
        /// <param name="Fqdn">Generated server FQDN from options.</param>
        /// <param name="ZoneId">Configured Cloudflare zone id.</param>
        [LoggerMessage(
            EventId = 1821,
            Level = LogLevel.Information,
            Message = "Starting Cloudflare DNS reconciliation for {Fqdn} in zone {ZoneId}")]
        internal static partial void StartingReconciliation(ILogger logger, string Fqdn, string ZoneId);

        /// <summary>Emitted after reconcile returns and this process marks FQDN ownership active.</summary>
        /// <param name="logger">Reconciliation-service logger.</param>
        /// <param name="Fqdn">Generated server FQDN that was reconciled.</param>
        /// <param name="AddressCount">Eligible bind addresses published, IPv4 and IPv6 combined.</param>
        [LoggerMessage(
            EventId = 1822,
            Level = LogLevel.Information,
            Message = "Cloudflare DNS reconciliation completed for {Fqdn} with {AddressCount} address(es)")]
        internal static partial void ReconciliationCompleted(ILogger logger, string Fqdn, int AddressCount);

        /// <summary>
        /// Emitted on stop when this process never completed a successful reconcile, so no cleanup is attempted.
        /// </summary>
        /// <param name="logger">Reconciliation-service logger.</param>
        [LoggerMessage(
            EventId = 1823,
            Level = LogLevel.Information,
            Message = "Cloudflare DNS cleanup skipped: FQDN ownership was not active (reconcile never completed successfully in this process)")]
        internal static partial void CleanupSkippedOwnershipInactive(ILogger logger);

        /// <summary>Emitted on stop after ownership was active, immediately before exact-FQDN removal.</summary>
        /// <param name="logger">Reconciliation-service logger.</param>
        /// <param name="Fqdn">Generated server FQDN being removed.</param>
        [LoggerMessage(
            EventId = 1824,
            Level = LogLevel.Information,
            Message = "Stopping Cloudflare DNS reconciliation: removing all records for exact FQDN {Fqdn}")]
        internal static partial void StoppingReconciliation(ILogger logger, string Fqdn);

        /// <summary>Emitted after exact-FQDN removal returns during a normal stop.</summary>
        /// <param name="logger">Reconciliation-service logger.</param>
        /// <param name="Fqdn">Generated server FQDN whose API view was verified empty.</param>
        [LoggerMessage(
            EventId = 1825,
            Level = LogLevel.Information,
            Message = "Cloudflare DNS cleanup completed for {Fqdn}: Cloudflare API reports no remaining records for the exact name. Recursive DNS caches may still return prior answers until TTLs expire")]
        internal static partial void CleanupCompleted(ILogger logger, string Fqdn);

        /// <summary>
        /// Emitted when startup reconcile fails or is canceled after it began, before a bounded cleanup of the exact FQDN.
        /// The original startup failure is preserved if cleanup also fails.
        /// </summary>
        /// <param name="logger">Reconciliation-service logger.</param>
        /// <param name="exception">The startup failure, or null when startup was canceled.</param>
        /// <param name="Fqdn">Generated server FQDN to clean up.</param>
        /// <param name="Canceled"><see langword="true"/> when the startup failure was cancellation.</param>
        [LoggerMessage(
            EventId = 1826,
            Level = LogLevel.Warning,
            Message = "Cloudflare DNS reconciliation did not complete successfully for {Fqdn}. Attempting authoritative cleanup of the exact FQDN before failing startup (cancellation={Canceled})")]
        internal static partial void PostFailureCleanupAttempt(
            ILogger logger,
            Exception? exception,
            string Fqdn,
            bool Canceled);

        /// <summary>Emitted when post-failure cleanup verifies that no exact-name records remain.</summary>
        /// <param name="logger">Reconciliation-service logger.</param>
        /// <param name="Fqdn">Generated server FQDN that was verified empty.</param>
        [LoggerMessage(
            EventId = 1827,
            Level = LogLevel.Information,
            Message = "Post-failure Cloudflare DNS cleanup verified for {Fqdn}: no exact-name records remain")]
        internal static partial void PostFailureCleanupVerified(ILogger logger, string Fqdn);

        /// <summary>
        /// Emitted when post-failure cleanup is canceled or hits its dedicated budget.
        /// Removal is not claimed, and the original startup failure is still thrown.
        /// </summary>
        /// <param name="logger">Reconciliation-service logger.</param>
        /// <param name="exception">The cleanup cancellation.</param>
        /// <param name="Fqdn">Generated server FQDN whose cleanup did not finish.</param>
        /// <param name="CleanupBudget">Dedicated wall-clock budget used for this cleanup, not the normal operation timeout.</param>
        [LoggerMessage(
            EventId = 1828,
            Level = LogLevel.Error,
            Message = "Post-failure Cloudflare DNS cleanup for {Fqdn} timed out or was canceled (budget={CleanupBudget}). Records may remain; cleanup is not claimed successful. The next successful startup will re-read and reconcile")]
        internal static partial void PostFailureCleanupTimedOut(
            ILogger logger,
            Exception exception,
            string Fqdn,
            TimeSpan CleanupBudget);

        /// <summary>
        /// Emitted when post-failure cleanup throws an exception other than cancellation.
        /// Removal is not claimed, and the original startup failure is still thrown.
        /// </summary>
        /// <param name="logger">Reconciliation-service logger.</param>
        /// <param name="exception">The cleanup failure.</param>
        /// <param name="Fqdn">Generated server FQDN whose cleanup did not verify.</param>
        [LoggerMessage(
            EventId = 1829,
            Level = LogLevel.Error,
            Message = "Post-failure Cloudflare DNS cleanup for {Fqdn} did not verify removal. Records may remain; the next successful startup will re-read and reconcile. Cleanup is not claimed successful")]
        internal static partial void PostFailureCleanupFailed(ILogger logger, Exception exception, string Fqdn);
    }
}
