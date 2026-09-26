using System.Net;
using Microsoft.Extensions.Options;

namespace VectorNNTP.BackFiller.Configuration;

/// <summary>
/// Validates <see cref="BackFillerOptions"/> at bind / startup time.
/// </summary>
/// <remarks>
/// Does not bind sockets, create directories, or connect to MySQL, RabbitMQ, or Cloudflare.
/// Failure messages never include secret values.
/// </remarks>
public sealed class BackFillerOptionsValidator : IValidateOptions<BackFillerOptions>
{
    private readonly ILocalIpAddressAssignee _localIpAddressAssignee;
    private readonly IPhysicalMemoryProvider _physicalMemoryProvider;

    /// <summary>
    /// Initializes a new validator.
    /// </summary>
    /// <param name="localIpAddressAssignee">NIC probe for explicit bind addresses.</param>
    /// <param name="physicalMemoryProvider">Physical-memory probe for retention capacity.</param>
    public BackFillerOptionsValidator(
        ILocalIpAddressAssignee localIpAddressAssignee,
        IPhysicalMemoryProvider physicalMemoryProvider)
    {
        _localIpAddressAssignee = localIpAddressAssignee ?? throw new ArgumentNullException(nameof(localIpAddressAssignee));
        _physicalMemoryProvider = physicalMemoryProvider ?? throw new ArgumentNullException(nameof(physicalMemoryProvider));
    }

    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, BackFillerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();
        ValidateIdentity(options, failures);
        ValidateBind(options, failures);
        ValidateDirectories(options, failures);
        ValidateShutdown(options, failures);
        ValidateListener(options, failures);
        ValidateAccounts(options, failures);
        ValidateArticleRetention(options, failures);
        ValidateTransitServer(options, failures);
        ValidateLetsEncrypt(options, failures);
        ValidateRabbitMq(options, failures);

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }

    private static void ValidateIdentity(BackFillerOptions options, List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(options.Name))
        {
            failures.Add("BackFiller:Name is required and cannot be empty.");
            return;
        }

        if (options.ServerId is null)
        {
            failures.Add(
                $"BackFiller:ServerId is required and must be an integer in the range {BackFillerIdentity.MinimumServerId}–{BackFillerIdentity.MaximumServerId} (old key: BackFiller:Id).");
            return;
        }

        if (options.ServerId is < BackFillerIdentity.MinimumServerId or > BackFillerIdentity.MaximumServerId)
        {
            failures.Add($"BackFiller:ServerId must be between {BackFillerIdentity.MinimumServerId} and {BackFillerIdentity.MaximumServerId}.");
            return;
        }

        if (string.IsNullOrWhiteSpace(options.DnsSuffix))
        {
            failures.Add("BackFiller:DnsSuffix is required and cannot be empty.");
            return;
        }

        var canonicalName = BackFillerIdentity.CanonicalizeName(options.Name);
        var canonicalSuffix = BackFillerIdentity.CanonicalizeDnsSuffix(options.DnsSuffix);

        if (!BackFillerIdentity.IsValidDnsLabel(canonicalName))
        {
            failures.Add("BackFiller:Name must be a valid DNS label (letters, digits, hyphens; no leading/trailing hyphen; max 63 chars).");
        }

        if (!BackFillerIdentity.IsValidDnsSuffix(canonicalSuffix))
        {
            failures.Add("BackFiller:DnsSuffix is not a syntactically valid DNS name.");
        }

        if (failures.Count > 0)
        {
            return;
        }

        var hostLabel = canonicalName + BackFillerIdentity.FormatServerId(options.ServerId.Value);
        if (!BackFillerIdentity.IsValidDnsLabel(hostLabel))
        {
            failures.Add("BackFiller:Name + ServerId produces an invalid host label (must be <=63 chars and DNS-label compliant).");
            return;
        }

        var fqdn = BackFillerIdentity.BuildFqdn(canonicalName, options.ServerId.Value, canonicalSuffix);
        if (fqdn.Length > BackFillerIdentity.MaximumFqdnLength)
        {
            failures.Add("BackFiller:DnsSuffix produces an FQDN longer than 253 characters.");
        }
        else if (Uri.CheckHostName(fqdn) != UriHostNameType.Dns)
        {
            failures.Add("BackFiller generated FQDN is not a valid DNS hostname.");
        }
    }

    private void ValidateBind(BackFillerOptions options, List<string> failures)
    {
        if (options.BindPort is null)
        {
            failures.Add("BackFiller:BindPort is required.");
        }
        else if (options.BindPort is < 1 or > 65535)
        {
            failures.Add("BackFiller:BindPort must be between 1 and 65535.");
        }

        if (options.BindAddress is null || options.BindAddress.Length == 0)
        {
            return;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < options.BindAddress.Length; i++)
        {
            var entry = options.BindAddress[i];
            if (string.IsNullOrWhiteSpace(entry))
            {
                failures.Add($"BackFiller:BindAddress[{i}] must not be empty.");
                continue;
            }

            var trimmed = entry.Trim();
            if (!seen.Add(trimmed))
            {
                failures.Add($"BackFiller:BindAddress[{i}] duplicate addresses are not allowed.");
                continue;
            }

            if (BackFillerOptions.IsBindAddressWildcard(trimmed))
            {
                continue;
            }

            if (!IPAddress.TryParse(trimmed, out var address))
            {
                failures.Add($"BackFiller:BindAddress[{i}] is not a valid IPv4 or IPv6 address.");
                continue;
            }

            if (!_localIpAddressAssignee.IsLocallyAssigned(address))
            {
                failures.Add($"BackFiller:BindAddress[{i}] is not assigned to any local network interface.");
            }
        }
    }

    private static void ValidateDirectories(BackFillerOptions options, List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(options.LogDirectory))
        {
            failures.Add("BackFiller:LogDirectory is required and cannot be empty (old key: DirLogs).");
        }

        if (string.IsNullOrWhiteSpace(options.CertificateDirectory))
        {
            failures.Add("BackFiller:CertificateDirectory is required and cannot be empty (old key: DirCerts).");
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

        if (shutdown.DrainQueuedWork && !shutdown.FinishActiveArticles)
        {
            failures.Add("BackFiller:Shutdown:DrainQueuedWork requires FinishActiveArticles to be true.");
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

    private static void ValidateAccounts(BackFillerOptions options, List<string> failures)
    {
        var accounts = options.Accounts ?? new BackFillerAccountsOptions();
        if (accounts.RefreshIntervalSeconds is < 5 or > 3600)
        {
            failures.Add("BackFiller:Accounts:RefreshIntervalSeconds must be between 5 and 3600.");
        }

        if (accounts.CommandTimeoutSeconds is < 1 or > 120)
        {
            failures.Add("BackFiller:Accounts:CommandTimeoutSeconds must be between 1 and 120.");
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

    private static void ValidateTransitServer(BackFillerOptions options, List<string> failures)
    {
        var transit = options.TransitServer ?? new BackFillerTransitServerOptions();
        if (string.IsNullOrWhiteSpace(transit.Host))
        {
            failures.Add("BackFiller:TransitServer:Host is required and cannot be empty.");
        }
        else
        {
            var host = transit.Host.Trim();
            if (host.Contains("://", StringComparison.Ordinal))
            {
                failures.Add("BackFiller:TransitServer:Host must not include a URI scheme.");
            }
            else if (!IPAddress.TryParse(host, out _) && Uri.CheckHostName(host) != UriHostNameType.Dns)
            {
                failures.Add("BackFiller:TransitServer:Host must be a valid hostname or IP address.");
            }
        }

        if (transit.Port is < 1 or > 65535)
        {
            failures.Add("BackFiller:TransitServer:Port must be between 1 and 65535.");
        }
    }

    private static void ValidateLetsEncrypt(BackFillerOptions options, List<string> failures)
    {
        var acme = options.LetsEncrypt ?? new BackFillerLetsEncryptOptions();
        if (string.IsNullOrWhiteSpace(acme.AcmeAccountEmail) || !IsPlausibleEmail(acme.AcmeAccountEmail))
        {
            failures.Add("BackFiller:LetsEncrypt:AcmeAccountEmail is required and must be a valid email address.");
        }

        if (string.IsNullOrWhiteSpace(acme.AcmeAccountKeyPem))
        {
            failures.Add("BackFiller:LetsEncrypt:AcmeAccountKeyPem is required and cannot be empty.");
        }

        RequireRange(acme.AcmeTransientRetryMaxAttempts, 1, 10, "BackFiller:LetsEncrypt:AcmeTransientRetryMaxAttempts", failures);
        RequireRange(acme.ClockSkewCheckTtlMinutes, 1, 60, "BackFiller:LetsEncrypt:ClockSkewCheckTtlMinutes", failures);
        RequireRange(acme.ClockSkewMaxMinutes, 1, 60, "BackFiller:LetsEncrypt:ClockSkewMaxMinutes", failures);
        RequireRange(acme.DnsAuthoritativeNsCacheMinutes, 1, 60, "BackFiller:LetsEncrypt:DnsAuthoritativeNsCacheMinutes", failures);
        RequireRange(acme.DnsPropagationDelaySeconds, 0, 600, "BackFiller:LetsEncrypt:DnsPropagationDelaySeconds", failures);
        RequireRange(acme.DnsTxtPollIntervalSeconds, 1, 60, "BackFiller:LetsEncrypt:DnsTxtPollIntervalSeconds", failures);
        RequireRange(acme.DnsTxtPollTimeoutSeconds, 1, 3600, "BackFiller:LetsEncrypt:DnsTxtPollTimeoutSeconds", failures);
        RequireRange(acme.RenewalCheckIntervalHours, 1, 168, "BackFiller:LetsEncrypt:RenewalCheckIntervalHours", failures);
        RequireRange(acme.RenewBeforeExpiryDays, 1, 60, "BackFiller:LetsEncrypt:RenewBeforeExpiryDays", failures);

        if (acme.DnsAuthoritativeQuorumRatio is null
            || acme.DnsAuthoritativeQuorumRatio is <= 0d or > 1d)
        {
            failures.Add("BackFiller:LetsEncrypt:DnsAuthoritativeQuorumRatio must be greater than 0 and less than or equal to 1.");
        }

        if (acme.RenewalJitterRatio is null
            || acme.RenewalJitterRatio is < 0d or >= 1d)
        {
            failures.Add("BackFiller:LetsEncrypt:RenewalJitterRatio must be between 0 (inclusive) and 1 (exclusive).");
        }

        if (string.IsNullOrWhiteSpace(acme.PfxExportPassword))
        {
            failures.Add(
                $"BackFiller:LetsEncrypt:PfxExportPassword is required (use environment variable {BackFillerOptions.PfxExportPasswordEnvironmentVariable}; never commit the value).");
        }

        if (string.IsNullOrWhiteSpace(acme.CloudFlareApiToken))
        {
            failures.Add(
                $"BackFiller:LetsEncrypt:CloudFlareApiToken is required (use environment variable {BackFillerOptions.CloudFlareApiTokenEnvironmentVariable}; never commit the value).");
        }

        if (string.IsNullOrWhiteSpace(acme.CloudFlareZoneId))
        {
            failures.Add("BackFiller:LetsEncrypt:CloudFlareZoneId is required.");
        }

        if (acme.DomainNames is null)
        {
            return;
        }

        for (var i = 0; i < acme.DomainNames.Length; i++)
        {
            var domain = acme.DomainNames[i];
            if (string.IsNullOrWhiteSpace(domain))
            {
                failures.Add($"BackFiller:LetsEncrypt:DomainNames[{i}] must not be empty.");
                continue;
            }

            var trimmed = domain.Trim();
            if (trimmed.StartsWith("*.", StringComparison.Ordinal))
            {
                var suffix = trimmed[2..];
                if (!BackFillerIdentity.IsValidDnsSuffix(BackFillerIdentity.CanonicalizeDnsSuffix(suffix)))
                {
                    failures.Add($"BackFiller:LetsEncrypt:DomainNames[{i}] wildcard entries must be valid DNS names in the form *.example.com.");
                }

                continue;
            }

            if (Uri.CheckHostName(trimmed) != UriHostNameType.Dns)
            {
                failures.Add($"BackFiller:LetsEncrypt:DomainNames[{i}] must be a valid DNS name.");
            }
        }
    }

    private static void ValidateRabbitMq(BackFillerOptions options, List<string> failures)
    {
        var rabbit = options.RabbitMQ ?? new BackFillerRabbitMqOptions();
        RequireRange(rabbit.WorkRequestMaxPayloadBytes, 1, 4096, "BackFiller:RabbitMQ:WorkRequestMaxPayloadBytes", failures);
        RequireRange(rabbit.ChannelLeaseTimeoutSeconds, 1, 3600, "BackFiller:RabbitMQ:ChannelLeaseTimeoutSeconds", failures);
        RequireRange(rabbit.RpcTimeoutSeconds, 1, 3600, "BackFiller:RabbitMQ:RpcTimeoutSeconds", failures);
        RequireRange(rabbit.ConnectionBlockedTimeoutSeconds, 5, 3600, "BackFiller:RabbitMQ:ConnectionBlockedTimeoutSeconds", failures);

        if (rabbit.ChannelLeaseTimeoutSeconds is { } lease
            && rabbit.RpcTimeoutSeconds is { } rpc
            && lease < rpc)
        {
            failures.Add("BackFiller:RabbitMQ:ChannelLeaseTimeoutSeconds must be greater than or equal to RpcTimeoutSeconds.");
        }

        if (rabbit.ConnectionBlockedTimeoutSeconds is { } blocked
            && rabbit.RpcTimeoutSeconds is { } rpcTimeout
            && blocked < rpcTimeout)
        {
            failures.Add("BackFiller:RabbitMQ:ConnectionBlockedTimeoutSeconds must be greater than or equal to RpcTimeoutSeconds.");
        }

        if (rabbit.Hosts is null || rabbit.Hosts.Length == 0)
        {
            failures.Add("BackFiller:RabbitMQ:Hosts must contain at least one entry.");
        }
        else
        {
            var normalizedHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < rabbit.Hosts.Length; i++)
            {
                var host = rabbit.Hosts[i];
                if (string.IsNullOrWhiteSpace(host))
                {
                    failures.Add($"BackFiller:RabbitMQ:Hosts[{i}] must not be empty.");
                    continue;
                }

                var trimmedHost = host.Trim();
                if (trimmedHost.Contains("://", StringComparison.Ordinal)
                    || trimmedHost.Contains('@')
                    || trimmedHost.Contains('/')
                    || trimmedHost.Contains('?'))
                {
                    failures.Add($"BackFiller:RabbitMQ:Hosts[{i}] must be a hostname or IP without scheme, credentials, path, or query.");
                    continue;
                }

                if (!IPAddress.TryParse(trimmedHost, out _) && Uri.CheckHostName(trimmedHost) != UriHostNameType.Dns)
                {
                    failures.Add($"BackFiller:RabbitMQ:Hosts[{i}] must be a valid hostname or IP address.");
                    continue;
                }

                if (!normalizedHosts.Add(trimmedHost))
                {
                    failures.Add($"BackFiller:RabbitMQ:Hosts[{i}] duplicate host entries are not allowed.");
                }
            }
        }

        var hasUsername = !string.IsNullOrWhiteSpace(rabbit.Username);
        if (rabbit.Username is not null && string.IsNullOrWhiteSpace(rabbit.Username))
        {
            failures.Add("BackFiller:RabbitMQ:Username must not be empty or whitespace when configured.");
        }

        if (hasUsername && string.IsNullOrWhiteSpace(rabbit.Password))
        {
            failures.Add("BackFiller:RabbitMQ:Password is required when Username is configured.");
        }

        if (!hasUsername && rabbit.Password is not null)
        {
            failures.Add("BackFiller:RabbitMQ:Username is required when Password is configured.");
        }

        if (string.IsNullOrWhiteSpace(rabbit.VirtualHost))
        {
            failures.Add("BackFiller:RabbitMQ:VirtualHost is required and cannot be empty.");
        }
        else if (rabbit.VirtualHost.Contains('\0'))
        {
            failures.Add("BackFiller:RabbitMQ:VirtualHost contains an invalid null character.");
        }

        if (rabbit.EnableSsl is null)
        {
            failures.Add("BackFiller:RabbitMQ:EnableSsl is required.");
        }

        RequireRange(rabbit.Port, 1, 65535, "BackFiller:RabbitMQ:Port", failures);
        RequireRange(rabbit.ChannelPoolSize, 1, 8192, "BackFiller:RabbitMQ:ChannelPoolSize", failures);
        RequireRange(rabbit.MinConnections, 1, 512, "BackFiller:RabbitMQ:MinConnections", failures);
        RequireRange(rabbit.MaxConnections, 1, 512, "BackFiller:RabbitMQ:MaxConnections", failures);
        RequireRange(rabbit.MaxConsecutiveRecoveryFailures, 1, 100, "BackFiller:RabbitMQ:MaxConsecutiveRecoveryFailures", failures);
        RequireRange(rabbit.MaxPendingLeaseWaiters, 0, 65536, "BackFiller:RabbitMQ:MaxPendingLeaseWaiters", failures);
        RequireRange(rabbit.ConnectionScaleDownIdleSeconds, 30, 86400, "BackFiller:RabbitMQ:ConnectionScaleDownIdleSeconds", failures);
        RequireRange(rabbit.ScaleDownCooldownSeconds, 0, 3600, "BackFiller:RabbitMQ:ScaleDownCooldownSeconds", failures);
        RequireRange(rabbit.NetworkRecoveryIntervalSeconds, 1, 3600, "BackFiller:RabbitMQ:NetworkRecoveryIntervalSeconds", failures);
        RequireRange(rabbit.PoolReconnectBaseDelayMs, 50, 60000, "BackFiller:RabbitMQ:PoolReconnectBaseDelayMs", failures);
        RequireRange(rabbit.PoolReconnectMaxDelayMs, 50, 300000, "BackFiller:RabbitMQ:PoolReconnectMaxDelayMs", failures);
        RequireRange(rabbit.MinimumConnectionLifetimeSeconds, 30, 86400, "BackFiller:RabbitMQ:MinimumConnectionLifetimeSeconds", failures);
        RequireRange(rabbit.PublishConfirmTimeoutSeconds, 1, 3600, "BackFiller:RabbitMQ:PublishConfirmTimeoutSeconds", failures);
        RequireRange(rabbit.MaximumShutdownDrainTimeoutSeconds, 1, 3600, "BackFiller:RabbitMQ:MaximumShutdownDrainTimeoutSeconds", failures);
        RequireRange(rabbit.UnhealthyThreshold, 1, 120, "BackFiller:RabbitMQ:UnhealthyThreshold", failures);
        RequireRange(rabbit.RequestedHeartbeatSeconds, 0, 3600, "BackFiller:RabbitMQ:RequestedHeartbeatSeconds", failures);
        RequireRange(rabbit.SocketTimeoutSeconds, 5, 600, "BackFiller:RabbitMQ:SocketTimeoutSeconds", failures);
        RequireRange(rabbit.RequestedChannelMax, 1, 65535, "BackFiller:RabbitMQ:RequestedChannelMax", failures);

        if (rabbit.MinConnections is { } min && rabbit.MaxConnections is { } max && min > max)
        {
            failures.Add("BackFiller:RabbitMQ:MinConnections must be less than or equal to MaxConnections.");
        }

        if (rabbit.PoolReconnectBaseDelayMs is { } baseDelay
            && rabbit.PoolReconnectMaxDelayMs is { } maxDelay
            && maxDelay < baseDelay)
        {
            failures.Add("BackFiller:RabbitMQ:PoolReconnectMaxDelayMs must be greater than or equal to PoolReconnectBaseDelayMs.");
        }

        if (rabbit.DegradedThreshold is null || rabbit.DegradedThreshold is <= 0d or > 1d)
        {
            failures.Add("BackFiller:RabbitMQ:DegradedThreshold must be greater than 0 and less than or equal to 1.");
        }

        if (rabbit.ConsumerPrefetchCount is 0)
        {
            failures.Add("BackFiller:RabbitMQ:ConsumerPrefetchCount must be between 1 and 65535.");
        }

        if (rabbit.MaxConnections is { } maxConnections
            && rabbit.RequestedChannelMax is { } channelMax
            && rabbit.ChannelPoolSize is { } poolSize)
        {
            try
            {
                var limit = checked(maxConnections * channelMax);
                if (poolSize > limit)
                {
                    failures.Add("BackFiller:RabbitMQ:ChannelPoolSize must be less than or equal to MaxConnections * RequestedChannelMax.");
                }
            }
            catch (OverflowException)
            {
                failures.Add("BackFiller:RabbitMQ:ChannelPoolSize: MaxConnections and RequestedChannelMax produce an invalid effective channel limit.");
            }
        }

        var grace = options.Shutdown?.GracePeriodSeconds ?? 0;
        if (grace > 0
            && rabbit.MaximumShutdownDrainTimeoutSeconds is { } drain
            && drain > grace)
        {
            failures.Add(
                "BackFiller:RabbitMQ:MaximumShutdownDrainTimeoutSeconds must be less than or equal to BackFiller:Shutdown:GracePeriodSeconds.");
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

    private static bool IsPlausibleEmail(string email)
    {
        var trimmed = email.Trim();
        if (trimmed.Length is 0 or > 254)
        {
            return false;
        }

        var at = trimmed.IndexOf('@');
        if (at <= 0 || at != trimmed.LastIndexOf('@') || at == trimmed.Length - 1)
        {
            return false;
        }

        var domain = trimmed[(at + 1)..];
        return domain.Contains('.', StringComparison.Ordinal)
               && BackFillerIdentity.IsValidDnsSuffix(BackFillerIdentity.CanonicalizeDnsSuffix(domain));
    }
}
