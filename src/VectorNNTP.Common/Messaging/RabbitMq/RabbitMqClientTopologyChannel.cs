using RabbitMQ.Client;

namespace VectorNNTP.Common.Messaging.RabbitMq
{
    /// <summary>Adapts a RabbitMQ.Client channel to declare-only topology operations.</summary>
    internal sealed class RabbitMqClientTopologyChannel : IRabbitMqTopologyChannel
    {
        /// <summary>
        /// Owned declare-only channel. <see cref="DisposeAsync"/> disposes it once and does not call <c>CloseAsync</c> first.
        /// </summary>
        private readonly IChannel _channel;

        /// <summary><c>1</c> after the first <see cref="DisposeAsync"/>.</summary>
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
        public async Task<RabbitMqQueueStats> QueueDeclarePassiveAsync(
            string queue,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(queue);
            var ok = await _channel.QueueDeclarePassiveAsync(queue, cancellationToken).ConfigureAwait(false);
            return new RabbitMqQueueStats(ok.MessageCount, ok.ConsumerCount);
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
        private static Dictionary<string, object?>? ToMutable(IReadOnlyDictionary<string, object?>? arguments)
        {
            if (arguments is null)
            {
                return null;
            }

            return new Dictionary<string, object?>(arguments, StringComparer.Ordinal);
        }
    }
}
