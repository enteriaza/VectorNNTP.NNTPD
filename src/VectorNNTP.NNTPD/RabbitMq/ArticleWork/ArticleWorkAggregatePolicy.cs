namespace VectorNNTP.NNTPD.RabbitMq.ArticleWork;

/// <summary>
/// NNTPD aggregate policy for multi-source article-work RPC.
/// </summary>
/// <remarks>
/// <para>
/// BackFiller v1 outcomes classify a single work item. They do not define how NNTPD
/// combines storage plus twelve providers into one lookup result.
/// </para>
/// <para>
/// Aggregate policy:
/// <list type="bullet">
/// <item>
/// <description>
/// <see cref="ArticleWorkOutcome.Success"/> is the only aggregate-terminal source
/// outcome. The first valid Success for the current logical <c>RequestId</c> completes
/// the lookup immediately (first-response-wins). No grace timer, provider wait, or
/// deadline wait follows.
/// </description>
/// </item>
/// <item>
/// <description>
/// <see cref="ArticleWorkOutcome.ArticleNotFound"/> is source-local. The source did
/// not have the article. The response is processed immediately and does not complete
/// the aggregate lookup.
/// </description>
/// </item>
/// <item>
/// <description>
/// <see cref="ArticleWorkOutcome.InvalidArticle"/> is source-local. One destination
/// rejected the article; another destination may still succeed. The response is
/// processed immediately and does not complete the aggregate lookup.
/// </description>
/// </item>
/// <item>
/// <description>
/// <see cref="ArticleWorkOutcome.InvalidRequest"/> is source-local. A protocol
/// rejection from one destination must not suppress valid responses from others.
/// The response is processed immediately and does not complete the aggregate lookup.
/// </description>
/// </item>
/// </list>
/// </para>
/// <para>
/// The aggregate result is <see cref="ArticleWorkOutcome.ArticleNotFound"/> only when
/// the 5-second lookup deadline elapses without Success (or the 60-second safety
/// ceiling fires). The 500ms value is only the storage-to-provider fan-out grace. It
/// is not a response-wait or qualification window.
/// </para>
/// </remarks>
internal static class ArticleWorkAggregatePolicy
{
    /// <summary>Returns whether this source outcome completes the whole lookup.</summary>
    internal static bool IsAggregateTerminal(ArticleWorkOutcome outcome) =>
        outcome == ArticleWorkOutcome.Success;
}
