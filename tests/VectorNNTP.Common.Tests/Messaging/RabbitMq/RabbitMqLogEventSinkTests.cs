using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using Serilog.Debugging;
using Serilog.Events;
using Serilog.Formatting;
using Serilog.Parsing;
using VectorNNTP.Common.Messaging.RabbitMq;

namespace VectorNNTP.Common.Tests.Messaging.RabbitMq
{
    public sealed class RabbitMqLogEventSinkTests
    {
        private const string SecretPassword = "rabbit-secret-value-not-in-logs";

        [Fact]
        public async Task Emit_PublishesThroughTheCurrentConnection_WithFormatterAndMetadata()
        {
            await using var harness = await Harness.StartAsync();
            var formatter = new FixedFormatter("formatted-payload");
            var timestamp = DateTimeOffset.Parse("2026-10-02T12:00:00Z");
            using var sink = CreateSink(harness.Service, formatter);

            sink.Emit(Event(timestamp));
            sink.Emit(Event(timestamp.AddSeconds(1)));

            Assert.Equal(1, harness.Factory.ConnectCount);
            var channel = Assert.Single(harness.Factory.LastConnection!.PublishChannels);
            Assert.Equal(2, channel.Publications.Count);
            Assert.Equal(["logs", "logs"], channel.Publications.Select(static publication => publication.Exchange));
            Assert.Equal(["backfiller", "backfiller"], channel.Publications.Select(static publication => publication.RoutingKey));
            Assert.Equal(
                ["formatted-payload", "formatted-payload"],
                channel.Publications.Select(static publication => Encoding.UTF8.GetString(publication.Body.Span)));
            Assert.NotEqual(channel.Publications[0].MessageId, channel.Publications[1].MessageId);

            var first = channel.Publications[0];
            Assert.Equal(RabbitMqLogEventSink.JsonContentType, first.ContentType);
            Assert.Equal(RabbitMqLogEventSink.Utf8ContentEncoding, first.ContentEncoding);
            Assert.True(first.Persistent);
            Assert.False(first.Mandatory);
            Assert.True(string.IsNullOrWhiteSpace(first.ExpirationMilliseconds));
            Assert.Equal("VectorNNTP.Common.Tests", first.AppId);
            Assert.Equal(timestamp, first.Timestamp);
            Assert.DoesNotContain(SecretPassword, Describe(first), StringComparison.Ordinal);
            Assert.DoesNotContain("nntparticles", Describe(first), StringComparison.Ordinal);

            var properties = RabbitMqClientPublishChannel.CreateProperties(first);
            Assert.Equal(first.MessageId, properties.MessageId);
            Assert.Equal(RabbitMqLogEventSink.JsonContentType, properties.ContentType);
            Assert.Equal(RabbitMqLogEventSink.Utf8ContentEncoding, properties.ContentEncoding);
            Assert.Equal(DeliveryModes.Persistent, properties.DeliveryMode);
            Assert.Equal("VectorNNTP.Common.Tests", properties.AppId);
            Assert.Equal(timestamp.ToUnixTimeSeconds(), properties.Timestamp.UnixTime);
            Assert.True(string.IsNullOrWhiteSpace(properties.Expiration));
        }

        [Fact]
        public async Task Emit_ReplacesTheChannelWhenTheConnectionGenerationChanges()
        {
            await using var harness = await Harness.StartAsync();
            using var sink = CreateSink(harness.Service, new FixedFormatter("one"));
            sink.Emit(Event(DateTimeOffset.UtcNow));
            var first = harness.Factory.LastConnection!;
            var firstChannel = Assert.Single(first.PublishChannels);

            var replaced = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            harness.Factory.Connected = replaced;
            first.SimulateLost();
            using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await replaced.Task.WaitAsync(safety.Token);

            sink.Emit(Event(DateTimeOffset.UtcNow));

            Assert.Equal(2, harness.Factory.ConnectCount);
            Assert.Equal(1, firstChannel.DisposeCount);
            var second = harness.Factory.Connections[1];
            Assert.NotSame(first, second);
            var secondChannel = Assert.Single(second.PublishChannels);
            Assert.Equal("one", Encoding.UTF8.GetString(Assert.Single(secondChannel.Publications).Body.Span));
            Assert.Single(firstChannel.Publications);
        }

        [Fact]
        public async Task Emit_DoesNotThrow_WhenPublishFails_AndDoesNotLogThroughSerilog()
        {
            await using var harness = await Harness.StartAsync();
            harness.Factory.LastConnection!.PublishException = new InvalidOperationException("publish failed");
            using var sink = CreateSink(harness.Service, new FixedFormatter("payload"));
            var selfLog = new StringWriter();
            SelfLog.Enable(selfLog);
            var probe = new List<LogEvent>();
            var previous = Serilog.Log.Logger;
            Serilog.Log.Logger = new Serilog.LoggerConfiguration().WriteTo.Sink(new ProbeSink(probe)).CreateLogger();
            try
            {
                var error = Record.Exception(() => sink.Emit(Event(DateTimeOffset.UtcNow)));
                Assert.Null(error);
                Assert.Empty(probe);
                Assert.Contains("publication failed", selfLog.ToString(), StringComparison.Ordinal);
                Assert.DoesNotContain(SecretPassword, selfLog.ToString(), StringComparison.Ordinal);
            }
            finally
            {
                Serilog.Log.CloseAndFlush();
                Serilog.Log.Logger = previous;
                SelfLog.Disable();
            }
        }

        [Fact]
        public async Task Emit_DropsReentrantEvents()
        {
            await using var harness = await Harness.StartAsync();
            RabbitMqLogEventSink? sink = null;
            harness.Factory.LastConnection!.OnPublish = () => sink!.Emit(Event(DateTimeOffset.UtcNow));
            sink = CreateSink(harness.Service, new FixedFormatter("outer"));
            using var _ = sink;
            var selfLog = new StringWriter();
            SelfLog.Enable(selfLog);
            try
            {
                sink.Emit(Event(DateTimeOffset.UtcNow));
                Assert.Single(Assert.Single(harness.Factory.LastConnection.PublishChannels).Publications);
                Assert.Contains("re-entrant", selfLog.ToString(), StringComparison.Ordinal);
            }
            finally
            {
                SelfLog.Disable();
            }
        }

        [Fact]
        public async Task Emit_SkipsWhenTheConnectionIsNotReady()
        {
            await using var harness = await Harness.StartAsync(start: false);
            using var sink = CreateSink(harness.Service, new FixedFormatter("skipped"));
            var selfLog = new StringWriter();
            SelfLog.Enable(selfLog);
            try
            {
                var error = Record.Exception(() => sink.Emit(Event(DateTimeOffset.UtcNow)));
                Assert.Null(error);
                Assert.Equal(0, harness.Factory.ConnectCount);
                Assert.Contains("not ready", selfLog.ToString(), StringComparison.Ordinal);
            }
            finally
            {
                SelfLog.Disable();
            }
        }

        private static RabbitMqLogEventSink CreateSink(IRabbitMqService service, ITextFormatter formatter) =>
            new(service, "logs", "backfiller", "VectorNNTP.Common.Tests", formatter, RabbitMqLogEventSink.JsonContentType);

        private static LogEvent Event(DateTimeOffset timestamp) =>
            new(
                timestamp,
                LogEventLevel.Information,
                exception: null,
                messageTemplate: new MessageTemplate("marker", []),
                properties: []);

        private static string Describe(RabbitMqConfirmedPublication publication) =>
            string.Join(
                '\n',
                publication.Exchange,
                publication.RoutingKey,
                publication.MessageId,
                publication.AppId,
                publication.ContentType,
                publication.ContentEncoding,
                publication.CorrelationId,
                publication.RequestIdHeader,
                publication.ExpirationMilliseconds,
                Encoding.UTF8.GetString(publication.Body.Span));

        private sealed class FixedFormatter(string text) : ITextFormatter
        {
            public void Format(LogEvent logEvent, TextWriter output)
            {
                ArgumentNullException.ThrowIfNull(logEvent);
                ArgumentNullException.ThrowIfNull(output);
                output.Write(text);
            }
        }

        private sealed class ProbeSink(List<LogEvent> events) : Serilog.Core.ILogEventSink
        {
            public void Emit(LogEvent logEvent) => events.Add(logEvent);
        }

        private sealed class Harness : IAsyncDisposable
        {
            private Harness(RabbitMqService service, CountingConnectionFactory factory)
            {
                Service = service;
                Factory = factory;
            }

            public RabbitMqService Service { get; }

            public CountingConnectionFactory Factory { get; }

            public static async Task<Harness> StartAsync(bool start = true)
            {
                var options = RabbitMqOptionsValidatorTests.CreateValidConnectivityOnly();
                options.Username = "nntparticles";
                options.Password = SecretPassword;
                options.PoolReconnectBaseDelayMs = 1;
                options.PoolReconnectMaxDelayMs = 1;
                var factory = new CountingConnectionFactory();
                var service = new RabbitMqService(
                    factory,
                    Options.Create(options),
                    new DelegateRabbitMqConnectionNameProvider(() => "VectorNNTP.Common.Tests:unit"),
                    NullLogger<RabbitMqService>.Instance);
                if (start)
                {
                    await service.StartAsync(CancellationToken.None);
                }

                return new Harness(service, factory);
            }

            public async ValueTask DisposeAsync() => await Service.DisposeAsync();
        }

        private sealed class CountingConnectionFactory : IRabbitMqConnectionFactory
        {
            private int _connectCount;

            public int ConnectCount => Volatile.Read(ref _connectCount);

            public LoggingConnection? LastConnection { get; private set; }

            public List<LoggingConnection> Connections { get; } = [];

            public TaskCompletionSource<int>? Connected { get; set; }

            public Task<IRabbitMqConnection> ConnectAsync(
                RabbitMqOptions options,
                string connectionName,
                CancellationToken cancellationToken)
            {
                var count = Interlocked.Increment(ref _connectCount);
                var connection = new LoggingConnection();
                if (LastConnection is not null)
                {
                    connection.PublishException = LastConnection.PublishException;
                    connection.OnPublish = LastConnection.OnPublish;
                }

                LastConnection = connection;
                Connections.Add(connection);
                Connected?.TrySetResult(count);
                return Task.FromResult<IRabbitMqConnection>(connection);
            }
        }

        private sealed class LoggingConnection : IRabbitMqConnection
        {
            public bool IsOpen { get; set; } = true;

            public string Host => "127.0.0.1";

            public int Port => 5672;

            public string VirtualHost => "/";

            public string ClientProvidedName => "unit";

            public Exception? PublishException { get; set; }

            public Action? OnPublish { get; set; }

            public List<LoggingPublishChannel> PublishChannels { get; } = [];

            public event EventHandler<RabbitMqConnectionLostEventArgs>? ConnectionLost;

            public void SimulateLost()
            {
                IsOpen = false;
                ConnectionLost?.Invoke(this, new RabbitMqConnectionLostEventArgs(320, "lost", "Peer"));
            }

            public Task<IRabbitMqPublishChannel> CreatePublishChannelAsync(long generation, CancellationToken cancellationToken)
            {
                var channel = new LoggingPublishChannel(generation, this);
                PublishChannels.Add(channel);
                return Task.FromResult<IRabbitMqPublishChannel>(channel);
            }

            public Task<IRabbitMqTopologyChannel> CreateTopologyChannelAsync(CancellationToken cancellationToken) =>
                throw new NotSupportedException();

            public Task<IRabbitMqRpcChannel> CreateRpcChannelAsync(long generation, CancellationToken cancellationToken) =>
                throw new NotSupportedException();

            public Task<IRabbitMqManualAckChannel> CreateManualAckChannelAsync(long generation, CancellationToken cancellationToken) =>
                throw new NotSupportedException();

            public Task<IRabbitMqAsyncConfirmPublishChannel> CreateAsyncConfirmPublishChannelAsync(
                long generation,
                CancellationToken cancellationToken) =>
                throw new NotSupportedException();

            public ValueTask DisposeAsync()
            {
                IsOpen = false;
                return ValueTask.CompletedTask;
            }
        }

        private sealed class LoggingPublishChannel(long generation, LoggingConnection connection) : IRabbitMqPublishChannel
        {
            public long Generation { get; } = generation;

            public bool IsOpen { get; private set; } = true;

            public int DisposeCount { get; private set; }

            public List<RabbitMqConfirmedPublication> Publications { get; } = [];

            public Task PublishConfirmedAsync(
                string exchange,
                string routingKey,
                string messageId,
                string appId,
                string expiration,
                ReadOnlyMemory<byte> body,
                CancellationToken cancellationToken) =>
                throw new NotSupportedException();

            public Task PublishConfirmedAsync(RabbitMqConfirmedPublication publication, CancellationToken cancellationToken)
            {
                if (connection.PublishException is not null)
                {
                    throw connection.PublishException;
                }

                Publications.Add(publication);
                connection.OnPublish?.Invoke();
                return Task.CompletedTask;
            }

            public ValueTask DisposeAsync()
            {
                DisposeCount++;
                IsOpen = false;
                return ValueTask.CompletedTask;
            }
        }
    }
}
