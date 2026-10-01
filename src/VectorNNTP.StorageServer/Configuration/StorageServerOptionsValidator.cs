using Microsoft.Extensions.Options;
using VectorNNTP.NNTPD.Configuration;

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
        ValidateFilesystemPath(
            options.CacheDir,
            "StorageServer:CacheDir",
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

        if (options.Storage.JournalCheckpointThresholdBytes < 0)
        {
            failures.Add(
                "StorageServer:Storage:JournalCheckpointThresholdBytes must be greater than or equal to 0.");
        }

        var articleCache = options.Storage.ArticleCache ?? new ArticleMemoryCacheOptions();
        if (articleCache.MaxBytes < 0)
        {
            failures.Add("StorageServer:Storage:ArticleCache:MaxBytes must be greater than or equal to 0.");
        }

        ValidateCompactionPolicy(options.Storage.Compaction, failures);
        ValidateCapacityPolicy(options.Storage.Capacity, failures);
    }

    private static void ValidateCapacityPolicy(
        ArticleCapacityOptions? capacity,
        List<string> failures)
    {
        capacity ??= new ArticleCapacityOptions();
        var util = capacity.MaximumUtilization;
        if (double.IsNaN(util) || double.IsInfinity(util) || util is <= 0 or >= 1)
        {
            failures.Add(
                "StorageServer:Storage:Capacity:MaximumUtilization must be a finite value in the open interval (0, 1).");
        }

        var headroom = capacity.CompactionHeadroom;
        if (double.IsNaN(headroom) || double.IsInfinity(headroom) || headroom <= 0)
        {
            failures.Add(
                "StorageServer:Storage:Capacity:CompactionHeadroom must be a finite value greater than 0.");
        }
        else if (!(double.IsNaN(util) || double.IsInfinity(util) || util is <= 0 or >= 1)
                 && util + headroom >= 1)
        {
            failures.Add(
                "StorageServer:Storage:Capacity:MaximumUtilization + CompactionHeadroom must be strictly less than 1.");
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

        var ratio = compaction.MinimumDeadRatio;
        if (double.IsNaN(ratio) || double.IsInfinity(ratio) || ratio is < 0 or > 1)
        {
            failures.Add(
                "StorageServer:Storage:Compaction:MinimumDeadRatio must be a finite value in the closed interval [0, 1].");
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
