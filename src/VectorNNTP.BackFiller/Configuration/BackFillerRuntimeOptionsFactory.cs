using System.Net;
using VectorNNTP.NNTPD.Acme;
using VectorNNTP.NNTPD.Configuration;

namespace VectorNNTP.BackFiller.Configuration;

/// <summary>
/// Builds <see cref="BackFillerRuntimeOptions"/> from already-validated bindable options.
/// </summary>
public static class BackFillerRuntimeOptionsFactory
{
    /// <summary>
    /// Projects validated options into the immutable runtime snapshot.
    /// </summary>
    /// <param name="options">Validated bindable options.</param>
    /// <param name="connectionStrings">Validated connection-string options.</param>
    /// <param name="contentRootPath">
    /// Host content root used to resolve relative <see cref="BackFillerOptions.LogDirectory"/>
    /// and <see cref="BackFillerOptions.CertificateDirectory"/> values. When omitted, relative
    /// paths resolve against <see cref="AppContext.BaseDirectory"/> rather than the process
    /// working directory.
    /// </param>
    /// <returns>Immutable snapshot.</returns>
    /// <exception cref="InvalidOperationException">Thrown when a required value is missing after validation.</exception>
    /// <remarks>
    /// The three-argument overload maps leftover BackFiller bind/certificate fields only when
    /// a shared <see cref="AcmeCloudflareOptions"/> instance is not supplied (tests).
    /// </remarks>
    public static BackFillerRuntimeOptions Create(
        BackFillerOptions options,
        BackFillerConnectionStringsOptions connectionStrings,
        string? contentRootPath = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        var acme = new AcmeCloudflareOptions
        {
            BindAddress = options.BindAddress is { Length: > 0 } ? options.BindAddress : ["*"],
            BindPort = options.BindPort ?? 1190,
            BindPortTls = options.BindPort ?? 1190,
            Fqdn = options.Fqdn,
            IncludeNewsHostnameInCertificate = false,
            AcmeEmail = "security@usenet.ninja",
            AcmeCertificatePassword = string.Empty,
            AcmeStateDir = options.CertificateDirectory,
            CloudFlareApiKey = string.Empty,
            CloudFlareZoneId = string.Empty,
            DnsSuffix = options.DnsSuffix,
        };
        return Create(options, connectionStrings, acme, contentRootPath);
    }

    /// <inheritdoc cref="Create(BackFillerOptions,BackFillerConnectionStringsOptions,string?)"/>
    public static BackFillerRuntimeOptions Create(
        BackFillerOptions options,
        BackFillerConnectionStringsOptions connectionStrings,
        AcmeCloudflareOptions acme,
        string? contentRootPath = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(connectionStrings);
        ArgumentNullException.ThrowIfNull(acme);

        if (string.IsNullOrWhiteSpace(options.Name) || options.ServerId is not { } serverId)
        {
            throw new InvalidOperationException("Validated BackFiller identity is required to build runtime options.");
        }

        var name = BackFillerIdentity.CanonicalizeName(options.Name);
        var dnsSuffix = BackFillerIdentity.CanonicalizeDnsSuffix(options.DnsSuffix);
        var fqdn = BackFillerIdentity.BuildFqdn(name, serverId, dnsSuffix);
        if (acme.BindPortTls is < 1 or > 65535)
        {
            throw new InvalidOperationException(
                "BindPortTls is required and must be 1–65535 because BackFiller is TLS-only. There is no cleartext fallback.");
        }

        var bindPort = acme.BindPortTls;

        var tokens = (acme.BindAddress ?? [])
            .Where(static x => !string.IsNullOrWhiteSpace(x))
            .Select(static x => x.Trim())
            .ToArray();

        var addresses = new List<IPAddress>();
        foreach (var token in tokens)
        {
            if (AcmeCloudflareOptions.IsBindAddressWildcard(token))
            {
                continue;
            }

            if (IPAddress.TryParse(token, out var address))
            {
                addresses.Add(address);
            }
        }

        var grabberDbValue = connectionStrings.GrabberDB;
        if (!GrabberDbConnectionString.TryParse(grabberDbValue, out var server, out var database, out var userId, out var reason))
        {
            throw new InvalidOperationException(reason);
        }

        var rabbit = options.RabbitMQ ?? throw new InvalidOperationException("BackFiller:RabbitMQ is required.");
        var hosts = (rabbit.Hosts ?? [])
            .Where(static x => !string.IsNullOrWhiteSpace(x))
            .Select(static x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var retention = options.ArticleRetention ?? throw new InvalidOperationException("BackFiller:ArticleRetention is required.");
        var listener = options.Listener ?? throw new InvalidOperationException("BackFiller:Listener is required.");
        var shutdown = options.Shutdown ?? throw new InvalidOperationException("BackFiller:Shutdown is required.");
        var transit = options.TransitServer ?? throw new InvalidOperationException("BackFiller:TransitServer is required.");
        var accounts = options.Accounts ?? throw new InvalidOperationException("BackFiller:Accounts is required.");

        return new BackFillerRuntimeOptions(
            Name: name,
            ServerId: serverId,
            DnsSuffix: dnsSuffix,
            Fqdn: fqdn,
            BindAddressTokens: tokens,
            CanonicalBindAddresses: addresses,
            BindPort: bindPort,
            LogDirectory: ResolveConfiguredDirectory(options.LogDirectory, contentRootPath),
            CertificateDirectory: ResolveConfiguredDirectory(acme.AcmeStateDir, contentRootPath),
            Shutdown: new BackFillerShutdownRuntimeOptions(
                TimeSpan.FromSeconds(shutdown.GracePeriodSeconds),
                shutdown.DrainQueuedWork,
                shutdown.FinishActiveArticles),
            Listener: new BackFillerListenerRuntimeOptions(
                listener.ParserAccumulationMaxBytes,
                TimeSpan.FromSeconds(listener.TlsHandshakeTimeoutSeconds),
                TimeSpan.FromSeconds(listener.IoProgressTimeoutSeconds),
                TimeSpan.FromSeconds(listener.AwaitingReceiptAckTimeoutSeconds),
                listener.MaxQueuedFoundPayloadBytes,
                listener.MaxActiveConnections),
            ArticleRetention: new BackFillerArticleRetentionRuntimeOptions(
                checked(retention.MaximumRetainedPayloadGigabytes * BackFillerArticleRetentionOptions.BytesPerGibibyte),
                TimeSpan.FromSeconds(retention.RetentionTtlSeconds),
                TimeSpan.FromSeconds(retention.SweepIntervalSeconds)),
            TransitServer: new BackFillerTransitServerRuntimeOptions(
                transit.Host.Trim(),
                transit.Port,
                transit.UseSsl),
            RabbitMq: new BackFillerRabbitMqRuntimeOptions(
                Hosts: hosts,
                Port: rabbit.Port ?? 0,
                Username: NullIfWhiteSpace(rabbit.Username),
                Password: rabbit.Password,
                VirtualHost: string.IsNullOrWhiteSpace(rabbit.VirtualHost) ? "/" : rabbit.VirtualHost.Trim(),
                EnableSsl: rabbit.EnableSsl ?? true,
                WorkRequestMaxPayloadBytes: rabbit.WorkRequestMaxPayloadBytes ?? 1024,
                ChannelLeaseTimeoutSeconds: rabbit.ChannelLeaseTimeoutSeconds ?? 60,
                RpcTimeoutSeconds: rabbit.RpcTimeoutSeconds ?? 30,
                ConnectionBlockedTimeoutSeconds: rabbit.ConnectionBlockedTimeoutSeconds ?? 30,
                ChannelPoolSize: rabbit.ChannelPoolSize ?? 512,
                MinConnections: rabbit.MinConnections ?? 4,
                MaxConnections: rabbit.MaxConnections ?? 16,
                MaxConsecutiveRecoveryFailures: rabbit.MaxConsecutiveRecoveryFailures ?? 5,
                MaxPendingLeaseWaiters: rabbit.MaxPendingLeaseWaiters ?? 1024,
                ConnectionScaleDownIdleSeconds: rabbit.ConnectionScaleDownIdleSeconds ?? 300,
                ScaleDownCooldownSeconds: rabbit.ScaleDownCooldownSeconds ?? 30,
                NetworkRecoveryIntervalSeconds: rabbit.NetworkRecoveryIntervalSeconds ?? 5,
                PoolReconnectBaseDelayMs: rabbit.PoolReconnectBaseDelayMs ?? 250,
                PoolReconnectMaxDelayMs: rabbit.PoolReconnectMaxDelayMs ?? 30000,
                MinimumConnectionLifetimeSeconds: rabbit.MinimumConnectionLifetimeSeconds ?? 300,
                PublishConfirmTimeoutSeconds: rabbit.PublishConfirmTimeoutSeconds ?? 10,
                MaximumShutdownDrainTimeoutSeconds: rabbit.MaximumShutdownDrainTimeoutSeconds ?? 30,
                DegradedThreshold: rabbit.DegradedThreshold ?? 0.75,
                UnhealthyThreshold: rabbit.UnhealthyThreshold ?? 5,
                RequestedHeartbeatSeconds: rabbit.RequestedHeartbeatSeconds ?? 60,
                SocketTimeoutSeconds: rabbit.SocketTimeoutSeconds ?? 30,
                RequestedChannelMax: rabbit.RequestedChannelMax ?? 2047,
                ConsumerPrefetchCount: rabbit.ConsumerPrefetchCount,
                DiagnosticPayloadCorrelationId: NullIfWhiteSpace(rabbit.DiagnosticPayloadCorrelationId)),
            CertificateDomainNames: CertificateIdentitiesForRuntime(acme),
            CertificatePassword: acme.AcmeCertificatePassword,
            GrabberDb: new GrabberDbRuntimeOptions(
                grabberDbValue!.Trim(),
                server!,
                database!,
                userId!),
            Accounts: new BackFillerAccountsRuntimeOptions(
                TimeSpan.FromSeconds(accounts.RefreshIntervalSeconds),
                TimeSpan.FromSeconds(accounts.CommandTimeoutSeconds)));
    }

    private static IReadOnlyList<string> CertificateIdentitiesForRuntime(AcmeCloudflareOptions acme)
    {
        if (string.IsNullOrWhiteSpace(acme.Fqdn))
        {
            return [];
        }

        return CertificateIdentities.ForFqdn(acme.Fqdn, acme.IncludeNewsHostnameInCertificate);
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// Resolves a configured directory against the host content root.
    /// Absolute paths stay absolute. Relative paths do not follow
    /// <see cref="Environment.CurrentDirectory"/>.
    /// </summary>
    /// <param name="path">Configured directory.</param>
    /// <param name="contentRootPath">Host content root, or <see langword="null"/> for the application base directory.</param>
    /// <returns>A fully qualified directory path.</returns>
    internal static string ResolveConfiguredDirectory(string path, string? contentRootPath)
    {
        var trimmed = path.Trim();
        var root = string.IsNullOrWhiteSpace(contentRootPath)
            ? AppContext.BaseDirectory
            : contentRootPath;
        return Path.GetFullPath(trimmed, Path.GetFullPath(root));
    }
}
