using System.Globalization;
using System.Text;
using Serilog.Core;
using Serilog.Debugging;
using Serilog.Events;
using Serilog.Formatting;

namespace VectorNNTP.Common.Messaging.RabbitMq
{
    /// <summary>
    /// Serilog sink that publishes formatted log events on the process RabbitMQ connection.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="RabbitMqService"/> remains the sole TCP connection owner. Each event calls
    /// <see cref="IRabbitMqService.TryGetCurrent"/> and publishes through
    /// <see cref="IRabbitMqConnection.CreatePublishChannelAsync"/>. A cached channel is replaced
    /// when the connection generation changes. This sink does not open a broker connection and
    /// does not accept host, credential, virtual-host, heartbeat, or TLS settings.
    /// </para>
    /// <para>
    /// <see cref="Emit"/> waits for the existing confirm-enabled publish API. Callers that must
    /// keep application threads off that wait should wrap this sink with Serilog's bounded async
    /// sink. Publish and format failures are reported through <see cref="SelfLog"/> and are not
    /// written back through Serilog.
    /// </para>
    /// <para>
    /// The configured exchange is assumed to exist. This sink does not declare topology.
    /// </para>
    /// </remarks>
    internal sealed class RabbitMqLogEventSink : ILogEventSink, IDisposable
    {
        /// <summary>AMQP content type used for plain-text log payloads.</summary>
        public const string TextContentType = "text/plain";

        /// <summary>AMQP content type used for JSON log payloads.</summary>
        public const string JsonContentType = "application/json";

        /// <summary>AMQP content encoding written on every publication.</summary>
        public const string Utf8ContentEncoding = "utf-8";

        /// <summary>Process connection owner. This sink does not dispose it and does not open its own connection.</summary>
        private readonly IRabbitMqService _rabbitMq;

        /// <summary>Serializes <see cref="Emit"/> publication and channel disposal.</summary>
        private readonly object _gate = new();

        /// <summary>
        /// Cached confirm channel for the current connection generation. Replaced when the generation changes or the channel is closed.
        /// </summary>
        private IRabbitMqPublishChannel? _channel;

        /// <summary><c>1</c> while <see cref="Emit"/> is in progress. A re-entrant call is dropped.</summary>
        private int _emitDepth;

        /// <summary><c>1</c> after <see cref="Dispose"/>. Later events are ignored.</summary>
        private int _disposed;

        /// <summary>
        /// Initializes a sink that publishes to <paramref name="exchange"/> with <paramref name="routingKey"/>.
        /// </summary>
        /// <param name="rabbitMq">Process RabbitMQ connection owner. Not disposed by this sink.</param>
        /// <param name="exchange">Destination exchange. The sink does not declare it.</param>
        /// <param name="routingKey">Routing key for each publication.</param>
        /// <param name="appId">AMQP AppId. Callers pass the entry-assembly application identity.</param>
        /// <param name="formatter">Payload formatter selected by the application.</param>
        /// <param name="contentType">AMQP content type for the formatted payload.</param>
        internal RabbitMqLogEventSink(
            IRabbitMqService rabbitMq,
            string exchange,
            string routingKey,
            string appId,
            ITextFormatter formatter,
            string contentType)
        {
            ArgumentNullException.ThrowIfNull(rabbitMq);
            ArgumentException.ThrowIfNullOrWhiteSpace(exchange);
            ArgumentException.ThrowIfNullOrWhiteSpace(routingKey);
            ArgumentException.ThrowIfNullOrWhiteSpace(appId);
            ArgumentNullException.ThrowIfNull(formatter);
            ArgumentException.ThrowIfNullOrWhiteSpace(contentType);

            _rabbitMq = rabbitMq;
            Exchange = exchange.Trim();
            RoutingKey = routingKey.Trim();
            AppId = appId.Trim();
            Formatter = formatter;
            ContentType = contentType.Trim();
        }

        /// <summary>Gets the destination exchange.</summary>
        private string Exchange { get; }

        /// <summary>Gets the routing key.</summary>
        private string RoutingKey { get; }

        /// <summary>Gets the AMQP AppId.</summary>
        private string AppId { get; }

        /// <summary>Gets the payload formatter.</summary>
        internal ITextFormatter Formatter { get; }

        /// <summary>Gets the AMQP content type.</summary>
        internal string ContentType { get; }

        /// <summary>
        /// Formats <paramref name="logEvent"/> and publishes it on the current RabbitMQ connection.
        /// </summary>
        /// <param name="logEvent">The event to publish.</param>
        /// <remarks>
        /// Failures are written to <see cref="SelfLog"/> and are not thrown to the logging caller.
        /// A re-entrant call on the same thread is dropped so a failure cannot log through this sink.
        /// </remarks>
        public void Emit(LogEvent logEvent)
        {
            ArgumentNullException.ThrowIfNull(logEvent);
            if (Volatile.Read(ref _disposed) == 1)
            {
                SelfLog.WriteLine("RabbitMqLogEventSink ignored an event because the sink is disposed.");
                return;
            }

            if (Interlocked.CompareExchange(ref _emitDepth, 1, 0) != 0)
            {
                SelfLog.WriteLine("RabbitMqLogEventSink dropped a re-entrant log event.");
                return;
            }

            try
            {
                lock (_gate)
                {
                    if (Volatile.Read(ref _disposed) == 1)
                    {
                        SelfLog.WriteLine("RabbitMqLogEventSink ignored an event because the sink is disposed.");
                        return;
                    }

                    Publish(logEvent);
                }
            }
            catch (Exception ex)
            {
                SelfLog.WriteLine("RabbitMqLogEventSink publication failed: {0}", ex.Message);
            }
            finally
            {
                Interlocked.Exchange(ref _emitDepth, 0);
            }
        }

        /// <summary>Disposes the cached publish channel. Does not dispose the RabbitMQ connection.</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            lock (_gate)
            {
                DisposeChannel();
            }
        }

        /// <summary>
        /// Confirm-publishes <paramref name="logEvent"/> on the current connection. Skips the event when no open handle is available.
        /// </summary>
        /// <param name="logEvent">Event already accepted by <see cref="Emit"/>.</param>
        /// <remarks>
        /// The body is persistent UTF-8 with a new message id, empty expiration, mandatory routing disabled, and the log timestamp.
        /// <see cref="IRabbitMqPublishChannel.PublishConfirmedAsync(RabbitMqConfirmedPublication, CancellationToken)"/> is awaited synchronously.
        /// This method does not declare the exchange. Failures propagate to <see cref="Emit"/>.
        /// </remarks>
        private void Publish(LogEvent logEvent)
        {
            if (!_rabbitMq.TryGetCurrent(out var handle) || !handle.IsOpen)
            {
                SelfLog.WriteLine("RabbitMqLogEventSink skipped an event because RabbitMQ is not ready.");
                return;
            }

            var channel = EnsureChannel(handle);
            var body = Format(logEvent);
            var publication = new RabbitMqConfirmedPublication(
                Exchange: Exchange,
                RoutingKey: RoutingKey,
                MessageId: Guid.NewGuid().ToString("D"),
                AppId: AppId,
                CorrelationId: null,
                ContentType: ContentType,
                RequestIdHeader: null,
                ExpirationMilliseconds: string.Empty,
                Persistent: true,
                Mandatory: false,
                Body: body,
                ContentEncoding: Utf8ContentEncoding,
                Timestamp: logEvent.Timestamp);
            channel.PublishConfirmedAsync(publication, CancellationToken.None).ConfigureAwait(false).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Returns the cached open channel when it matches <paramref name="handle"/>'s generation; otherwise replaces it.
        /// </summary>
        /// <param name="handle">Current connection snapshot. Not pinned for the publish that follows.</param>
        /// <returns>An open confirm channel for <paramref name="handle"/>'s generation.</returns>
        /// <remarks>Channel creation blocks on <see cref="IRabbitMqConnection.CreatePublishChannelAsync"/>. Topology is not declared.</remarks>
        private IRabbitMqPublishChannel EnsureChannel(RabbitMqConnectionHandle handle)
        {
            if (_channel is { IsOpen: true } current && current.Generation == handle.Generation)
            {
                return current;
            }

            DisposeChannel();
            _channel = handle.Connection
                .CreatePublishChannelAsync(handle.Generation, CancellationToken.None)
                .ConfigureAwait(false)
                .GetAwaiter()
                .GetResult();
            return _channel;
        }

        /// <summary>Formats <paramref name="logEvent"/> with <see cref="Formatter"/> and returns UTF-8 bytes.</summary>
        /// <param name="logEvent">Event to format.</param>
        /// <returns>The formatted payload. No AMQP framing is added.</returns>
        private byte[] Format(LogEvent logEvent)
        {
            using var writer = new StringWriter(CultureInfo.InvariantCulture);
            Formatter.Format(logEvent, writer);
            return Encoding.UTF8.GetBytes(writer.ToString());
        }

        /// <summary>
        /// Disposes the cached publish channel and clears it. A dispose failure is written to <see cref="SelfLog"/> and not thrown.
        /// </summary>
        private void DisposeChannel()
        {
            var channel = _channel;
            _channel = null;
            if (channel is null)
            {
                return;
            }

            try
            {
                channel.DisposeAsync().AsTask().ConfigureAwait(false).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                SelfLog.WriteLine("RabbitMqLogEventSink failed to dispose a publish channel: {0}", ex.Message);
            }
        }
    }
}
