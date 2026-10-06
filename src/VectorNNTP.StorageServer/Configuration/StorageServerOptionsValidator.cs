using Microsoft.Extensions.Options;
using VectorNNTP.Common.Configuration;

namespace VectorNNTP.StorageServer.Configuration;

/// <summary>
/// Validates <see cref="StorageServerOptions"/> at bind / startup time.
/// </summary>
/// <remarks>
/// Does not bind sockets, create directories, or connect to Cloudflare.
/// Failure messages never include secret values.
/// </remarks>
public sealed class StorageServerOptionsValidator : IValidateOptions<StorageServerOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, StorageServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();
        ValidateIdentity(options, failures);
        ValidateBindPorts(options, failures);
        ValidateAcme(options, failures);
        ValidateDirectories(options, failures);
        ValidateLifecycle(options, failures);
        ValidateListener(options, failures);
        ValidateSystemd(options, failures);

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }

    private static void ValidateSystemd(StorageServerOptions options, List<string> failures)
    {
        if (options.Systemd is null)
        {
            failures.Add($"{nameof(StorageServerOptions.Systemd)} must be provided.");
            return;
        }

        var fraction = options.Systemd.WatchdogIntervalFraction;
        if (double.IsNaN(fraction) || fraction is <= 0 or >= 1)
        {
            failures.Add(
                $"{nameof(StorageServerOptions.Systemd)}.{nameof(StorageServerSystemdOptions.WatchdogIntervalFraction)} must be in the open interval (0, 1).");
        }
        else if (fraction is < 0.05 or > 0.9)
        {
            failures.Add(
                $"{nameof(StorageServerOptions.Systemd)}.{nameof(StorageServerSystemdOptions.WatchdogIntervalFraction)} must be between 0.05 and 0.9 inclusive.");
        }
    }

    private static void ValidateIdentity(StorageServerOptions options, List<string> failures)
    {
        if (options.ServerId is not { } serverId)
        {
            failures.Add(ServerIdRules.Validate(null, "StorageServer:ServerId")!);
            return;
        }

        var serverIdFailure = ServerIdRules.Validate(serverId, "StorageServer:ServerId");
        if (serverIdFailure is not null)
        {
            failures.Add(serverIdFailure);
            return;
        }

        if (string.IsNullOrWhiteSpace(options.DnsSuffix))
        {
            failures.Add("StorageServer:DnsSuffix is required and cannot be empty.");
            return;
        }

        if (string.IsNullOrWhiteSpace(options.CloudFlareZoneId))
        {
            failures.Add("StorageServer:CloudFlareZoneId is required and cannot be empty.");
            return;
        }

        var canonicalSuffix = ApplicationFqdn.CanonicalizeDnsSuffix(options.DnsSuffix);
        if (!StorageServerIdentity.IsValidDnsSuffix(canonicalSuffix))
        {
            failures.Add("StorageServer:DnsSuffix is not a syntactically valid DNS name.");
            return;
        }

        var fqdn = options.Fqdn;
        var hostLabel = ApplicationFqdn.FormatHostLabel(StorageServerOptions.ApplicationPrefix, serverId);
        if (!StorageServerIdentity.IsValidDnsLabel(hostLabel))
        {
            failures.Add("StorageServer generated host label is not a valid DNS label.");
            return;
        }

        if (fqdn.Length > ApplicationFqdn.MaximumLength)
        {
            failures.Add("StorageServer:DnsSuffix produces an FQDN longer than 253 characters.");
        }
        else if (Uri.CheckHostName(fqdn) != UriHostNameType.Dns)
        {
            failures.Add("StorageServer generated FQDN is not a valid DNS hostname.");
        }
    }

    private static void ValidateBindPorts(StorageServerOptions options, List<string> failures)
    {
        if (options.BindPort is < 0 or > 65535)
        {
            failures.Add("StorageServer:BindPort must be an integer in the range 0–65535 (0 = unused; cleartext is never listened on).");
        }

        if (options.BindPortTls is null or < 1 or > 65535)
        {
            failures.Add(
                "StorageServer:BindPortTls is required and must be an integer in the range 1–65535 because StorageServer is TLS-only. There is no cleartext listener fallback.");
        }
    }

    private static void ValidateAcme(StorageServerOptions options, List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(options.AcmeDirectoryUrl))
        {
            failures.Add("StorageServer:AcmeDirectoryUrl must be a non-empty HTTPS ACME directory URL.");
        }
        else if (!Uri.TryCreate(options.AcmeDirectoryUrl.Trim(), UriKind.Absolute, out var directoryUri)
                 || directoryUri.Scheme != Uri.UriSchemeHttps)
        {
            failures.Add(
                "StorageServer:AcmeDirectoryUrl must be an absolute HTTPS URL (default is Let's Encrypt staging).");
        }

        if (options.AcmeRenewalThresholdDays is < 1 or > 90)
        {
            failures.Add("StorageServer:AcmeRenewalThresholdDays must be an integer in the range 1–90.");
        }

        if (string.IsNullOrWhiteSpace(options.AcmeStateDir) && string.IsNullOrWhiteSpace(options.CertificateDirectory))
        {
            failures.Add("StorageServer:AcmeStateDir must be a non-empty filesystem path.");
        }
    }

    private static void ValidateDirectories(StorageServerOptions options, List<string> failures)
    {
        ValidateFilesystemPath(
            options.LogDir,
            "StorageServer:LogDir",
            failures);
        ValidateStorage(options, failures);
    }

    private static void ValidateStorage(StorageServerOptions options, List<string> failures)
    {
        if (options.Storage is null)
        {
            failures.Add("StorageServer:Storage must be provided.");
            return;
        }

        ValidateFilesystemPath(
            options.Storage.CacheDir,
            "StorageServer:Storage:CacheDir",
            failures);
        ValidateFilesystemPath(
            options.Storage.ControlDir,
            "StorageServer:Storage:ControlDir",
            failures);

        if (options.Storage.JournalSoftLimitBytes < 1)
        {
            failures.Add("StorageServer:Storage:JournalSoftLimitBytes must be at least 1.");
        }

        if (options.Storage.JournalHardLimitBytes < 1)
        {
            failures.Add("StorageServer:Storage:JournalHardLimitBytes must be at least 1.");
        }
        else if (options.Storage.JournalSoftLimitBytes >= 1
                 && options.Storage.JournalHardLimitBytes < options.Storage.JournalSoftLimitBytes)
        {
            failures.Add(
                "StorageServer:Storage:JournalHardLimitBytes must be greater than or equal to JournalSoftLimitBytes.");
        }

        if (options.Storage.SegmentTargetSizeBytes < 1)
        {
            failures.Add("StorageServer:Storage:SegmentTargetSizeBytes must be at least 1.");
        }

        if (options.Storage.ActiveSegmentCount is not 1 and not 2 and not 4)
        {
            failures.Add("StorageServer:Storage:ActiveSegmentCount must be 1, 2, or 4.");
        }

        if (options.Storage.MaxSegmentSealDelay < TimeSpan.Zero)
        {
            failures.Add(
                "StorageServer:Storage:MaxSegmentSealDelay must be greater than or equal to 00:00:00. 00:00:00 disables age sealing.");
        }

        if (options.Storage.MaxRetentionAge < TimeSpan.Zero)
        {
            failures.Add(
                "StorageServer:Storage:MaxRetentionAge must be greater than or equal to 00:00:00. 00:00:00 disables age-based retention.");
        }

        if (options.Storage.JournalCheckpointThresholdBytes < 0)
        {
            failures.Add(
                "StorageServer:Storage:JournalCheckpointThresholdBytes must be greater than or equal to 0.");
        }

        if (options.Storage.IndexCheckpointThresholdBytes < 0)
        {
            failures.Add(
                "StorageServer:Storage:IndexCheckpointThresholdBytes must be greater than or equal to 0.");
        }

        var articleCache = options.Storage.ArticleCache ?? new ArticleMemoryCacheOptions();
        if (articleCache.MaxBytes < 0)
        {
            failures.Add("StorageServer:Storage:ArticleCache:MaxBytes must be greater than or equal to 0.");
        }

        ValidateCompactionPolicy(options.Storage.Compaction, failures);
        ValidateCapacityPolicy(options.Storage.Capacity, failures);
        ValidateBulkPressure(options.Storage.BulkPressure, failures);
    }

    private static void ValidateCapacityPolicy(
        ArticleCapacityOptions? capacity,
        List<string> failures)
    {
        capacity ??= new ArticleCapacityOptions();
        ValidatePercent(
            capacity.MaximumUtilization,
            "StorageServer:Storage:Capacity:MaximumUtilization",
            failures);
        ValidatePercent(
            capacity.CompactionHeadroom,
            "StorageServer:Storage:Capacity:CompactionHeadroom",
            failures);
        ValidatePercent(
            capacity.MaximumUsageCapacity,
            "StorageServer:Storage:Capacity:MaximumUsageCapacity",
            failures);
        ValidatePercent(
            capacity.FreeCapacity,
            "StorageServer:Storage:Capacity:FreeCapacity",
            failures);

        if (capacity.MaximumUtilization is < 1 or > 100)
        {
            failures.Add(
                "StorageServer:Storage:Capacity:MaximumUtilization must be an integer from 1 to 100.");
        }

        if (capacity.CompactionHeadroom < 1)
        {
            failures.Add(
                "StorageServer:Storage:Capacity:CompactionHeadroom must be an integer from 1 to 100.");
        }
        else if (capacity.MaximumUtilization is >= 1 and <= 100
                 && capacity.MaximumUtilization + capacity.CompactionHeadroom > 100)
        {
            failures.Add(
                "StorageServer:Storage:Capacity:MaximumUtilization + CompactionHeadroom must be less than or equal to 100.");
        }

        if (capacity.MaximumUsageCapacity < 1)
        {
            failures.Add(
                "StorageServer:Storage:Capacity:MaximumUsageCapacity must be an integer from 1 to 100.");
        }
        else if (capacity.MaximumUtilization is >= 1 and <= 100
                 && capacity.MaximumUsageCapacity >= capacity.MaximumUtilization)
        {
            failures.Add(
                "StorageServer:Storage:Capacity:MaximumUsageCapacity must be less than MaximumUtilization.");
        }

        if (capacity.FreeCapacity < 1)
        {
            failures.Add(
                "StorageServer:Storage:Capacity:FreeCapacity must be an integer from 1 to 100.");
        }
        else if (capacity.FreeCapacity > capacity.MaximumUsageCapacity)
        {
            failures.Add(
                "StorageServer:Storage:Capacity:FreeCapacity must be less than or equal to MaximumUsageCapacity.");
        }
    }

    private static void ValidateBulkPressure(
        BulkStoragePressureOptions? bulkPressure,
        List<string> failures)
    {
        bulkPressure ??= new BulkStoragePressureOptions();
        ValidatePercent(
            bulkPressure.WarningPercent,
            "StorageServer:Storage:BulkPressure:WarningPercent",
            failures);
        ValidatePercent(
            bulkPressure.PressurePercent,
            "StorageServer:Storage:BulkPressure:PressurePercent",
            failures);
        ValidatePercent(
            bulkPressure.HighPercent,
            "StorageServer:Storage:BulkPressure:HighPercent",
            failures);
        ValidatePercent(
            bulkPressure.CriticalPercent,
            "StorageServer:Storage:BulkPressure:CriticalPercent",
            failures);
        ValidatePercent(
            bulkPressure.EmergencyPercent,
            "StorageServer:Storage:BulkPressure:EmergencyPercent",
            failures);
        ValidatePercent(
            bulkPressure.OperationalReservePercent,
            "StorageServer:Storage:BulkPressure:OperationalReservePercent",
            failures);
        ValidatePercent(
            bulkPressure.RecoveryReservePercent,
            "StorageServer:Storage:BulkPressure:RecoveryReservePercent",
            failures);
        ValidatePercent(
            bulkPressure.RewriteReservePercent,
            "StorageServer:Storage:BulkPressure:RewriteReservePercent",
            failures);

        if (bulkPressure.WarningPercent is >= 0 and <= 100
            && bulkPressure.PressurePercent is >= 0 and <= 100
            && bulkPressure.HighPercent is >= 0 and <= 100
            && bulkPressure.CriticalPercent is >= 0 and <= 100
            && bulkPressure.EmergencyPercent is >= 0 and <= 100
            && (bulkPressure.WarningPercent >= bulkPressure.PressurePercent
                || bulkPressure.PressurePercent >= bulkPressure.HighPercent
                || bulkPressure.HighPercent >= bulkPressure.CriticalPercent
                || bulkPressure.CriticalPercent >= bulkPressure.EmergencyPercent))
        {
            failures.Add(
                "StorageServer:Storage:BulkPressure watermarks must be strictly increasing: WarningPercent < PressurePercent < HighPercent < CriticalPercent < EmergencyPercent.");
        }
    }

    private static void ValidatePercent(int percent, string key, List<string> failures)
    {
        if (percent is < 0 or > 100)
        {
            failures.Add($"{key} must be an integer from 0 to 100.");
        }
    }

    private static void ValidateCompactionPolicy(
        ArticleCompactionPolicyOptions? compaction,
        List<string> failures)
    {
        compaction ??= new ArticleCompactionPolicyOptions();

        if (compaction.MinimumDeadBytes < 0)
        {
            failures.Add(
                "StorageServer:Storage:Compaction:MinimumDeadBytes must be greater than or equal to 0.");
        }

        if (compaction.MinimumDeadRatio is < 0 or > 100)
        {
            failures.Add(
                "StorageServer:Storage:Compaction:MinimumDeadRatio must be an integer from 0 to 100.");
        }

        var interval = compaction.Interval;
        if (interval <= TimeSpan.Zero)
        {
            failures.Add(
                "StorageServer:Storage:Compaction:Interval must be greater than 00:00:00 (zero and negative values are invalid).");
        }
    }

    private static void ValidateFilesystemPath(string? path, string configurationKey, List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            failures.Add($"{configurationKey} is required and cannot be empty.");
            return;
        }

        var trimmed = path.Trim();
        if (trimmed.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            failures.Add($"{configurationKey} contains invalid path characters.");
            return;
        }

        try
        {
            _ = Path.GetFullPath(trimmed);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            failures.Add($"{configurationKey} is not a valid filesystem path.");
        }
    }

    private static void ValidateLifecycle(StorageServerOptions options, List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(options.ApplicationName))
        {
            failures.Add($"{nameof(StorageServerOptions.ApplicationName)} must be a non-empty string.");
        }
        else if (options.ApplicationName.Length > 128)
        {
            failures.Add($"{nameof(StorageServerOptions.ApplicationName)} must be 128 characters or fewer.");
        }

        if (options.GracefulShutdownTimeout < TimeSpan.FromSeconds(1))
        {
            failures.Add($"{nameof(StorageServerOptions.GracefulShutdownTimeout)} must be at least 1 second.");
        }

        if (options.GracefulShutdownTimeout > TimeSpan.FromHours(1))
        {
            failures.Add($"{nameof(StorageServerOptions.GracefulShutdownTimeout)} must not exceed 1 hour.");
        }

        if (options.StartupTimeout is { } startup)
        {
            if (startup < TimeSpan.FromSeconds(1))
            {
                failures.Add($"{nameof(StorageServerOptions.StartupTimeout)} must be at least 1 second when set.");
            }

            if (startup > TimeSpan.FromHours(1))
            {
                failures.Add($"{nameof(StorageServerOptions.StartupTimeout)} must not exceed 1 hour when set.");
            }
        }
    }

    private static void ValidateListener(StorageServerOptions options, List<string> failures)
    {
        var listener = options.Listener ?? new StorageServerListenerOptions();
        if (listener.ParserAccumulationMaxBytes < 32768)
        {
            failures.Add("StorageServer:Listener:ParserAccumulationMaxBytes must be between 32768 and 2147483647.");
        }

        if (listener.TlsHandshakeTimeoutSeconds is < 1 or > 300)
        {
            failures.Add("StorageServer:Listener:TlsHandshakeTimeoutSeconds must be between 1 and 300.");
        }

        if (listener.IoProgressTimeoutSeconds is < 1 or > 600)
        {
            failures.Add("StorageServer:Listener:IoProgressTimeoutSeconds must be between 1 and 600.");
        }

        if (listener.MaxActiveConnections < 1)
        {
            failures.Add("StorageServer:Listener:MaxActiveConnections must be between 1 and 2147483647.");
        }
    }
}
