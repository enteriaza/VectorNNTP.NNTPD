namespace VectorNNTP.NNTPD.RabbitMq;

/// <summary>Source-generated RabbitMQ topology log messages. Never includes credentials.</summary>
internal static partial class RabbitMqTopologyLogMessages
{
    [LoggerMessage(
        EventId = 2820,
        Level = LogLevel.Information,
        Message = "Establishing article-retrieval topology (providers={ProviderCount})")]
    public static partial void Establishing(ILogger logger, int ProviderCount);

    [LoggerMessage(
        EventId = 2821,
        Level = LogLevel.Information,
        Message = "Article-retrieval topology established (providers={ProviderCount}, generation={Generation})")]
    public static partial void Established(ILogger logger, int ProviderCount, long Generation);

    [LoggerMessage(
        EventId = 2822,
        Level = LogLevel.Error,
        Message = "Article-retrieval topology declaration failed (provider={Provider}, exchange={Exchange}, queue={Queue})")]
    public static partial void DeclarationFailed(
        ILogger logger,
        Exception exception,
        string Provider,
        string Exchange,
        string Queue);

    [LoggerMessage(
        EventId = 2823,
        Level = LogLevel.Error,
        Message = "RabbitMQ connection is not ready for topology declaration")]
    public static partial void ConnectionNotReady(ILogger logger);
}
