using Microsoft.Extensions.Options;
using VectorNNTP.Common.Messaging.RabbitMq;
using VectorNNTP.NNTPD.Configuration;

namespace VectorNNTP.BackFiller.Configuration;

/// <summary>
/// Validates <see cref="BackFillerOptions"/> at bind / startup time.
/// </summary>
/// <remarks>
/// Does not bind sockets, create directories, or connect to MySQL, RabbitMQ, or Cloudflare.
/// Failure messages never include secret values. RabbitMQ connectivity is validated by
/// Common <see cref="RabbitMqOptionsValidator"/>; this type only cross-checks shutdown
/// grace against <see cref="RabbitMqOptions.MaximumShutdownDrainTimeoutSeconds"/>.
/// </remarks>
public sealed class BackFillerOptionsValidator : IValidateOptions<BackFillerOptions>
{
    private readonly IPhysicalMemoryProvider _physicalMemoryProvider;
    private readonly IOptions<RabbitMqOptions> _rabbitMqOptions;

    /// <summary>
    /// Initializes a new validator.
    /// </summary>
    /// <param name="physicalMemoryProvider">Physical-memory probe for retention capacity.</param>
    /// <param name="rabbitMqOptions">Top-level RabbitMQ options used for grace-period cross-check.</param>
    public BackFillerOptionsValidator(
        IPhysicalMemoryProvider physicalMemoryProvider,
        IOptions<RabbitMqOptions> rabbitMqOptions)
    {
        _physicalMemoryProvider = physicalMemoryProvider ?? throw new ArgumentNullException(nameof(physicalMemoryProvider));
        _rabbitMqOptions = rabbitMqOptions ?? throw new ArgumentNullException(nameof(rabbitMqOptions));
    }

    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, BackFillerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();
        ValidateIdentity(options, failures);
        ValidateBindPortTls(options, failures);
        ValidateAcme(options, failures);
        ValidateDirectories(options, failures);
        ValidateLogging(options, failures);
        ValidateShutdown(options, failures);
        ValidateListener(options, failures);
        ValidateAccountRefresh(options, failures);
        ValidateArticleRetention(options, failures);
        ValidateSystemd(options, failures);
        ValidateRabbitMqDrainAgainstGrace(options, _rabbitMqOptions.Value, failures);

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }

    private static void ValidateSystemd(BackFillerOptions options, List<string> failures)
    {
        if (options.Systemd is null)
        {
            failures.Add($"{nameof(BackFillerOptions.Systemd)} must be provided.");
            return;
        }

        var fraction = options.Systemd.WatchdogIntervalFraction;
        if (double.IsNaN(fraction) || fraction is <= 0 or >= 1)
        {
            failures.Add(
                $"{nameof(BackFillerOptions.Systemd)}.{nameof(BackFillerSystemdOptions.WatchdogIntervalFraction)} must be in the open interval (0, 1).");
        }
        else if (fraction is < 0.05 or > 0.9)
        {
            failures.Add(
                $"{nameof(BackFillerOptions.Systemd)}.{nameof(BackFillerSystemdOptions.WatchdogIntervalFraction)} must be between 0.05 and 0.9 inclusive.");
        }
    }

    private static void ValidateIdentity(BackFillerOptions options, List<string> failures)
    {
        if (options.ServerId is not { } serverId)
        {
            failures.Add(ServerIdRules.Validate(null, "BackFiller:ServerId")!);
            return;
        }

        var serverIdFailure = ServerIdRules.Validate(serverId, "BackFiller:ServerId");
        if (serverIdFailure is not null)
        {
            failures.Add(serverIdFailure);
            return;
        }

        if (string.IsNullOrWhiteSpace(options.DnsSuffix))
        {
            failures.Add("BackFiller:DnsSuffix is required and cannot be empty.");
            return;
        }

        if (string.IsNullOrWhiteSpace(options.CloudFlareZoneId))
        {
            failures.Add("BackFiller:CloudFlareZoneId is required and cannot be empty.");
            return;
        }

        var canonicalSuffix = ApplicationFqdn.CanonicalizeDnsSuffix(options.DnsSuffix);
        if (!BackFillerIdentity.IsValidDnsSuffix(canonicalSuffix))
        {
            failures.Add("BackFiller:DnsSuffix is not a syntactically valid DNS name.");
            return;
        }

        var fqdn = options.Fqdn;
        var hostLabel = ApplicationFqdn.FormatHostLabel(BackFillerOptions.ApplicationPrefix, serverId);
        if (!BackFillerIdentity.IsValidDnsLabel(hostLabel))
        {
            failures.Add("BackFiller generated host label is not a valid DNS label.");
            return;
        }

        if (fqdn.Length > ApplicationFqdn.MaximumLength)
        {
            failures.Add("BackFiller:DnsSuffix produces an FQDN longer than 253 characters.");
        }
        else if (Uri.CheckHostName(fqdn) != UriHostNameType.Dns)
        {
            failures.Add("BackFiller generated FQDN is not a valid DNS hostname.");
        }
    }

    private static void ValidateBindPortTls(BackFillerOptions options, List<string> failures)
    {
        if (options.BindPortTls is null or < 1 or > 65535)
        {
            failures.Add(
                "BackFiller:BindPortTls is required and must be an integer in the range 1–65535 because BackFiller is TLS-only. There is no cleartext listener fallback.");
        }
    }

    private static void ValidateAcme(BackFillerOptions options, List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(options.AcmeDirectoryUrl))
        {
            failures.Add("BackFiller:AcmeDirectoryUrl must be a non-empty HTTPS ACME directory URL.");
        }
        else if (!Uri.TryCreate(options.AcmeDirectoryUrl.Trim(), UriKind.Absolute, out var directoryUri)
                 || directoryUri.Scheme != Uri.UriSchemeHttps)
        {
            failures.Add(
                "BackFiller:AcmeDirectoryUrl must be an absolute HTTPS URL (default is Let's Encrypt staging).");
        }

        if (options.AcmeRenewalThresholdDays is < 1 or > 90)
        {
            failures.Add("BackFiller:AcmeRenewalThresholdDays must be an integer in the range 1–90.");
        }

        if (string.IsNullOrWhiteSpace(options.AcmeStateDir) && string.IsNullOrWhiteSpace(options.CertificateDirectory))
        {
            failures.Add("BackFiller:AcmeStateDir must be a non-empty filesystem path.");
        }
    }

    private static void ValidateDirectories(BackFillerOptions options, List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(options.LogDirectory))
        {
            failures.Add("BackFiller:LogDirectory is required and cannot be empty (old key: DirLogs).");
        }
    }

    private static void ValidateLogging(BackFillerOptions options, List<string> failures)
    {
        if (!BackFillerLogLevelParser.TryParse(options.LogLevel, out _))
        {
            failures.Add(
                "BackFiller:LogLevel must be one of Verbose, Debug, Information, Warning, Error, Fatal.");
        }

        if (options.LogRetentionDays is < BackFillerOptions.MinimumLogRetentionDays
            or > BackFillerOptions.MaximumLogRetentionDays)
        {
            failures.Add(
                $"BackFiller:LogRetentionDays must be an integer in the range {BackFillerOptions.MinimumLogRetentionDays}–{BackFillerOptions.MaximumLogRetentionDays}.");
        }
    }

    private static void ValidateShutdown(BackFillerOptions options, List<string> failures)
    {
        var shutdown = options.Shutdown ?? new BackFillerShutdownOptions();
        if (shutdown.GracePeriodSeconds is < BackFillerShutdownOptions.MinimumGracePeriodSeconds
            or > BackFillerShutdownOptions.MaximumGracePeriodSeconds)
        {
            failures.Add(
                $"BackFiller:Shutdown:GracePeriodSeconds must be between {BackFillerShutdownOptions.MinimumGracePeriodSeconds} and {BackFillerShutdownOptions.MaximumGracePeriodSeconds}.");
        }
    }

    private static void ValidateListener(BackFillerOptions options, List<string> failures)
    {
        var listener = options.Listener ?? new BackFillerListenerOptions();
        if (listener.ParserAccumulationMaxBytes < 32768)
        {
            failures.Add("BackFiller:Listener:ParserAccumulationMaxBytes must be between 32768 and 2147483647.");
        }

        if (listener.TlsHandshakeTimeoutSeconds is < 1 or > 300)
        {
            failures.Add("BackFiller:Listener:TlsHandshakeTimeoutSeconds must be between 1 and 300.");
        }

        if (listener.IoProgressTimeoutSeconds is < 1 or > 600)
        {
            failures.Add("BackFiller:Listener:IoProgressTimeoutSeconds must be between 1 and 600.");
        }

        if (listener.AwaitingReceiptAckTimeoutSeconds is < 1 or > 300)
        {
            failures.Add("BackFiller:Listener:AwaitingReceiptAckTimeoutSeconds must be between 1 and 300.");
        }

        if (listener.MaxQueuedFoundPayloadBytes < 1)
        {
            failures.Add("BackFiller:Listener:MaxQueuedFoundPayloadBytes must be between 1 and 2147483647.");
        }

        if (listener.MaxActiveConnections < 1)
        {
            failures.Add("BackFiller:Listener:MaxActiveConnections must be between 1 and 2147483647.");
        }
    }

    private static void ValidateAccountRefresh(BackFillerOptions options, List<string> failures)
    {
        if (options.BackFillerAccountRefreshIntervalSeconds
            is < BackFillerOptions.MinimumAccountRefreshIntervalSeconds
            or > BackFillerOptions.MaximumAccountRefreshIntervalSeconds)
        {
            failures.Add(
                "BackFiller:BackFillerAccountRefreshIntervalSeconds must be between 5 and 3600.");
        }
    }

    private void ValidateArticleRetention(BackFillerOptions options, List<string> failures)
    {
        var retention = options.ArticleRetention ?? new BackFillerArticleRetentionOptions();
        if (retention.MaximumRetainedPayloadGigabytes < 1)
        {
            failures.Add("BackFiller:ArticleRetention:MaximumRetainedPayloadGigabytes must be greater than zero.");
            return;
        }

        if (retention.RetentionTtlSeconds is < 1 or > 60)
        {
            failures.Add("BackFiller:ArticleRetention:RetentionTtlSeconds must be between 1 and 60.");
        }

        if (retention.MaxOpenableRequestIdsPerArticle is < 1 or > 256)
        {
            failures.Add("BackFiller:ArticleRetention:MaxOpenableRequestIdsPerArticle must be between 1 and 256.");
        }

        if (retention.SweepIntervalSeconds is < 1 or > 60)
        {
            failures.Add("BackFiller:ArticleRetention:SweepIntervalSeconds must be between 1 and 60.");
        }

        long totalPhysicalMemoryBytes;
        try
        {
            totalPhysicalMemoryBytes = _physicalMemoryProvider.GetTotalPhysicalMemoryBytes();
        }
        catch (Exception)
        {
            failures.Add("BackFiller:ArticleRetention:MaximumRetainedPayloadGigabytes cannot be validated because total physical memory could not be determined.");
            return;
        }

        var maximumPolicyGigabytes = Math.Max(
            1,
            (int)(totalPhysicalMemoryBytes * BackFillerArticleRetentionOptions.PhysicalMemoryCeilingRatio
                  / BackFillerArticleRetentionOptions.BytesPerGibibyte));
        if (retention.MaximumRetainedPayloadGigabytes > maximumPolicyGigabytes)
        {
            failures.Add(
                $"BackFiller:ArticleRetention:MaximumRetainedPayloadGigabytes exceeds the allowed 80% physical-memory ceiling ({maximumPolicyGigabytes} GiB).");
        }

        var maxSupportedGigabytes = (int)Math.Min(int.MaxValue, long.MaxValue / BackFillerArticleRetentionOptions.BytesPerGibibyte);
        if (retention.MaximumRetainedPayloadGigabytes > maxSupportedGigabytes)
        {
            failures.Add($"BackFiller:ArticleRetention:MaximumRetainedPayloadGigabytes must be less than or equal to {maxSupportedGigabytes}.");
        }
    }

    private static void ValidateRabbitMqDrainAgainstGrace(
        BackFillerOptions options,
        RabbitMqOptions rabbitMq,
        List<string> failures)
    {
        ArgumentNullException.ThrowIfNull(rabbitMq);

        var grace = options.Shutdown?.GracePeriodSeconds ?? 0;
        if (grace > 0
            && rabbitMq.MaximumShutdownDrainTimeoutSeconds is { } drain
            && drain > grace)
        {
            failures.Add(
                "RabbitMQ:MaximumShutdownDrainTimeoutSeconds must be less than or equal to BackFiller:Shutdown:GracePeriodSeconds.");
        }
    }

    private static void RequireRange(int? value, int min, int max, string key, List<string> failures)
    {
        if (value is null)
        {
            failures.Add($"{key} is required.");
            return;
        }

        if (value < min || value > max)
        {
            failures.Add($"{key} must be between {min} and {max}.");
        }
    }

}
