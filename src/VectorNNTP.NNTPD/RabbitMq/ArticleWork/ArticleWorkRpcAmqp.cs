using System.Text;

namespace VectorNNTP.NNTPD.RabbitMq.ArticleWork;

/// <summary>AMQP transport constants for article-work RPC publications.</summary>
internal static class ArticleWorkRpcAmqp
{
    /// <summary>AMQP header/property name carrying the logical lookup UUID.</summary>
    internal const string RequestIdPropertyName = "RequestId";

    /// <summary>
    /// AMQP <c>Expiration</c> in milliseconds for both request and response messages.
    /// The broker expires undelivered or leftover messages. This is not an end-to-end
    /// article-retrieval deadline. NNTPD does not delete or purge queues.
    /// </summary>
    internal const string ExpirationMilliseconds = "1000";

    /// <summary>Creates the AMQP header table that carries <see cref="RequestIdPropertyName"/>.</summary>
    internal static Dictionary<string, object?> CreateRequestIdHeaders(string requestId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [RequestIdPropertyName] = requestId,
        };
    }

    /// <summary>Reads the logical lookup UUID from AMQP headers, if present.</summary>
    internal static string? ReadRequestId(IDictionary<string, object?>? headers)
    {
        if (headers is null
            || !headers.TryGetValue(RequestIdPropertyName, out var value)
            || value is null)
        {
            return null;
        }

        return value switch
        {
            string text => text,
            byte[] bytes => Encoding.UTF8.GetString(bytes),
            ReadOnlyMemory<byte> memory => Encoding.UTF8.GetString(memory.Span),
            _ => null,
        };
    }
}
