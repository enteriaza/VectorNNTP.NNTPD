using VectorNNTP.Common.Articles.Parsing;

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
            "SELECT maxartsize, sitename, prometheusurl FROM nntpsharedconfig LIMIT 2";

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
                rows.Add(new NntpSharedConfigurationCandidate(maxArticleBytes, siteName, prometheusUrl));
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

            return new NntpSharedConfiguration((int)row.MaxArticleBytes, row.SiteName!, row.PrometheusUrl);
        }
    }
}
