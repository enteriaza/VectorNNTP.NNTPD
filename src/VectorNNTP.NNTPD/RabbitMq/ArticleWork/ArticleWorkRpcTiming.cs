namespace VectorNNTP.NNTPD.RabbitMq.ArticleWork;

/// <summary>Fixed article-work RPC deadlines. These are not configuration keys.</summary>
internal static class ArticleWorkRpcTiming
{
    /// <summary>
    /// Storage-to-provider fan-out grace, measured from lookup start. Not a response-wait window.
    /// </summary>
    internal static readonly TimeSpan StorageGrace = TimeSpan.FromMilliseconds(500);

    /// <summary>Operational article-not-found deadline measured from the original request start.</summary>
    internal static readonly TimeSpan LookupDeadline = TimeSpan.FromSeconds(5);

    /// <summary>Absolute safety ceiling for a single RPC operation.</summary>
    internal static readonly TimeSpan AbsoluteLifetime = TimeSpan.FromSeconds(60);
}
