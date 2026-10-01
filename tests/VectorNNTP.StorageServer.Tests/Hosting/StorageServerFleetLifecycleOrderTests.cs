using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Articles.Parsing;
using VectorNNTP.Common.Messaging.Cache;
using VectorNNTP.Common.Messaging.RabbitMq;
using VectorNNTP.NNTPD.Cloudflare;
using VectorNNTP.NNTPD.Configuration;
using VectorNNTP.NNTPD.Core;
using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Hosting;
using VectorNNTP.StorageServer.Listener;
using VectorNNTP.StorageServer.Logging;
using VectorNNTP.StorageServer.Storage;
using VectorNNTP.StorageServer.Storage.Engine;
using VectorNNTP.StorageServer.Tests.Fixtures;
using VectorNNTP.StorageServer.Tests.TestDoubles;

namespace VectorNNTP.StorageServer.Tests.Hosting;

/// <summary>
/// Proves advertisement and presence follow the VATP listener in
/// <see cref="ApplicationServiceManager"/> registration order.
/// </summary>
public sealed class StorageServerFleetLifecycleOrderTests
{
    [Fact]
    public async Task AdvertisementPublisher_DoesNotStartBeforeVatpListener()
    {
        await using var session = await FleetSession.StartHeldAtListenerAsync();
        Assert.NotEqual(StorageVatpListenerState.Running, session.Listener.State);
        Assert.Equal(0, session.Publisher.PublishedCount);
        Assert.Null(session.Publisher.Execution);
    }

    [Fact]
    public async Task PresenceConsumer_DoesNotAnswerBeforeVatpListener()
    {
        await using var session = await FleetSession.StartHeldAtListenerAsync();
        Assert.NotEqual(StorageVatpListenerState.Running, session.Listener.State);
        Assert.Null(session.Lookup.CurrentQueueName);
        Assert.Empty(session.Connection.ManualAckChannels);
        Assert.Empty(session.Connection.PublishChannels.SelectMany(static channel => channel.Publications));
    }

    [Fact]
    public async Task AdvertisementPublisher_StartsAfterVatpListener()
    {
        await using var session = await FleetSession.StartAsync();
        Assert.Equal(StorageVatpListenerState.Running, session.Listener.State);
        await WaitForAsync(() => session.Publisher.PublishedCount >= 1, TimeSpan.FromSeconds(5));
        Assert.True(session.Publisher.PublishedCount >= 1);
    }

    [Fact]
    public async Task PresenceConsumer_StartsAfterVatpListener()
    {
        await using var session = await FleetSession.StartAsync();
        Assert.Equal(StorageVatpListenerState.Running, session.Listener.State);
        Assert.False(string.IsNullOrWhiteSpace(session.Lookup.CurrentQueueName));

        var record = CreateRecord("<fleet-order-present@seg.test>");
        var engine = session.AppHost.Services.GetRequiredService<StorageEngineApplicationService>();
        Assert.Equal(
            ArticleAcceptOutcome.Accepted,
            (await engine.Engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await engine.Engine.DrainPendingAsync(CancellationToken.None);

        var channel = Assert.Single(session.Connection.ManualAckChannels);
        var request = new StorageArticleLookupRequest(1, Guid.NewGuid(), record.ArtId);
        await channel.DeliverAsync(
            11,
            StorageArticleLookupWireProtocol.SerializeRequestV1(request),
            "corr-fleet-order",
            "nntpd.reply");

        var responsePublication = session.Connection.PublishChannels
            .SelectMany(static item => item.Publications)
            .Single(static publication => publication.RoutingKey == "nntpd.reply");
        Assert.True(StorageArticleLookupWireProtocol.TryParseResponseV1(
            responsePublication.Body.Span,
            out var response,
            out _));
        Assert.NotNull(response);
        Assert.Equal(record.ArtId, response.ArticleId);
    }

    [Fact]
    public async Task PresenceConsumer_StopsBeforeVatpListener()
    {
        await using var session = await FleetSession.StartAsync(traceStops: true);
        await session.StopAsync();
        var lookupEnd = session.StopEvents.IndexOf("end:StorageArticleLookupConsumer");
        var listenerBegin = session.StopEvents.IndexOf("begin:StorageVatpListener");
        Assert.True(lookupEnd >= 0);
        Assert.True(listenerBegin > lookupEnd);
    }

    [Fact]
    public async Task AdvertisementPublisher_StopsBeforeVatpListener()
    {
        await using var session = await FleetSession.StartAsync(traceStops: true);
        await WaitForAsync(() => session.Publisher.PublishedCount >= 1, TimeSpan.FromSeconds(5));
        await session.StopAsync();
        var publisherEnd = session.StopEvents.IndexOf("end:StorageServerAdvertisementPublisher");
        var listenerBegin = session.StopEvents.IndexOf("begin:StorageVatpListener");
        Assert.True(publisherEnd >= 0);
        Assert.True(listenerBegin > publisherEnd);
        Assert.Equal(session.PublishedCountWhenListenerStopBegan, session.Publisher.PublishedCount);
    }

    [Fact]
    public async Task StartupFailure_DoesNotAdvertise()
    {
        var port = GetFreePort();
        using var occupied = new TcpListener(IPAddress.Loopback, port);
        occupied.Start();
        await using var session = await FleetSession.CreateAsync(port);
        var failure = await Record.ExceptionAsync(() => session.Manager.StartAsync(CancellationToken.None));
        Assert.NotNull(failure);
        Assert.Equal(0, session.Publisher.PublishedCount);
        Assert.Null(session.Publisher.Execution);
        Assert.NotEqual(StorageVatpListenerState.Running, session.Listener.State);
    }

    [Fact]
    public async Task Shutdown_DoesNotLeaveAdvertisementLoop()
    {
        await using var session = await FleetSession.StartAsync();
        await WaitForAsync(() => session.Publisher.Execution is not null, TimeSpan.FromSeconds(5));
        var loop = session.Publisher.Execution;
        Assert.NotNull(loop);
        await session.StopAsync();
        Assert.True(loop.IsCompleted);
        Assert.Null(session.Publisher.Execution);
    }

    private static ArticleRecord CreateRecord(string messageId)
    {
        var parser = new NntpArticleParser("cache01.usenet.ninja");
        var builder = new StringBuilder();
        _ = builder.Append("Path: peer.example\r\n");
        _ = builder.Append("Date: Fri, 23 Aug 2024 07:30:10 +0000\r\n");
        _ = builder.Append("Message-ID: ").Append(messageId).Append("\r\n");
        _ = builder.Append("Newsgroups: alt.test\r\n");
        _ = builder.Append("From: user@example.test\r\n");
        _ = builder.Append("Subject: fleet-order\r\n");
        _ = builder.Append("\r\nline1\r\n");
        var created = ArticleRecordFactory.TryCreate(parser, Encoding.ASCII.GetBytes(builder.ToString()));
        Assert.True(created.IsAccepted, created.ParseFailure.ToString());
        return created.Record;
    }

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition was not met before timeout.");
            }

            await Task.Delay(10);
        }
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class FleetSession : IAsyncDisposable
    {
        private readonly RecordingStorageServerRabbitMqConnectionFactory _factory;
        private readonly TaskCompletionSource<bool> _releaseListener =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly bool _held;
        private bool _stopped;

        private FleetSession(
            IHost host,
            StorageVatpListenerService listener,
            StorageServerAdvertisementPublisherService publisher,
            StorageArticleLookupConsumerService lookup,
            RecordingStorageServerRabbitMqConnectionFactory factory,
            bool held)
        {
            AppHost = host;
            Listener = listener;
            Publisher = publisher;
            Lookup = lookup;
            _factory = factory;
            _held = held;
        }

        public IHost AppHost { get; }

        public ApplicationServiceManager Manager { get; private set; } = null!;

        public StorageVatpListenerService Listener { get; }

        public StorageServerAdvertisementPublisherService Publisher { get; }

        public StorageArticleLookupConsumerService Lookup { get; }

        public RecordingStorageServerRabbitMqConnection Connection =>
            _factory.LastConnection ?? throw new InvalidOperationException("RabbitMQ connection was not created.");

        public List<string> StopEvents { get; } = [];

        public long PublishedCountWhenListenerStopBegan { get; private set; }

        public TaskCompletionSource ListenerEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private Task Starting { get; set; } = Task.CompletedTask;

        public static Task<FleetSession> StartHeldAtListenerAsync() =>
            StartAsync(holdListener: true, traceStops: false);

        public static async Task<FleetSession> StartAsync(bool holdListener = false, bool traceStops = false)
        {
            var session = await CreateAsync(GetFreePort(), holdListener, traceStops);
            if (!holdListener)
            {
                await session.Manager.StartAsync(CancellationToken.None);
                return session;
            }

            session.Starting = session.Manager.StartAsync(CancellationToken.None);
            await session.ListenerEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            return session;
        }

        public static Task<FleetSession> CreateAsync(int port) =>
            CreateAsync(port, holdListener: false, traceStops: false);

        public async Task StopAsync()
        {
            if (_stopped)
            {
                return;
            }

            _stopped = true;
            if (_held)
            {
                _releaseListener.TrySetResult(true);
                try
                {
                    await Starting.WaitAsync(TimeSpan.FromSeconds(30));
                }
                catch (Exception)
                {
                    // A startup failure is reported by the caller. Shutdown still runs below.
                }
            }

            await Manager.StopAsync(CancellationToken.None);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await StopAsync();
            }
            finally
            {
                AppHost.Dispose();
            }
        }

        private static Task<FleetSession> CreateAsync(int port, bool holdListener, bool traceStops)
        {
            var factory = new RecordingStorageServerRabbitMqConnectionFactory();
            var host = CreateHost(port, factory);
            var ordered = host.Services.GetServices<IApplicationService>().ToArray();
            AssertProductionOrder(ordered);
            var listener = host.Services.GetRequiredService<StorageVatpListenerService>();
            var publisher = host.Services.GetRequiredService<StorageServerAdvertisementPublisherService>();
            var lookup = host.Services.GetRequiredService<StorageArticleLookupConsumerService>();
            var session = new FleetSession(host, listener, publisher, lookup, factory, holdListener);
            var services = ordered.Select(service => service switch
            {
                StorageVatpListenerService when holdListener => (IApplicationService)new HoldListener(
                    listener,
                    session.ListenerEntered,
                    session._releaseListener),
                StorageVatpListenerService when traceStops => new StopTrace(
                    service,
                    session.StopEvents,
                    () => session.PublishedCountWhenListenerStopBegan = publisher.PublishedCount),
                _ when traceStops => new StopTrace(service, session.StopEvents, onStopBegin: null),
                _ => service,
            }).ToArray();

            session.Manager = new ApplicationServiceManager(
                services,
                host.Services.GetRequiredService<IApplicationLifecycleOptions>(),
                NullLogger<ApplicationServiceManager>.Instance);
            return Task.FromResult(session);
        }

        private static void AssertProductionOrder(IApplicationService[] ordered)
        {
            var listenerIndex = Array.FindIndex(ordered, static service => service is StorageVatpListenerService);
            var publisherIndex = Array.FindIndex(
                ordered,
                static service => service is StorageServerAdvertisementPublisherService);
            var lookupIndex = Array.FindIndex(
                ordered,
                static service => service is StorageArticleLookupConsumerService);
            var rabbitIndex = Array.FindIndex(ordered, static service => service is RabbitMqService);
            var acmeIndex = Array.FindIndex(ordered, static service => service is ImmediateAcmeReadyApplicationService);
            Assert.True(rabbitIndex >= 0 && rabbitIndex < acmeIndex);
            Assert.True(acmeIndex < listenerIndex);
            Assert.True(listenerIndex < publisherIndex);
            Assert.True(publisherIndex < lookupIndex);
        }

        private static IHost CreateHost(int port, RecordingStorageServerRabbitMqConnectionFactory factory)
        {
            var builder = Host.CreateApplicationBuilder([]);
            var pairs = StorageServerTestOptions.CreateValidConfigurationPairs();
            pairs["StorageServer:BindPortTls"] = port.ToString();
            pairs["StorageServer:BindAddress:0"] = "127.0.0.1";
            builder.Configuration.AddInMemoryCollection(pairs);
            builder.Services.AddSingleton<ILocalIpAddressAssignee>(new FakeLocalIpAddressAssignee(assignAll: true));
            builder.Services.AddSingleton<ICloudflareDnsReconciler>(new NoOpCloudflareDnsReconciler());
            builder.ConfigureStorageServerLogging();
            builder.ConfigureStorageServerPlatformHosting();
            builder.AddStorageServerHosting();
            builder.Services.AddSingleton<IRabbitMqConnectionFactory>(factory);
            ReplaceAcme(builder.Services);
            return builder.Build();
        }

        private static void ReplaceAcme(IServiceCollection services)
        {
            for (var i = 0; i < services.Count; i++)
            {
                if (services[i].ServiceType == typeof(IApplicationService)
                    && services[i].ImplementationType == typeof(VectorNNTP.StorageServer.Acme.AcmeCertificateApplicationService))
                {
                    services[i] = ServiceDescriptor.Singleton<IApplicationService, ImmediateAcmeReadyApplicationService>();
                }
            }
        }
    }

    private sealed class HoldListener : IApplicationService
    {
        private readonly StorageVatpListenerService _inner;
        private readonly TaskCompletionSource _entered;
        private readonly TaskCompletionSource<bool> _release;

        public HoldListener(
            StorageVatpListenerService inner,
            TaskCompletionSource entered,
            TaskCompletionSource<bool> release)
        {
            _inner = inner;
            _entered = entered;
            _release = release;
        }

        public string Name => _inner.Name;

        public Task? Execution => _inner.Execution;

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            _entered.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            await _inner.StartAsync(cancellationToken).ConfigureAwait(false);
        }

        public Task StopAsync(CancellationToken cancellationToken) => _inner.StopAsync(cancellationToken);
    }

    private sealed class StopTrace : IApplicationService
    {
        private readonly IApplicationService _inner;
        private readonly List<string> _events;
        private readonly Action? _onStopBegin;

        public StopTrace(IApplicationService inner, List<string> events, Action? onStopBegin)
        {
            _inner = inner;
            _events = events;
            _onStopBegin = onStopBegin;
        }

        public string Name => _inner.Name;

        public Task? Execution => _inner.Execution;

        public Task StartAsync(CancellationToken cancellationToken) => _inner.StartAsync(cancellationToken);

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            _onStopBegin?.Invoke();
            _events.Add("begin:" + Name);
            await _inner.StopAsync(cancellationToken).ConfigureAwait(false);
            _events.Add("end:" + Name);
        }
    }
}
