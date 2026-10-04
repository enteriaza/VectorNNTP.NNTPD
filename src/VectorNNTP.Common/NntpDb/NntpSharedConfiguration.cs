using VectorNNTP.Common.Configuration;
using VectorNNTP.Common.Messaging.RabbitMq;

namespace VectorNNTP.Common.NntpDb
{
    /// <summary>
    /// One immutable <c>nntpsharedconfig</c> row. Values are the database values; bytes are not converted.
    /// </summary>
    /// <param name="MaxArticleBytes">Positive <c>maxartsize</c> in bytes.</param>
    /// <param name="SiteName">Path tracker component from <c>sitename</c>.</param>
    /// <param name="PrometheusUrl"><c>prometheusurl</c>, or <see langword="null"/> when the column is null. Stored only.</param>
    /// <param name="AcmeDirectoryUrl">Absolute HTTP or HTTPS <c>acmedirectoryurl</c>.</param>
    /// <param name="AcmeRenewalThresholdDays">Positive <c>acmerenewalthresholddays</c>.</param>
    /// <param name="CloudFlareZoneId">Non-empty <c>cloudflarezoneid</c>.</param>
    /// <param name="DnsSuffix">Canonical <c>dnssuffix</c>.</param>
    /// <param name="AcmeAccount">Trimmed <c>acmeaccount</c> contact email.</param>
    /// <param name="AcmeCertificatePassword"><c>acmecertpass</c>. Secret. Never log.</param>
    /// <param name="CloudFlareApiKey"><c>cloudflareapikey</c>. Secret. Never log.</param>
    /// <param name="RabbitMqPassword"><c>rabbitmqpassword</c>. Secret. Never log.</param>
    /// <param name="RabbitMqUsername">Trimmed <c>rabbitmqusername</c>.</param>
    /// <remarks>
    /// NNTPD and BackFiller publish <paramref name="AcmeAccount"/>, <paramref name="AcmeCertificatePassword"/>,
    /// <paramref name="CloudFlareApiKey"/>, <paramref name="RabbitMqUsername"/>, and <paramref name="RabbitMqPassword"/>
    /// from this row. Do not log those five values.
    /// </remarks>
    internal readonly record struct NntpSharedConfiguration(
        int MaxArticleBytes,
        string SiteName,
        string? PrometheusUrl,
        string AcmeDirectoryUrl,
        int AcmeRenewalThresholdDays,
        string CloudFlareZoneId,
        string DnsSuffix,
        string AcmeAccount = NntpSharedConfigurationColumns.PlaceholderAcmeAccount,
        string AcmeCertificatePassword = NntpSharedConfigurationColumns.PlaceholderAcmeCertificatePassword,
        string CloudFlareApiKey = NntpSharedConfigurationColumns.PlaceholderCloudFlareApiKey,
        string RabbitMqPassword = NntpSharedConfigurationColumns.PlaceholderRabbitMqPassword,
        string RabbitMqUsername = NntpSharedConfigurationColumns.PlaceholderRabbitMqUsername)
    {
        /// <summary>
        /// Copies the ACME directory, renewal threshold, account email, certificate password,
        /// Cloudflare API key, zone id, and DNS suffix onto <paramref name="options"/>.
        /// </summary>
        /// <param name="options">Options instance that ACME and Cloudflare read.</param>
        internal void CopyAcmeDnsTo(AcmeCloudflareOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            options.AcmeDirectoryUrl = AcmeDirectoryUrl;
            options.AcmeRenewalThresholdDays = AcmeRenewalThresholdDays;
            options.AcmeEmail = AcmeAccount;
            options.AcmeCertificatePassword = AcmeCertificatePassword;
            options.CloudFlareApiKey = CloudFlareApiKey;
            options.CloudFlareZoneId = CloudFlareZoneId;
            options.DnsSuffix = DnsSuffix;
        }

        /// <summary>Copies the broker username and password onto <paramref name="options"/>.</summary>
        /// <param name="options">RabbitMQ options read by the connection factory and Management client.</param>
        /// <remarks>Does not change hosts, virtual host, TLS, or Management settings.</remarks>
        internal void CopyRabbitMqCredentialsTo(RabbitMqOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            options.Username = RabbitMqUsername;
            options.Password = RabbitMqPassword;
        }

        /// <summary>Builds <c>{prefix}{serverId:00}.{DnsSuffix}</c> and rejects a name that is not a DNS FQDN of at most 253 characters.</summary>
        /// <param name="prefix">Fixed application prefix.</param>
        /// <param name="serverId">Validated server id.</param>
        /// <returns>The application FQDN for this snapshot.</returns>
        /// <exception cref="InvalidOperationException"><see cref="DnsSuffix"/> does not produce a usable FQDN.</exception>
        internal string RequireApplicationFqdn(string prefix, int serverId)
        {
            var fqdn = ApplicationFqdn.Build(prefix, serverId, DnsSuffix);
            if (fqdn.Length > ApplicationFqdn.MaximumLength || Uri.CheckHostName(fqdn) != UriHostNameType.Dns)
            {
                throw new InvalidOperationException("nntpsharedconfig.dnssuffix produces an invalid application FQDN.");
            }

            return fqdn;
        }
    }

    /// <summary>One unread <c>nntpsharedconfig</c> candidate before validation.</summary>
    /// <param name="MaxArticleBytes">Raw <c>maxartsize</c> before the positive-int check.</param>
    /// <param name="SiteName">Raw <c>sitename</c>, or <see langword="null"/> when the column is null.</param>
    /// <param name="PrometheusUrl">Raw <c>prometheusurl</c>, or <see langword="null"/> when the column is null.</param>
    /// <param name="AcmeDirectoryUrl">Raw <c>acmedirectoryurl</c>, or <see langword="null"/> when the column is null.</param>
    /// <param name="AcmeRenewalThresholdDays">Raw <c>acmerenewalthresholddays</c> before the positive-int check.</param>
    /// <param name="CloudFlareZoneId">Raw <c>cloudflarezoneid</c>, or <see langword="null"/> when the column is null.</param>
    /// <param name="DnsSuffix">Raw <c>dnssuffix</c>, or <see langword="null"/> when the column is null.</param>
    /// <param name="AcmeAccount">Raw <c>acmeaccount</c>, or <see langword="null"/> when the column is null.</param>
    /// <param name="AcmeCertificatePassword">Raw <c>acmecertpass</c>, or <see langword="null"/> when the column is null. Secret.</param>
    /// <param name="CloudFlareApiKey">Raw <c>cloudflareapikey</c>, or <see langword="null"/> when the column is null. Secret.</param>
    /// <param name="RabbitMqPassword">Raw <c>rabbitmqpassword</c>, or <see langword="null"/> when the column is null. Secret.</param>
    /// <param name="RabbitMqUsername">Raw <c>rabbitmqusername</c>, or <see langword="null"/> when the column is null.</param>
    internal readonly record struct NntpSharedConfigurationCandidate(
        long MaxArticleBytes,
        string? SiteName,
        string? PrometheusUrl,
        string? AcmeDirectoryUrl,
        long AcmeRenewalThresholdDays,
        string? CloudFlareZoneId,
        string? DnsSuffix,
        string? AcmeAccount = NntpSharedConfigurationColumns.PlaceholderAcmeAccount,
        string? AcmeCertificatePassword = NntpSharedConfigurationColumns.PlaceholderAcmeCertificatePassword,
        string? CloudFlareApiKey = NntpSharedConfigurationColumns.PlaceholderCloudFlareApiKey,
        string? RabbitMqPassword = NntpSharedConfigurationColumns.PlaceholderRabbitMqPassword,
        string? RabbitMqUsername = NntpSharedConfigurationColumns.PlaceholderRabbitMqUsername);

    /// <summary>Schema lengths and non-secret placeholders for omitted candidate fields.</summary>
    /// <remarks>
    /// Placeholders exist so callers that construct a candidate without the credential columns still
    /// satisfy validation. The SQL reader always passes the database values, including null.
    /// </remarks>
    internal static class NntpSharedConfigurationColumns
    {
        /// <summary>Maximum <c>acmeaccount</c> length.</summary>
        internal const int AcmeAccountMaximumLength = 45;

        /// <summary>Maximum <c>acmecertpass</c> length.</summary>
        internal const int AcmeCertificatePasswordMaximumLength = 32;

        /// <summary>Maximum <c>cloudflareapikey</c> length.</summary>
        internal const int CloudFlareApiKeyMaximumLength = 45;

        /// <summary>Maximum <c>rabbitmqpassword</c> length.</summary>
        internal const int RabbitMqPasswordMaximumLength = 45;

        /// <summary>Maximum <c>rabbitmqusername</c> length.</summary>
        internal const int RabbitMqUsernameMaximumLength = 45;

        /// <summary>Non-secret stand-in used when a candidate omits <c>acmeaccount</c>.</summary>
        internal const string PlaceholderAcmeAccount = "ops@example.test";

        /// <summary>Non-secret stand-in used when a candidate omits <c>acmecertpass</c>.</summary>
        internal const string PlaceholderAcmeCertificatePassword = "pfx-placeholder";

        /// <summary>Non-secret stand-in used when a candidate omits <c>cloudflareapikey</c>.</summary>
        internal const string PlaceholderCloudFlareApiKey = "cf-api-key-placeholder";

        /// <summary>Non-secret stand-in used when a candidate omits <c>rabbitmqpassword</c>.</summary>
        internal const string PlaceholderRabbitMqPassword = "rmq-password-placeholder";

        /// <summary>Non-secret stand-in used when a candidate omits <c>rabbitmqusername</c>.</summary>
        internal const string PlaceholderRabbitMqUsername = "rmq-user";
    }

    /// <summary>
    /// Supplies candidate rows when the session is not a provider connection.
    /// Production MySQL sessions do not implement this; the reader executes the query itself.
    /// </summary>
    internal interface INntpSharedConfigurationRowSource
    {
        /// <summary>Returns every candidate the reader should validate, at most two.</summary>
        /// <param name="cancellationToken">Token used to cancel the read.</param>
        /// <returns>Zero, one, or two candidates. A second candidate makes the configuration invalid.</returns>
        ValueTask<IReadOnlyList<NntpSharedConfigurationCandidate>> ReadCandidatesAsync(
            CancellationToken cancellationToken);
    }

    /// <summary>Published <see cref="NntpSharedConfiguration"/> used by one application.</summary>
    internal interface INntpSharedConfigurationCatalogue
    {
        /// <summary>Gets the last published snapshot.</summary>
        /// <exception cref="InvalidOperationException">No snapshot has been published.</exception>
        NntpSharedConfiguration Current { get; }
    }
}
