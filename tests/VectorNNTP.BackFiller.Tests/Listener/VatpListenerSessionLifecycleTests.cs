using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTP.BackFiller.Configuration;
using VectorNNTP.BackFiller.Listener;
using VectorNNTP.BackFiller.Retention;
using VectorNNTP.BackFiller.Tests.Fixtures;
using VectorNNTP.BackFiller.Tests.Retention;
using VectorNNTP.BackFiller.Tests.TestDoubles;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Transport.ArticleTransfer;
using VectorNNTP.NNTPD.Networking.Certificates;

namespace VectorNNTP.BackFiller.Tests.Listener
{
    /// <summary>
    /// Regression coverage for VATP dual-half session termination when the writer fails
    /// while the peer continues sending inbound frames.
    /// </summary>
    public sealed class VatpListenerSessionLifecycleTests
    {
        private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

        [Fact]
        public async Task WriterFailure_WithInboundTraffic_TerminatesSession()
        {
            await using var harness = await SessionHarness.CreateAsync();
            harness.Transport.AllowSuccessfulWritesThenBlock(2);

            var runTask = harness.Session.RunAsync(CancellationToken.None);
            harness.Transport.EnqueueInbound(EncodeHello());
            await WaitAsync(() => harness.Transport.WriteCalls >= 2);

            // Adversarial peer: keep sending inbound frames after writer is wedged / about to fail.
            // Do not close the inbound side.
            var flood = FloodWindowsAsync(harness.Transport, count: 64, CancellationToken.None);
            await Task.Delay(50);
            harness.Transport.FailWrites();

            await AssertSessionTerminatedAsync(runTask);
            await AssertCompletesAsync(flood);
            Assert.Equal(0, harness.Session.ActiveStreamCount);
        }

        [Fact]
        public async Task WriterFailure_UnblocksQueueFrameAsync()
        {
            await using var harness = await SessionHarness.CreateAsync();
            // HELLO = header + payload writes; then block so the outbound channel can fill.
            harness.Transport.AllowSuccessfulWritesThenBlock(2);

            var runTask = harness.Session.RunAsync(CancellationToken.None);
            harness.Transport.EnqueueInbound(EncodeHello());
            await WaitAsync(() => harness.Transport.WriteCalls >= 2);

            // Fill the bounded outbound channel while the writer is blocked on transport write.
            // Capacity is MaxStreamsPerConnection * 4 (= 256); exceed it so QueueFrameAsync waits.
            var flood = FloodWindowsAsync(harness.Transport, count: 400, CancellationToken.None);
            await WaitAsync(() => harness.Transport.WriteCalls >= 3);

            // Writer is blocked on write #3; inbound flood drives QueueFrameAsync until the channel fills.
            await Task.Delay(100);
            Assert.False(runTask.IsCompleted, "session must still be alive while QueueFrameAsync is blocked");

            harness.Transport.FailWrites();
            await AssertSessionTerminatedAsync(runTask);
            await AssertCompletesAsync(flood);
        }

        [Fact]
        public async Task WriterFailure_ReleasesStreamsAndLeases()
        {
            await using var harness = await SessionHarness.CreateAsync();
            var body = BuildBody(4096);
            var prepared = RetentionTestArticles.RetainPrepared(
                harness.Authority,
                "<writer-fail-lease@example.test>",
                body: body);

            harness.Transport.AllowSuccessfulWritesThenBlock(2);
            var runTask = harness.Session.RunAsync(CancellationToken.None);
            harness.Transport.EnqueueInbound(EncodeHello());
            await WaitAsync(() => harness.Transport.WriteCalls >= 2);

            harness.Transport.EnqueueInbound(EncodeOpen(1, prepared.RequestId, prepared.Record.ArtId));
            await WaitAsync(() => harness.Session.ActiveStreamCount == 1);
            Assert.True(harness.Session.ReservedFoundPayloadBytes > 0);

            harness.Transport.FailWrites();
            await AssertSessionTerminatedAsync(runTask);

            Assert.Equal(0, harness.Session.ActiveStreamCount);
            Assert.Equal(0, harness.Session.ReservedFoundPayloadBytes);

            // Lease released: a fresh RequestId can open the same retained ArtData.
            var reattach = RetentionTestArticles.Create("<writer-fail-lease@example.test>", body);
            Assert.Equal(
                ArticleRetentionKind.AlreadyPresent,
                harness.Authority.RetainCanonical(
                    reattach.MessageId,
                    reattach.RequestId,
                    reattach.Record,
                    reattach.SelectedDateHeaderName).Kind);
            using var open = harness.Authority.TryOpenTransfer(reattach.RequestId, prepared.Record.ArtId);
            Assert.Equal(VatpOpenKind.Opened, open.Kind);
        }

        [Fact]
        public async Task WriterFailure_ReleasesReservedBytes()
        {
            await using var harness = await SessionHarness.CreateAsync();
            var prepared = RetentionTestArticles.RetainPrepared(
                harness.Authority,
                "<writer-fail-bytes@example.test>",
                body: BuildBody(8192));

            harness.Transport.AllowSuccessfulWritesThenBlock(2);
            var runTask = harness.Session.RunAsync(CancellationToken.None);
            harness.Transport.EnqueueInbound(EncodeHello());
            await WaitAsync(() => harness.Transport.WriteCalls >= 2);
            harness.Transport.EnqueueInbound(EncodeOpen(1, prepared.RequestId, prepared.Record.ArtId));
            await WaitAsync(() => harness.Session.ReservedFoundPayloadBytes > 0);

            var reservedBefore = harness.Session.ReservedFoundPayloadBytes;
            Assert.Equal(prepared.Record.ArtSize, reservedBefore);

            harness.Transport.FailWrites();
            await AssertSessionTerminatedAsync(runTask);
            Assert.Equal(0, harness.Session.ReservedFoundPayloadBytes);
        }

        [Fact]
        public async Task WriterFailure_ReleasesConnectionAdmission()
        {
            var runtime = CreateRuntime(GetFreePort(), maxConnections: 1, ioProgressTimeout: TimeSpan.FromMilliseconds(200));
            await using var context = await ListenerContext.StartAsync(runtime);
            Assert.Equal(0, context.Service.ActiveConnections);

            await using var client = await ConnectAsync(context);
            await client.Stream.WriteAsync(EncodeHello());
            // Do not read server responses — fill the TCP send buffer so the listener writer times out.
            // Keep the inbound side open and continue writing so the original hang can reproduce without the fix.
            var floodCts = new CancellationTokenSource();
            var flood = Task.Run(async () =>
            {
                var window = EncodeWindow(999, 1);
                while (!floodCts.IsCancellationRequested)
                {
                    try
                    {
                        await client.Stream.WriteAsync(window, floodCts.Token);
                    }
                    catch (OperationCanceledException) when (floodCts.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (IOException)
                    {
                        return;
                    }
                }
            });

            await WaitAsync(() => context.Service.ActiveConnections == 0, TimeSpan.FromSeconds(15));
            await floodCts.CancelAsync();
            try
            {
                await flood;
            }
            catch (IOException)
            {
            }

            Assert.Equal(0, context.Service.ActiveConnections);

            // Admission slot is free for a subsequent connection.
            await using var second = await ConnectAsync(context);
            await ExchangeHelloFullyAsync(second.Stream);
            Assert.Equal(1, context.Service.ActiveConnections);
            await second.DisposeAsync();
            await WaitAsync(() => context.Service.ActiveConnections == 0);
        }

        [Fact]
        public async Task ReaderFailure_StopsWriter()
        {
            await using var harness = await SessionHarness.CreateAsync();
            var runTask = harness.Session.RunAsync(CancellationToken.None);
            harness.Transport.EnqueueInbound(EncodeHello());
            await WaitAsync(() => harness.Transport.WriteCalls >= 2);

            harness.Transport.FailReads(new IOException("forced reader transport failure"));

            var ex = await Assert.ThrowsAsync<IOException>(() => AssertCompletesAsync(runTask));
            Assert.Equal("forced reader transport failure", ex.Message);
            Assert.Equal(0, harness.Session.ActiveStreamCount);
        }

        [Fact]
        public async Task ConcurrentReaderWriterFailure_CleansUpOnce()
        {
            await using var harness = await SessionHarness.CreateAsync();
            var prepared = RetentionTestArticles.RetainPrepared(
                harness.Authority,
                "<concurrent-fail@example.test>",
                body: BuildBody(2048));

            harness.Transport.AllowSuccessfulWritesThenBlock(2);
            var runTask = harness.Session.RunAsync(CancellationToken.None);
            harness.Transport.EnqueueInbound(EncodeHello());
            await WaitAsync(() => harness.Transport.WriteCalls >= 2);
            harness.Transport.EnqueueInbound(EncodeOpen(1, prepared.RequestId, prepared.Record.ArtId));
            await WaitAsync(() => harness.Session.ActiveStreamCount == 1);

            harness.Transport.FailReads(new IOException("reader boom"));
            harness.Transport.FailWrites(new IOException("writer boom"));

            try
            {
                await AssertCompletesAsync(runTask);
            }
            catch (IOException)
            {
                // Either half may surface; cleanup must still be single-shot.
            }

            Assert.Equal(0, harness.Session.ActiveStreamCount);
            Assert.Equal(0, harness.Session.ReservedFoundPayloadBytes);

            // Second dispose/cleanup path must remain safe.
            await harness.Session.DisposeAsync();
            Assert.Equal(0, harness.Session.ReservedFoundPayloadBytes);
        }

        [Fact]
        public async Task NormalDisconnect_RemainsClean()
        {
            await using var harness = await SessionHarness.CreateAsync();
            var runTask = harness.Session.RunAsync(CancellationToken.None);
            harness.Transport.EnqueueInbound(EncodeHello());
            await WaitAsync(() => harness.Transport.WriteCalls >= 2);

            harness.Transport.CompleteInbound();
            await AssertCompletesAsync(runTask);
            Assert.Equal(0, harness.Session.ActiveStreamCount);
            Assert.Equal(0, harness.Session.ReservedFoundPayloadBytes);
        }

        [Fact]
        public async Task GracefulShutdown_RemainsClean()
        {
            await using var harness = await SessionHarness.CreateAsync();
            using var cts = new CancellationTokenSource();
            // Hold the writer after HELLO so the OPEN stream remains active until host cancel.
            harness.Transport.AllowSuccessfulWritesThenBlock(2);
            var runTask = harness.Session.RunAsync(cts.Token);
            harness.Transport.EnqueueInbound(EncodeHello());
            await WaitAsync(() => harness.Transport.WriteCalls >= 2);

            var prepared = RetentionTestArticles.RetainPrepared(
                harness.Authority,
                "<graceful-shutdown@example.test>",
                body: BuildBody(2048));
            harness.Transport.EnqueueInbound(EncodeOpen(1, prepared.RequestId, prepared.Record.ArtId));
            await WaitAsync(() => harness.Session.ActiveStreamCount == 1);

            await cts.CancelAsync();
            await AssertCompletesAsync(runTask);
            Assert.Equal(0, harness.Session.ActiveStreamCount);
            Assert.Equal(0, harness.Session.ReservedFoundPayloadBytes);
        }

        private static string BuildBody(int minimumBytes)
        {
            const string line = "abcdefghijklmnopqrstuvwxyz0123456789\r\n";
            var builder = new System.Text.StringBuilder(minimumBytes + line.Length);
            while (builder.Length < minimumBytes)
            {
                builder.Append(line);
            }

            return builder.ToString();
        }

        private static async Task FloodWindowsAsync(
            ControllableCacheListenerTransport transport,
            int count,
            CancellationToken cancellationToken)
        {
            var frame = EncodeWindow(999, 1);
            for (var i = 0; i < count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    transport.EnqueueInbound(frame);
                }
                catch (ObjectDisposedException)
                {
                    return;
                }

                if ((i & 15) == 0)
                {
                    await Task.Yield();
                }
            }
        }

        private static byte[] EncodeHello() =>
            VatpFrameEncoder.ToSingleBuffer(VatpFrameEncoder.EncodeHello(VatpProtocol.DefaultMaxFramePayload));

        private static byte[] EncodeWindow(uint streamId, uint addCredit) =>
            VatpFrameEncoder.ToSingleBuffer(VatpFrameEncoder.EncodeWindow(streamId, addCredit));

        private static byte[] EncodeOpen(uint streamId, Guid requestId, ArticleId articleId)
        {
            Span<byte> idBytes = stackalloc byte[VatpProtocol.ArticleIdLength];
            articleId.CopyTo(idBytes);
            return VatpFrameEncoder.ToSingleBuffer(VatpFrameEncoder.EncodeOpen(streamId, requestId, idBytes));
        }

        private static async Task AssertCompletesAsync(Task task)
        {
            var finished = await Task.WhenAny(task, Task.Delay(TestTimeout));
            Assert.Same(task, finished);
            await task;
        }

        /// <summary>
        /// Writer transport failures are observed (not swallowed); RunAsync may fault with IOException.
        /// </summary>
        private static async Task AssertSessionTerminatedAsync(Task task)
        {
            var finished = await Task.WhenAny(task, Task.Delay(TestTimeout));
            Assert.Same(task, finished);
            try
            {
                await task;
            }
            catch (IOException)
            {
            }
        }

        private static async Task WaitAsync(Func<bool> condition, TimeSpan? timeout = null)
        {
            var deadline = TimeProvider.System.GetUtcNow() + (timeout ?? TestTimeout);
            while (!condition())
            {
                if (TimeProvider.System.GetUtcNow() > deadline)
                {
                    Assert.Fail("condition not met before timeout");
                }

                await Task.Delay(10);
            }
        }

        private static async Task ExchangeHelloFullyAsync(SslStream stream)
        {
            await stream.WriteAsync(EncodeHello());
            var header = new byte[VatpProtocol.HeaderLengthBytes];
            await ReadExactAsync(stream, header);
            var payloadLen = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(8, 4));
            if (payloadLen > 0)
            {
                var payload = new byte[payloadLen];
                await ReadExactAsync(stream, payload);
            }
        }

        private static async Task ReadExactAsync(Stream stream, Memory<byte> buffer)
        {
            var offset = 0;
            while (offset < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer[offset..]);
                Assert.True(read > 0, "unexpected EOF");
                offset += read;
            }
        }

        private static BackFillerRuntimeOptions CreateRuntime(
            int port,
            int maxConnections = 8,
            TimeSpan? ioProgressTimeout = null)
        {
            var options = BackFillerTestOptions.CreateValid();
            options.BindPortTls = port;
            options.BindAddress = ["127.0.0.1"];
            var runtime = BackFillerRuntimeOptionsFactory.Create(
                options,
                BackFillerTestOptions.CreateValidNntpDb());
            return runtime with
            {
                Listener = runtime.Listener with
                {
                    MaxActiveConnections = maxConnections,
                    IoProgressTimeout = ioProgressTimeout ?? runtime.Listener.IoProgressTimeout,
                },
            };
        }

        private static int GetFreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        private static async Task<TlsClient> ConnectAsync(ListenerContext context)
        {
            var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, context.Port);
            var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false, static (_, _, _, _) => true);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "localhost",
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            });
            return new TlsClient(tcp, ssl);
        }

        private sealed class TlsClient(TcpClient tcp, SslStream stream) : IAsyncDisposable
        {
            public SslStream Stream { get; } = stream;

            public async ValueTask DisposeAsync()
            {
                await Stream.DisposeAsync();
                tcp.Dispose();
            }
        }

        private sealed class ListenerContext : IAsyncDisposable
        {
            private ListenerContext(
                CacheListenerService service,
                ArticleRetentionAuthority authority,
                int port,
                TlsCertificateContextProvider certificates)
            {
                Service = service;
                Authority = authority;
                Port = port;
                Certificates = certificates;
            }

            public CacheListenerService Service { get; }

            public ArticleRetentionAuthority Authority { get; }

            public int Port { get; }

            private TlsCertificateContextProvider Certificates { get; }

            public static async Task<ListenerContext> StartAsync(BackFillerRuntimeOptions runtime)
            {
                var authority = ArticleRetentionAuthorityTests.Create(TimeProvider.System, 16 * 1024 * 1024);
                var certificates = TestListenerCertificates.CreatePublishedProvider();
                var service = new CacheListenerService(
                    runtime,
                    certificates,
                    authority,
                    NullLogger<CacheListenerService>.Instance);
                await service.StartAsync(CancellationToken.None);
                return new ListenerContext(service, authority, runtime.BindPortTls, certificates);
            }

            public async ValueTask DisposeAsync()
            {
                await Service.DisposeAsync();
                await Authority.DisposeAsync();
                await Certificates.DisposeAsync();
            }
        }

        private sealed class SessionHarness : IAsyncDisposable
        {
            private SessionHarness(
                ControllableCacheListenerTransport transport,
                VatpListenerSession session,
                ArticleRetentionAuthority authority,
                BackFillerListenerRuntimeOptions listener)
            {
                Transport = transport;
                Session = session;
                Authority = authority;
                _ = listener;
            }

            public ControllableCacheListenerTransport Transport { get; }

            public VatpListenerSession Session { get; }

            public ArticleRetentionAuthority Authority { get; }

            public static Task<SessionHarness> CreateAsync()
            {
                var authority = ArticleRetentionAuthorityTests.Create(TimeProvider.System, 16 * 1024 * 1024);
                var listener = new BackFillerListenerRuntimeOptions(
                    TlsHandshakeTimeout: TimeSpan.FromSeconds(5),
                    IoProgressTimeout: TimeSpan.FromSeconds(5),
                    MaxQueuedFoundPayloadBytes: 8 * 1024 * 1024,
                    MaxActiveConnections: 8);
                var transport = new ControllableCacheListenerTransport();
                var session = new VatpListenerSession(
                    transport,
                    authority,
                    listener,
                    NullLogger.Instance);
                return Task.FromResult(new SessionHarness(transport, session, authority, listener));
            }

            public async ValueTask DisposeAsync()
            {
                await Session.DisposeAsync();
                await Authority.DisposeAsync();
            }
        }
    }
}
