using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTP.BackFiller.Nntp;
using VectorNNTP.BackFiller.Tests.TestDoubles;

namespace VectorNNTP.BackFiller.Tests.Nntp
{
    public sealed class NntpStartTlsTests
    {
        private const string Password = "super-secret-nntp-password";

        [Fact]
        public async Task Capabilities_starttls_382_then_tls_then_authentication()
        {
            var logger = new CollectingLogger<NntpProviderSession>();
            using var certificate = TestListenerCertificates.CreateSelfSigned("nntp-starttls.test");
            await using var server = new LoopbackStartTlsNntpServer(certificate);
            var session = CreateSession(
                logger,
                host: "127.0.0.1",
                port: server.Port,
                user: "user",
                password: Password,
                acceptAnyCertificate: true);

            Assert.Null(await session.ConnectAsync(new TcpNntpTransportFactory(), CancellationToken.None));

            Assert.Equal(NntpSessionState.Ready, session.State);
            Assert.True(session.IsReusable);
            Assert.Equal(["CAPABILITIES", "STARTTLS", "AUTHINFO USER user", "AUTHINFO PASS " + Password], server.Commands);
            Assert.True(server.TlsHandshakeCompleted);
            Assert.Contains(logger.Messages, static message => message.Contains("TX: CAPABILITIES", StringComparison.Ordinal));
            Assert.Contains(logger.Messages, static message => message.Contains("RX: 101 Capability list follows", StringComparison.Ordinal));
            Assert.Contains(logger.Messages, static message => message.Contains("RX: STARTTLS", StringComparison.Ordinal));
            Assert.Contains(logger.Messages, static message => message.Contains("TX: STARTTLS", StringComparison.Ordinal));
            Assert.Contains(
                logger.Messages,
                static message => message.Contains("RX: 382 Continue with TLS negotiation", StringComparison.Ordinal));
            Assert.Contains(logger.Messages, static message => message.Contains("TLS handshake starting", StringComparison.Ordinal));
            Assert.Contains(logger.Messages, static message => message.Contains("TLS handshake completed", StringComparison.Ordinal));
            Assert.Contains(logger.Messages, static message => message.Contains("TX: AUTHINFO USER ***", StringComparison.Ordinal));
            Assert.Contains(logger.Messages, static message => message.Contains("TX: AUTHINFO PASS ***", StringComparison.Ordinal));
            Assert.All(
                logger.Messages,
                static message =>
                {
                    Assert.DoesNotContain(Password, message, StringComparison.Ordinal);
                    Assert.DoesNotContain("AUTHINFO USER user", message, StringComparison.Ordinal);
                });
            await session.DisposeAsync();
        }

        [Fact]
        public async Task Capabilities_without_starttls_continues_plaintext_authentication()
        {
            var factory = new ScriptedNntpTransportFactory();
            var server = new ScriptedNntpServer();
            server.Respond(static command => command.StartsWith("AUTHINFO USER", StringComparison.Ordinal)
                ? "381 password required\r\n"
                : "281 ok\r\n");
            factory.Enqueue(server);
            var session = CreateSession(user: "user", password: "secret");

            Assert.Null(await session.ConnectAsync(factory, CancellationToken.None));
            Assert.Equal(NntpSessionState.Ready, session.State);
            Assert.Equal("CAPABILITIES", server.Commands[0]);
            Assert.Equal("AUTHINFO USER user", server.Commands[1]);
            Assert.Equal("AUTHINFO PASS secret", server.Commands[2]);
            Assert.DoesNotContain(server.Commands, static command => command.Equals("STARTTLS", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(server.Commands, static command => command.StartsWith("COMPRESS", StringComparison.OrdinalIgnoreCase));
            await session.DisposeAsync();
        }

        [Fact]
        public async Task Advertised_starttls_non_382_retires_session_without_authentication()
        {
            var factory = new ScriptedNntpTransportFactory();
            var server = new ScriptedNntpServer
            {
                AdvertiseStartTls = true,
                StartTlsResponse = "502 Command unavailable\r\n",
            };
            factory.Enqueue(server);
            var session = CreateSession(user: "user", password: "secret");

            var result = await session.ConnectAsync(factory, CancellationToken.None);

            Assert.Equal(ArticleRetrievalKind.ProviderFailure, result!.Kind);
            Assert.Equal(502, result.StatusCode);
            Assert.False(result.SessionReusable);
            Assert.Equal(NntpSessionState.Retiring, session.State);
            Assert.Equal(["CAPABILITIES", "STARTTLS"], server.Commands);
            await session.DisposeAsync();
        }

        [Fact]
        public async Task Tls_handshake_failure_retires_session_without_authentication()
        {
            var factory = new ScriptedNntpTransportFactory();
            var server = new ScriptedNntpServer
            {
                AdvertiseStartTls = true,
                CompleteAfterStartTls = true,
            };
            factory.Enqueue(server);
            var session = CreateSession(user: "user", password: "secret");

            var result = await session.ConnectAsync(factory, CancellationToken.None);

            Assert.Equal(ArticleRetrievalKind.ProviderFailure, result!.Kind);
            Assert.False(result.SessionReusable);
            Assert.Contains("TLS handshake failed", result.Reason, StringComparison.Ordinal);
            Assert.Equal(NntpSessionState.Retiring, session.State);
            Assert.Equal(["CAPABILITIES", "STARTTLS"], server.Commands);
            await session.DisposeAsync();
        }

        [Fact]
        public async Task Capabilities_malformed_status_is_a_protocol_failure()
        {
            var factory = new ScriptedNntpTransportFactory();
            var server = new ScriptedNntpServer
            {
                CapabilitiesResponse = "not-a-status\r\n",
            };
            factory.Enqueue(server);
            var session = CreateSession(user: "user", password: "secret");

            var result = await session.ConnectAsync(factory, CancellationToken.None);

            Assert.Equal(ArticleRetrievalKind.ProviderFailure, result!.Kind);
            Assert.False(result.SessionReusable);
            Assert.DoesNotContain(server.Commands, static command => command.StartsWith("AUTHINFO", StringComparison.Ordinal));
            Assert.DoesNotContain(server.Commands, static command => command.Equals("STARTTLS", StringComparison.OrdinalIgnoreCase));
            await session.DisposeAsync();
        }

        [Fact]
        public async Task Capabilities_incomplete_list_is_a_protocol_failure()
        {
            var factory = new ScriptedNntpTransportFactory();
            var server = new ScriptedNntpServer
            {
                CapabilitiesResponse = "101 Capability list follows\r\nVERSION 2\r\nSTARTTLS\r\n",
                CompleteAfterCapabilities = true,
            };
            factory.Enqueue(server);
            var session = CreateSession(user: "user", password: "secret");

            var result = await session.ConnectAsync(factory, CancellationToken.None);

            Assert.Equal(ArticleRetrievalKind.ProviderFailure, result!.Kind);
            Assert.False(result.SessionReusable);
            Assert.DoesNotContain(server.Commands, static command => command.StartsWith("AUTHINFO", StringComparison.Ordinal));
            Assert.DoesNotContain(server.Commands, static command => command.Equals("STARTTLS", StringComparison.OrdinalIgnoreCase));
            await session.DisposeAsync();
        }

        [Fact]
        public async Task Authentication_cannot_occur_before_required_starttls()
        {
            var factory = new ScriptedNntpTransportFactory();
            var server = new ScriptedNntpServer
            {
                AdvertiseStartTls = true,
                StartTlsResponse = "480 TLS required but unavailable\r\n",
            };
            server.Respond(static _ => "281 should-never-be-used\r\n");
            factory.Enqueue(server);
            var session = CreateSession(user: "user", password: "secret");

            var result = await session.ConnectAsync(factory, CancellationToken.None);

            Assert.Equal(ArticleRetrievalKind.ProviderFailure, result!.Kind);
            Assert.Equal(480, result.StatusCode);
            Assert.Equal(["CAPABILITIES", "STARTTLS"], server.Commands);
            await session.DisposeAsync();
        }

        [Theory]
        [InlineData("starttls")]
        [InlineData("StartTls")]
        [InlineData("STARTTLS")]
        public async Task Starttls_capability_matching_is_case_insensitive(string label)
        {
            var factory = new ScriptedNntpTransportFactory();
            var server = new ScriptedNntpServer
            {
                AdvertiseStartTls = true,
                StartTlsCapabilityLabel = label,
                StartTlsResponse = "502 no\r\n",
            };
            factory.Enqueue(server);
            var session = CreateSession(user: "user", password: "secret");

            var result = await session.ConnectAsync(factory, CancellationToken.None);

            Assert.Equal(ArticleRetrievalKind.ProviderFailure, result!.Kind);
            Assert.Equal("STARTTLS", server.Commands[1]);
            Assert.DoesNotContain(server.Commands, static command => command.StartsWith("AUTHINFO", StringComparison.Ordinal));
            await session.DisposeAsync();
        }

        [Fact]
        public async Task Compress_is_never_negotiated_when_advertised()
        {
            var factory = new ScriptedNntpTransportFactory();
            var server = new ScriptedNntpServer
            {
                AdvertiseStartTls = true,
                AdvertiseCompress = true,
                StartTlsResponse = "502 no\r\n",
            };
            factory.Enqueue(server);
            var session = CreateSession(user: "user", password: "secret");

            _ = await session.ConnectAsync(factory, CancellationToken.None);

            Assert.DoesNotContain(
                server.Commands,
                static command => command.StartsWith("COMPRESS", StringComparison.OrdinalIgnoreCase));
            Assert.Equal("STARTTLS", server.Commands[1]);
            await session.DisposeAsync();
        }

        [Fact]
        public void Production_nntp_code_does_not_mention_compress_negotiation()
        {
            var directory = NntpProviderSessionTests.FindSourceDirectory();
            foreach (var file in Directory.GetFiles(directory, "*.cs"))
            {
                var text = File.ReadAllText(file);
                Assert.DoesNotContain("COMPRESS", text, StringComparison.Ordinal);
            }
        }

        [Fact]
        public void Capability_label_equals_is_case_insensitive_and_token_scoped()
        {
            Assert.True(NntpProtocolIo.CapabilityLabelEquals("STARTTLS"u8, NntpProtocolIo.StartTlsCapability));
            Assert.True(NntpProtocolIo.CapabilityLabelEquals("starttls"u8, NntpProtocolIo.StartTlsCapability));
            Assert.True(NntpProtocolIo.CapabilityLabelEquals(" StartTls extra"u8, NntpProtocolIo.StartTlsCapability));
            Assert.False(NntpProtocolIo.CapabilityLabelEquals("XSTARTTLS"u8, NntpProtocolIo.StartTlsCapability));
            Assert.False(NntpProtocolIo.CapabilityLabelEquals("COMPRESS DEFLATE"u8, NntpProtocolIo.StartTlsCapability));
        }

        [Fact]
        public async Task Implicit_tls_does_not_issue_starttls_when_advertised()
        {
            var factory = new ScriptedNntpTransportFactory();
            var server = new ScriptedNntpServer { AdvertiseStartTls = true };
            server.Respond(static command => command.StartsWith("AUTHINFO USER", StringComparison.Ordinal)
                ? "381 password required\r\n"
                : "281 ok\r\n");
            factory.Enqueue(server);
            var session = CreateSession(useTls: true, user: "user", password: "secret");

            Assert.Null(await session.ConnectAsync(factory, CancellationToken.None));
            Assert.DoesNotContain(server.Commands, static command => command.Equals("STARTTLS", StringComparison.OrdinalIgnoreCase));
            Assert.Equal("AUTHINFO USER user", server.Commands[1]);
            await session.DisposeAsync();
        }

        [Fact]
        public async Task Leftover_bytes_after_382_fail_before_authentication()
        {
            var factory = new ScriptedNntpTransportFactory();
            var server = new ScriptedNntpServer
            {
                AdvertiseStartTls = true,
                StartTlsResponse = "382 Continue with TLS negotiation\r\nNOISE",
            };
            factory.Enqueue(server);
            var session = CreateSession(user: "user", password: "secret");

            var result = await session.ConnectAsync(factory, CancellationToken.None);

            Assert.Equal(ArticleRetrievalKind.ProviderFailure, result!.Kind);
            Assert.Contains("unread bytes", result.Reason, StringComparison.Ordinal);
            Assert.DoesNotContain(server.Commands, static command => command.StartsWith("AUTHINFO", StringComparison.Ordinal));
            await session.DisposeAsync();
        }

        private static NntpProviderSession CreateSession(
            ILogger? logger = null,
            string host = "127.0.0.1",
            int port = 119,
            bool useTls = false,
            string? user = null,
            string? password = null,
            bool acceptAnyCertificate = false) =>
            new(
                new BackFillerProviderDefinition("Giganews", host, port, useTls, user, password, 0, 1),
                NntpSessionOptions.Default with
                {
                    CommandTimeout = TimeSpan.FromSeconds(5),
                    ReceiveTimeout = TimeSpan.FromSeconds(5),
                    ConnectTimeout = TimeSpan.FromSeconds(5),
                    ServerCertificateValidationCallback = acceptAnyCertificate
                        ? static (_, _, _, _) => true
                        : null,
                },
                logger ?? NullLogger.Instance);

        private sealed class LoopbackStartTlsNntpServer : IAsyncDisposable
        {
            private readonly TcpListener _listener;
            private readonly X509Certificate2 _certificate;
            private readonly CancellationTokenSource _cts = new();
            private readonly Task _accept;
            private readonly List<string> _commands = [];

            public LoopbackStartTlsNntpServer(X509Certificate2 certificate)
            {
                _certificate = certificate;
                _listener = new TcpListener(IPAddress.Loopback, 0);
                _listener.Start();
                Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
                _accept = AcceptAsync(_cts.Token);
            }

            public int Port { get; }

            public bool TlsHandshakeCompleted { get; private set; }

            public IReadOnlyList<string> Commands
            {
                get
                {
                    lock (_commands)
                    {
                        return [.. _commands];
                    }
                }
            }

            public async ValueTask DisposeAsync()
            {
                await _cts.CancelAsync().ConfigureAwait(false);
                _listener.Stop();
                try
                {
                    await _accept.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                }
                catch (Exception)
                {
                }

                _cts.Dispose();
            }

            private async Task AcceptAsync(CancellationToken cancellationToken)
            {
                using var tcp = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                Stream stream = tcp.GetStream();
                await WriteAsync(stream, "200 ready\r\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
                while (!cancellationToken.IsCancellationRequested)
                {
                    var command = await ReadLineAsync(stream, cancellationToken).ConfigureAwait(false);
                    if (command is null)
                    {
                        break;
                    }

                    lock (_commands)
                    {
                        _commands.Add(command);
                    }

                    if (command.Equals("CAPABILITIES", StringComparison.OrdinalIgnoreCase))
                    {
                        await WriteAsync(
                                stream,
                                "101 Capability list follows\r\nVERSION 2\r\nSTARTTLS\r\nREADER\r\n.\r\n"u8.ToArray(),
                                cancellationToken)
                            .ConfigureAwait(false);
                        continue;
                    }

                    if (command.Equals("STARTTLS", StringComparison.OrdinalIgnoreCase))
                    {
                        await WriteAsync(
                                stream,
                                "382 Continue with TLS negotiation\r\n"u8.ToArray(),
                                cancellationToken)
                            .ConfigureAwait(false);
                        var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
                        stream = ssl;
                        await ssl.AuthenticateAsServerAsync(
                                new SslServerAuthenticationOptions
                                {
                                    ServerCertificate = _certificate,
                                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                                },
                                cancellationToken)
                            .ConfigureAwait(false);
                        TlsHandshakeCompleted = true;
                        continue;
                    }

                    if (command.StartsWith("AUTHINFO USER", StringComparison.OrdinalIgnoreCase))
                    {
                        await WriteAsync(stream, "381 password required\r\n"u8.ToArray(), cancellationToken)
                            .ConfigureAwait(false);
                        continue;
                    }

                    if (command.StartsWith("AUTHINFO PASS", StringComparison.OrdinalIgnoreCase))
                    {
                        await WriteAsync(stream, "281 Authentication accepted\r\n"u8.ToArray(), cancellationToken)
                            .ConfigureAwait(false);
                        continue;
                    }

                    if (command.Equals("QUIT", StringComparison.OrdinalIgnoreCase))
                    {
                        break;
                    }

                    await WriteAsync(stream, "500 unknown\r\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
                }

                if (stream is SslStream sslStream)
                {
                    await sslStream.DisposeAsync().ConfigureAwait(false);
                }
            }

            private static async Task WriteAsync(Stream stream, byte[] bytes, CancellationToken cancellationToken)
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            private static async Task<string?> ReadLineAsync(Stream stream, CancellationToken cancellationToken)
            {
                var builder = new List<byte>(64);
                var one = new byte[1];
                while (true)
                {
                    var read = await stream.ReadAsync(one, cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        return builder.Count == 0 ? null : Encoding.ASCII.GetString(builder.ToArray());
                    }

                    if (one[0] == (byte)'\n')
                    {
                        if (builder.Count > 0 && builder[^1] == (byte)'\r')
                        {
                            builder.RemoveAt(builder.Count - 1);
                        }

                        return Encoding.ASCII.GetString(builder.ToArray());
                    }

                    builder.Add(one[0]);
                }
            }
        }
    }
}
