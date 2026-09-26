using System.Text;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace VectorNNTP.BackFiller.RabbitMq;

/// <summary>Owns one RabbitMQ.Client channel. Does not own the parent connection.</summary>
internal sealed class BackFillerRabbitMqClientChannel : IBackFillerRabbitMqChannel
{
    private readonly IChannel _channel;
    private AsyncEventingBasicConsumer? _consumer;
    private Func<BackFillerRabbitMqConsumedDelivery, Task>? _onDelivery;
    private int _disposed;

    /// <summary>
    /// Initializes a new wrapper around an opened channel.
    /// </summary>
    /// <param name="channel">Opened RabbitMQ.Client channel.</param>
    /// <param name="generation">Connection generation the channel belongs to.</param>
    internal BackFillerRabbitMqClientChannel(IChannel channel, long generation)
    {
        ArgumentNullException.ThrowIfNull(channel);
        _channel = channel;
        Generation = generation;
    }

    /// <inheritdoc />
    public long Generation { get; }

    /// <inheritdoc />
    public bool IsOpen => _channel.IsOpen;

    /// <inheritdoc />
    public async Task<string> BasicConsumeAsync(
        string queue,
        ushort prefetchCount,
        Func<BackFillerRabbitMqConsumedDelivery, Task> onDelivery,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queue);
        ArgumentNullException.ThrowIfNull(onDelivery);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
        if (!_channel.IsOpen)
        {
            throw new InvalidOperationException("RabbitMQ channel is not open for consume.");
        }

        _onDelivery = onDelivery;
        await _channel.BasicQosAsync(0, prefetchCount, global: false, cancellationToken).ConfigureAwait(false);
        var consumer = new AsyncEventingBasicConsumer(_channel);
        consumer.ReceivedAsync += OnReceivedAsync;
        _consumer = consumer;
        return await _channel
            .BasicConsumeAsync(queue, autoAck: false, consumer, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task BasicCancelAsync(string consumerTag, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(consumerTag);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
        return _channel.BasicCancelAsync(consumerTag, cancellationToken: cancellationToken);
    }

    /// <inheritdoc />
    public Task BasicAckAsync(ulong deliveryTag, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
        return _channel.BasicAckAsync(deliveryTag, multiple: false, cancellationToken).AsTask();
    }

    /// <inheritdoc />
    public Task BasicNackAsync(ulong deliveryTag, bool requeue, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
        return _channel.BasicNackAsync(deliveryTag, multiple: false, requeue, cancellationToken).AsTask();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        if (_consumer is not null)
        {
            _consumer.ReceivedAsync -= OnReceivedAsync;
            _consumer = null;
        }

        _onDelivery = null;

        try
        {
            if (_channel.IsOpen)
            {
                await _channel.CloseAsync().ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
        }

        await _channel.DisposeAsync().ConfigureAwait(false);
    }

    private async Task OnReceivedAsync(object sender, BasicDeliverEventArgs eventArgs)
    {
        var handler = _onDelivery;
        if (handler is null)
        {
            return;
        }

        var delivery = new BackFillerRabbitMqConsumedDelivery(
            eventArgs.DeliveryTag,
            eventArgs.Body,
            eventArgs.BasicProperties.CorrelationId,
            eventArgs.BasicProperties.ReplyTo,
            eventArgs.BasicProperties.ContentType,
            ReadRequestId(eventArgs.BasicProperties.Headers),
            eventArgs.Redelivered,
            eventArgs.RoutingKey,
            eventArgs.Exchange,
            eventArgs.ConsumerTag,
            Generation);
        await handler(delivery).ConfigureAwait(false);
    }

    private static string? ReadRequestId(IDictionary<string, object?>? headers)
    {
        if (headers is null
            || !headers.TryGetValue("RequestId", out var value)
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
