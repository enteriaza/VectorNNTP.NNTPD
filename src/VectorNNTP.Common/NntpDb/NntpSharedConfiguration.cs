namespace VectorNNTP.Common.NntpDb
{
    /// <summary>
    /// One immutable <c>nntpsharedconfig</c> row. Values are the database values; bytes are not converted.
    /// </summary>
    /// <param name="MaxArticleBytes">Positive <c>maxartsize</c> in bytes.</param>
    /// <param name="SiteName">Path tracker component from <c>sitename</c>.</param>
    /// <param name="PrometheusUrl"><c>prometheusurl</c>, or <see langword="null"/> when the column is null. Stored only.</param>
    internal readonly record struct NntpSharedConfiguration(
        int MaxArticleBytes,
        string SiteName,
        string? PrometheusUrl);

    /// <summary>One unread <c>nntpsharedconfig</c> candidate before validation.</summary>
    /// <param name="MaxArticleBytes">Raw <c>maxartsize</c> before the positive-int check.</param>
    /// <param name="SiteName">Raw <c>sitename</c>, or <see langword="null"/> when the column is null.</param>
    /// <param name="PrometheusUrl">Raw <c>prometheusurl</c>, or <see langword="null"/> when the column is null.</param>
    internal readonly record struct NntpSharedConfigurationCandidate(
        long MaxArticleBytes,
        string? SiteName,
        string? PrometheusUrl);

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
