using VectorNNTP.Common.Articles.Parsing;
using VectorNNTP.Common.Configuration;

namespace VectorNNTP.Common.NntpDb
{
    /// <summary>
    /// Reads and validates one <c>nntpsharedconfig</c> row.
    /// </summary>
    /// <remarks>
    /// This type has no timer, background task, or application lifecycle.
    /// <c>maxartsize</c> is bytes. <c>prometheusurl</c> is stored and is not interpreted.
    /// </remarks>
    internal static class NntpSharedConfigurationReader
    {
        /// <summary>
        /// Selects at most two rows so a second row is visible. A singleton table is still invalid when empty or duplicated.
        /// </summary>
        internal const string SelectSql =
            "SELECT maxartsize, sitename, prometheusurl, acmedirectoryurl, acmerenewalthresholddays, cloudflarezoneid, dnssuffix, acmeaccount, acmecertpass, cloudflareapikey, rabbitmqpassword, rabbitmqusername FROM nntpsharedconfig LIMIT 2";

        /// <summary>Reads <c>nntpsharedconfig</c> from <paramref name="session"/> and validates the row.</summary>
        /// <param name="session">Open logical session.</param>
        /// <param name="cancellationToken">Token used to cancel the query.</param>
        /// <returns>The validated snapshot.</returns>
        /// <exception cref="InvalidOperationException">The row is missing, duplicated, or fails the audited contract.</exception>
        internal static async ValueTask<NntpSharedConfiguration> ReadAsync(
            INntpDbSession session,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(session);
            if (session is INntpSharedConfigurationRowSource source)
            {
                return Validate(await source.ReadCandidatesAsync(cancellationToken).ConfigureAwait(false));
            }

            await using var command = session.CreateCommand();
            command.CommandText = SelectSql;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var rows = new List<NntpSharedConfigurationCandidate>(2);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var maxArticleBytes = reader.IsDBNull(0) ? 0L : Convert.ToInt64(reader.GetValue(0));
                var siteName = reader.IsDBNull(1) ? null : reader.GetString(1);
                var prometheusUrl = reader.IsDBNull(2) ? null : reader.GetString(2);
                var acmeDirectoryUrl = reader.IsDBNull(3) ? null : reader.GetString(3);
                var acmeRenewalThresholdDays = reader.IsDBNull(4) ? 0L : Convert.ToInt64(reader.GetValue(4));
                var cloudFlareZoneId = reader.IsDBNull(5) ? null : reader.GetString(5);
                var dnsSuffix = reader.IsDBNull(6) ? null : reader.GetString(6);
                var acmeAccount = reader.IsDBNull(7) ? null : reader.GetString(7);
                var acmeCertificatePassword = reader.IsDBNull(8) ? null : reader.GetString(8);
                var cloudFlareApiKey = reader.IsDBNull(9) ? null : reader.GetString(9);
                var rabbitMqPassword = reader.IsDBNull(10) ? null : reader.GetString(10);
                var rabbitMqUsername = reader.IsDBNull(11) ? null : reader.GetString(11);
                rows.Add(new NntpSharedConfigurationCandidate(
                    maxArticleBytes,
                    siteName,
                    prometheusUrl,
                    acmeDirectoryUrl,
                    acmeRenewalThresholdDays,
                    cloudFlareZoneId,
                    dnsSuffix,
                    acmeAccount,
                    acmeCertificatePassword,
                    cloudFlareApiKey,
                    rabbitMqPassword,
                    rabbitMqUsername));
                if (rows.Count > 1)
                {
                    break;
                }
            }

            return Validate(rows);
        }

        /// <summary>Validates the audited <c>nntpsharedconfig</c> contract.</summary>
        /// <param name="rows">Candidates. Exactly one is required.</param>
        /// <returns>The validated snapshot.</returns>
        /// <exception cref="InvalidOperationException">The candidates are not one valid row.</exception>
        internal static NntpSharedConfiguration Validate(IReadOnlyList<NntpSharedConfigurationCandidate> rows)
        {
            ArgumentNullException.ThrowIfNull(rows);
            if (rows.Count == 0)
            {
                throw new InvalidOperationException("nntpsharedconfig has no row.");
            }

            if (rows.Count != 1)
            {
                throw new InvalidOperationException("nntpsharedconfig has more than one row.");
            }

            var row = rows[0];
            if (row.MaxArticleBytes <= 0 || row.MaxArticleBytes > int.MaxValue)
            {
                throw new InvalidOperationException("nntpsharedconfig.maxartsize must be a positive 32-bit byte count.");
            }

            if (!ArticlePathCanonicalizer.IsValidSiteName(row.SiteName))
            {
                throw new InvalidOperationException("nntpsharedconfig.sitename is not a valid Path component.");
            }

            if (string.IsNullOrWhiteSpace(row.AcmeDirectoryUrl)
                || !Uri.TryCreate(row.AcmeDirectoryUrl.Trim(), UriKind.Absolute, out var directoryUri)
                || (directoryUri.Scheme != Uri.UriSchemeHttp && directoryUri.Scheme != Uri.UriSchemeHttps))
            {
                throw new InvalidOperationException(
                    "nntpsharedconfig.acmedirectoryurl must be an absolute HTTP or HTTPS URL.");
            }

            if (row.AcmeRenewalThresholdDays is < 1 or > int.MaxValue)
            {
                throw new InvalidOperationException(
                    "nntpsharedconfig.acmerenewalthresholddays must be a positive 32-bit integer.");
            }

            if (string.IsNullOrWhiteSpace(row.CloudFlareZoneId))
            {
                throw new InvalidOperationException(
                    "nntpsharedconfig.cloudflarezoneid is required and cannot be empty.");
            }

            string dnsSuffix;
            try
            {
                dnsSuffix = ApplicationFqdn.CanonicalizeDnsSuffix(row.DnsSuffix!);
            }
            catch (ArgumentException)
            {
                throw new InvalidOperationException("nntpsharedconfig.dnssuffix is not a syntactically valid DNS name.");
            }

            if (!NntpdDnsName.IsValidSuffix(dnsSuffix) || dnsSuffix.Split('.').Length < 2)
            {
                throw new InvalidOperationException("nntpsharedconfig.dnssuffix is not a syntactically valid DNS name.");
            }

            var acmeAccount = RequireTrimmedCredential(
                row.AcmeAccount,
                "acmeaccount",
                NntpSharedConfigurationColumns.AcmeAccountMaximumLength);
            if (!AcmeCloudflareOptionsValidator.IsPlausibleEmail(acmeAccount))
            {
                throw new InvalidOperationException(
                    "nntpsharedconfig.acmeaccount must be a valid contact email address.");
            }

            var acmeCertificatePassword = RequireOpaqueCredential(
                row.AcmeCertificatePassword,
                "acmecertpass",
                NntpSharedConfigurationColumns.AcmeCertificatePasswordMaximumLength);
            var cloudFlareApiKey = RequireOpaqueCredential(
                row.CloudFlareApiKey,
                "cloudflareapikey",
                NntpSharedConfigurationColumns.CloudFlareApiKeyMaximumLength);
            var rabbitMqPassword = RequireOpaqueCredential(
                row.RabbitMqPassword,
                "rabbitmqpassword",
                NntpSharedConfigurationColumns.RabbitMqPasswordMaximumLength);
            var rabbitMqUsername = RequireTrimmedCredential(
                row.RabbitMqUsername,
                "rabbitmqusername",
                NntpSharedConfigurationColumns.RabbitMqUsernameMaximumLength);

            return new NntpSharedConfiguration(
                (int)row.MaxArticleBytes,
                row.SiteName!,
                row.PrometheusUrl,
                row.AcmeDirectoryUrl.Trim(),
                (int)row.AcmeRenewalThresholdDays,
                row.CloudFlareZoneId.Trim(),
                dnsSuffix,
                acmeAccount,
                acmeCertificatePassword,
                cloudFlareApiKey,
                rabbitMqPassword,
                rabbitMqUsername);
        }

        /// <summary>Rejects a blank credential and stores it without trimming.</summary>
        /// <param name="value">Column value. Null and white space are rejected.</param>
        /// <param name="column">Unqualified column name used in the exception. The value is never included.</param>
        /// <param name="maximumLength">Schema maximum length, inclusive.</param>
        /// <returns>The original value.</returns>
        private static string RequireOpaqueCredential(string? value, string column, int maximumLength)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException($"nntpsharedconfig.{column} is required and cannot be empty.");
            }

            if (value.Length > maximumLength)
            {
                throw new InvalidOperationException($"nntpsharedconfig.{column} exceeds {maximumLength} characters.");
            }

            return value;
        }

        /// <summary>Rejects a blank credential and stores the trimmed value.</summary>
        /// <param name="value">Column value. Null and white space are rejected.</param>
        /// <param name="column">Unqualified column name used in the exception. The value is never included.</param>
        /// <param name="maximumLength">Schema maximum length of the trimmed value, inclusive.</param>
        /// <returns>The trimmed value.</returns>
        private static string RequireTrimmedCredential(string? value, string column, int maximumLength)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException($"nntpsharedconfig.{column} is required and cannot be empty.");
            }

            var trimmed = value.Trim();
            if (trimmed.Length > maximumLength)
            {
                throw new InvalidOperationException($"nntpsharedconfig.{column} exceeds {maximumLength} characters.");
            }

            return trimmed;
        }
    }
}
