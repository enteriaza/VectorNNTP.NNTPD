using System.Net;
using MySqlConnector;
using VectorNNTP.Common.Messaging.RabbitMq;
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
    /// <param name="nntpDb">Validated shared NntpDB options.</param>
    /// <param name="contentRootPath">
    /// Application binary directory used to resolve relative
    /// <c>BackFiller:Logging:File:LogDir</c> and certificate paths.
    /// Production hosting passes <see cref="AppContext.BaseDirectory"/>. When omitted,
    /// relative paths resolve against <see cref="AppContext.BaseDirectory"/> rather than
    /// the process working directory or an IDE project content root.
    /// </param>
    /// <returns>Immutable snapshot.</returns>
    /// <exception cref="InvalidOperationException">Thrown when a required value is missing after validation.</exception>
    /// <remarks>
    /// The three-argument overload maps leftover BackFiller bind/certificate fields only when
    /// a shared <see cref="AcmeCloudflareOptions"/> instance is not supplied (tests).
    /// RabbitMQ defaults are used when <see cref="RabbitMqOptions"/> is omitted.
    /// </remarks>
    public static BackFillerRuntimeOptions Create(
        BackFillerOptions options,
        NntpDbOptions nntpDb,
        string? contentRootPath = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        var acme = new AcmeCloudflareOptions
        {
            BindAddress = options.BindAddress is { Length: > 0 } ? options.BindAddress : ["*"],
            BindPortTls = options.BindPortTls ?? 0,
            Fqdn = options.Fqdn,
            IncludeNewsHostnameInCertificate = false,
            AcmeDirectoryUrl = options.AcmeDirectoryUrl,
            AcmeRenewalThresholdDays = options.AcmeRenewalThresholdDays,
            AcmeCertificatePassword = string.Empty,
            AcmeStateDir = string.IsNullOrWhiteSpace(options.AcmeStateDir)
                ? options.CertificateDirectory
                : options.AcmeStateDir,
            CloudFlareApiKey = string.Empty,
            CloudFlareZoneId = string.Empty,
            DnsSuffix = options.DnsSuffix,
        };
        return Create(options, nntpDb, acme, new RabbitMqOptions(), contentRootPath);
    }

    /// <inheritdoc cref="Create(BackFillerOptions,NntpDbOptions,string?)"/>
    public static BackFillerRuntimeOptions Create(
        BackFillerOptions options,
        NntpDbOptions nntpDb,
        AcmeCloudflareOptions acme,
        string? contentRootPath = null) =>
        Create(options, nntpDb, acme, new RabbitMqOptions(), contentRootPath);

    /// <inheritdoc cref="Create(BackFillerOptions,NntpDbOptions,string?)"/>
    public static BackFillerRuntimeOptions Create(
        BackFillerOptions options,
        NntpDbOptions nntpDb,
        AcmeCloudflareOptions acme,
        RabbitMqOptions rabbitMq,
        string? contentRootPath = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(nntpDb);
        ArgumentNullException.ThrowIfNull(acme);
        ArgumentNullException.ThrowIfNull(rabbitMq);

        if (options.ServerId is not { } serverId || string.IsNullOrWhiteSpace(options.Fqdn))
        {
            throw new InvalidOperationException("Validated BackFiller identity is required to build runtime options.");
        }

        var dnsSuffix = ApplicationFqdn.CanonicalizeDnsSuffix(options.DnsSuffix);
        var fqdn = options.Fqdn;
        if (acme.BindPortTls is < 1 or > 65535)
        {
            throw new InvalidOperationException(
                "BindPortTls is required and must be 1–65535 because BackFiller is TLS-only. There is no cleartext fallback.");
        }

        var bindPortTls = acme.BindPortTls;

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

        if (string.IsNullOrWhiteSpace(nntpDb.ConnectionString))
        {
            throw new InvalidOperationException(
                $"ConnectionStrings:{NntpDbOptions.ConnectionStringName} must be configured.");
        }

        var nntpDbBuilder = new MySqlConnectionStringBuilder(nntpDb.ConnectionString);

        var retention = options.ArticleRetention ?? throw new InvalidOperationException("BackFiller:ArticleRetention is required.");
        var listener = options.Listener ?? throw new InvalidOperationException("BackFiller:Listener is required.");
        var shutdown = options.Shutdown ?? throw new InvalidOperationException("BackFiller:Shutdown is required.");

        return new BackFillerRuntimeOptions(
            ServerId: serverId,
            DnsSuffix: dnsSuffix,
            Fqdn: fqdn,
            BindAddressTokens: tokens,
            CanonicalBindAddresses: addresses,
            BindPortTls: bindPortTls,
            LogDirectory: ResolveFileLogDirectory(options, contentRootPath),
            CertificateDirectory: ApplicationLocalPath.ResolveApplicationLocalPath(acme.AcmeStateDir, contentRootPath),
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
                TimeSpan.FromSeconds(retention.SweepIntervalSeconds),
                retention.MaxOpenableRequestIdsPerArticle),
            RabbitMq: new BackFillerRabbitMqRuntimeOptions(
                WorkRequestMaxPayloadBytes: rabbitMq.WorkRequestMaxPayloadBytes ?? 1024,
                PublishConfirmTimeoutSeconds: rabbitMq.PublishConfirmTimeoutSeconds ?? 10,
                ConsumerPrefetchCount: rabbitMq.ConsumerPrefetchCount),
            CertificateDomainNames: CertificateIdentities.ForFqdn(fqdn, acme.IncludeNewsHostnameInCertificate),
            CertificatePassword: acme.AcmeCertificatePassword,
            NntpDb: new NntpDbRuntimeOptions(
                nntpDb.ConnectionString.Trim(),
                nntpDbBuilder.Server ?? string.Empty,
                nntpDbBuilder.Database ?? string.Empty,
                nntpDbBuilder.UserID ?? string.Empty),
            AccountRefreshInterval: TimeSpan.FromSeconds(options.BackFillerAccountRefreshIntervalSeconds));
    }

    private static string ResolveFileLogDirectory(BackFillerOptions options, string? contentRootPath)
    {
        var file = options.Logging?.File ?? new BackFillerFileLoggingTargetOptions();
        if (string.IsNullOrWhiteSpace(file.LogDir))
        {
            return string.Empty;
        }

        return ApplicationLocalPath.ResolveApplicationLocalPath(file.LogDir, contentRootPath);
    }
}
