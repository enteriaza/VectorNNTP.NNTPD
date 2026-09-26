using RabbitMQ.Client;

namespace VectorNNTP.NNTPD.RabbitMq;

/// <summary>Adapts a RabbitMQ.Client channel to declare-only topology operations.</summary>
internal sealed class RabbitMqClientTopologyChannel : IRabbitMqTopologyChannel
{
    private readonly IChannel _channel;
    private int _disposed;

    /// <summary>Initializes a new wrapper around an opened broker channel.</summary>
    internal RabbitMqClientTopologyChannel(IChannel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        _channel = channel;
    }

    /// <inheritdoc />
    public Task ExchangeDeclareAsync(
        string exchange,
        string type,
        bool durable,
        bool autoDelete,
        IReadOnlyDictionary<string, object?>? arguments,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exchange);
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        return _channel.ExchangeDeclareAsync(
            exchange,
            type,
            durable,
            autoDelete,
            ToMutable(arguments),
            cancellationToken: cancellationToken);
    }

    /// <inheritdoc />
    public async Task QueueDeclareAsync(
        string queue,
        bool durable,
        bool exclusive,
        bool autoDelete,
        IReadOnlyDictionary<string, object?>? arguments,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queue);
        _ = await _channel.QueueDeclareAsync(
            queue,
            durable,
            exclusive,
            autoDelete,
            ToMutable(arguments),
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task QueueBindAsync(
        string queue,
        string exchange,
        string routingKey,
        IReadOnlyDictionary<string, object?>? arguments,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queue);
        ArgumentException.ThrowIfNullOrWhiteSpace(exchange);
        ArgumentNullException.ThrowIfNull(routingKey);
        return _channel.QueueBindAsync(
            queue,
            exchange,
            routingKey,
            ToMutable(arguments),
            cancellationToken: cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        await _channel.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Copies immutable argument dictionaries into the mutable shape expected by RabbitMQ.Client.
    /// </summary>
    private static IDictionary<string, object?>? ToMutable(IReadOnlyDictionary<string, object?>? arguments)
    {
        if (arguments is null)
        {
            return null;
        }

        return new Dictionary<string, object?>(arguments, StringComparer.Ordinal);
    }
}
