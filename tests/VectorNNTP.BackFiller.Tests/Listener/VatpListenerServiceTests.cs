using System.Buffers;
using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTP.BackFiller.Configuration;
using VectorNNTP.BackFiller.Listener;
using VectorNNTP.BackFiller.Retention;
using VectorNNTP.BackFiller.Tests.Fixtures;
using VectorNNTP.BackFiller.Tests.Retention;
using VectorNNTP.BackFiller.Tests.TestDoubles;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Articles.Parsing;
using VectorNNTP.Common.Transport.ArticleTransfer;
using VectorNNTP.Common.Networking.Certificates;

namespace VectorNNTP.BackFiller.Tests.Listener
{
    public sealed class VatpListenerServiceTests
    {
        private const int LargeBodyBytes = 300 * 1024;
        private const string BodyLine = "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx\r\n";

        [Fact]
        public async Task Handshake_successful_tls_and_hello_exchange()
        {
            await using var context = await VatpListenerContext.StartAsync();
            await using var client = await ConnectAsync(context);
            await ExchangeHelloAsync(client.Stream);
        }

        [Fact]
        public async Task Handshake_bad_magic_yields_fail()
        {
            await using var context = await VatpListenerContext.StartAsync();
            await using var client = await ConnectAsync(context);
            var payload = new byte[VatpProtocol.HelloPayloadLength];
            "BADMAG01"u8.CopyTo(payload);
            BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(8, 4), VatpProtocol.DefaultMaxFramePayload);
            await client.Stream.WriteAsync(
                VatpFrameEncoder.ToSingleBuffer(
                    VatpFrameEncoder.Encode(VatpFrameType.Hello, VatpProtocol.ConnectionStreamId, payload, 0)));
            var fail = await ReadVatpFrameAsync(client.Stream);
            Assert.Equal(VatpFrameType.Fail, fail.Header.Type);
            Assert.Equal(VatpErrorCode.InvalidHello, ReadFailCode(fail));
        }

        [Fact]
        public async Task Handshake_invalid_version_terminates()
        {
            await using var context = await VatpListenerContext.StartAsync();
            await using var client = await ConnectAsync(context);
            var frame = VatpFrameEncoder.ToSingleBuffer(VatpFrameEncoder.EncodeHello(VatpProtocol.DefaultMaxFramePayload));
            frame[0] = 0x02;
            await client.Stream.WriteAsync(frame);
            var fail = await ReadVatpFrameAsync(client.Stream);
            Assert.Equal(VatpFrameType.Fail, fail.Header.Type);
            Assert.Equal(VatpErrorCode.UnsupportedVersion, ReadFailCode(fail));
            await WaitForIdleAsync(context);
        }

        [Fact]
        public async Task Handshake_invalid_stream_id_on_hello_yields_fail()
        {
            await using var context = await VatpListenerContext.StartAsync();
            await using var client = await ConnectAsync(context);
            var frame = VatpFrameEncoder.ToSingleBuffer(VatpFrameEncoder.EncodeHello(VatpProtocol.DefaultMaxFramePayload));
            BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(4, 4), 1);
            await client.Stream.WriteAsync(frame);
            var fail = await ReadVatpFrameAsync(client.Stream);
            Assert.Equal(VatpErrorCode.InvalidStreamId, ReadFailCode(fail));
        }

        [Fact]
        public async Task Handshake_invalid_max_frame_yields_fail()
        {
            await using var context = await VatpListenerContext.StartAsync();
            await using var client = await ConnectAsync(context);
            var payload = new byte[VatpProtocol.HelloPayloadLength];
            VatpProtocol.HelloMagic.CopyTo(payload);
            BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(8, 4), 0);
            await client.Stream.WriteAsync(
                VatpFrameEncoder.ToSingleBuffer(
                    VatpFrameEncoder.Encode(VatpFrameType.Hello, VatpProtocol.ConnectionStreamId, payload, 0)));
            var fail = await ReadVatpFrameAsync(client.Stream);
            Assert.Equal(VatpErrorCode.InvalidMaxFramePayload, ReadFailCode(fail));
            await WaitForIdleAsync(context);
        }

        [Fact]
        public async Task Open_before_hello_closes_connection()
        {
            await using var context = await VatpListenerContext.StartAsync();
            var prepared = RetainArticle(context.Authority, "<open-before-hello@example.test>");
            await using var client = await ConnectAsync(context);
            Span<byte> artId = stackalloc byte[VatpProtocol.ArticleIdLength];
            prepared.Record.ArtId.CopyTo(artId);
            await client.Stream.WriteAsync(
                VatpFrameEncoder.ToSingleBuffer(VatpFrameEncoder.EncodeOpen(1, prepared.RequestId, artId)));
            await WaitForIdleAsync(context);
        }

        [Fact]
        public async Task Duplicate_hello_yields_fail()
        {
            await using var context = await VatpListenerContext.StartAsync();
            await using var client = await ConnectAsync(context);
            await ExchangeHelloAsync(client.Stream);
            await client.Stream.WriteAsync(
                VatpFrameEncoder.ToSingleBuffer(VatpFrameEncoder.EncodeHello(VatpProtocol.DefaultMaxFramePayload)));
            var fail = await ReadVatpFrameAsync(client.Stream);
            Assert.Equal(VatpErrorCode.InvalidHello, ReadFailCode(fail));
        }

        [Fact]
        public async Task Truncated_hello_then_close_is_safe()
        {
            await using var context = await VatpListenerContext.StartAsync();
            var frame = VatpFrameEncoder.ToSingleBuffer(VatpFrameEncoder.EncodeHello(VatpProtocol.DefaultMaxFramePayload));
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, context.Port);
            var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false, static (_, _, _, _) => true);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "localhost",
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            });
            await ssl.WriteAsync(frame.AsMemory(0, 8));
            ssl.Dispose();
            tcp.Close();
            await WaitForIdleAsync(context);
            Assert.Equal(CacheListenerState.Running, context.Service.State);
        }

        [Fact]
        public async Task Single_transfer_meta_data_end_matches_canonical_record()
        {
            await using var context = await VatpListenerContext.StartAsync();
            var prepared = RetainArticle(context.Authority, "<vatp@example.test>", "body\r\n");
            await using var client = await ConnectAsync(context);
            await ExchangeHelloAsync(client.Stream);
            await SendOpenAsync(client.Stream, 1, prepared.RequestId, prepared.Record.ArtId);
            var received = await ReceiveTransferAsync(client.Stream, 1, prepared.RequestId, prepared.Record.ArtId);
            Assert.Equal(prepared.Record.ArtHash, received.ArtHash);
            Assert.Equal(prepared.Record.ArtSize, received.ArtSize);
            Assert.Equal(prepared.Record.ArtId, received.ArtId);
            Assert.True(received.ArtData.Span.SequenceEqual(prepared.ExpectedArtData));
        }

        [Fact]
        public async Task Multiplex_interleaved_transfers_complete()
        {
            await using var context = await VatpListenerContext.StartAsync(maxBytes: 16 * 1024 * 1024);
            var articles = new PreparedArticle[8];
            for (var i = 0; i < articles.Length; i++)
            {
                articles[i] = RetainArticle(context.Authority, $"<vatp-mux{i}@example.test>", $"body-{i}\r\n");
            }

            await using var client = await ConnectAsync(context);
            await ExchangeHelloAsync(client.Stream);
            for (var i = 0; i < articles.Length; i++)
            {
                await SendOpenAsync(client.Stream, (uint)(i + 1), articles[i].RequestId, articles[i].Record.ArtId);
            }

            var receivers = new Dictionary<uint, ManualTransferReceiver>();
            for (var i = 0; i < articles.Length; i++)
            {
                var streamId = (uint)(i + 1);
                receivers[streamId] = new ManualTransferReceiver(articles[i].Record.ArtId);
            }

            var completed = 0;
            while (completed < articles.Length)
            {
                var frame = await ReadVatpFrameAsync(client.Stream);
                if (!receivers.TryGetValue(frame.Header.StreamId, out var receiver))
                {
                    continue;
                }

                if (frame.Header.Type == VatpFrameType.Fail)
                {
                    Assert.Fail($"Unexpected FAIL: {ReadFailCode(frame)}");
                }

                if (ApplyFrame(client.Stream, frame, receiver, grantServerWindow: true)
                    && receiver.IsComplete)
                {
                    var expected = articles[(int)frame.Header.StreamId - 1];
                    Assert.True(receiver.Record!.Value.ArtData.Span.SequenceEqual(expected.ExpectedArtData));
                    receivers.Remove(frame.Header.StreamId);
                    completed++;
                }
            }
        }

        [Fact]
        public async Task Window_on_unknown_stream_fails()
        {
            await using var context = await VatpListenerContext.StartAsync();
            await using var client = await ConnectAsync(context);
            await ExchangeHelloAsync(client.Stream);
            await client.Stream.WriteAsync(VatpFrameEncoder.ToSingleBuffer(VatpFrameEncoder.EncodeWindow(99, 1024)));
            var fail = await ReadVatpFrameAsync(client.Stream);
            Assert.Equal(VatpErrorCode.UnknownStream, ReadFailCode(fail));
        }

        [Fact]
        public async Task Window_after_open_is_accepted()
        {
            await using var context = await VatpListenerContext.StartAsync();
            var prepared = RetainArticle(context.Authority, "<window-after-open@example.test>");
            await using var client = await ConnectAsync(context);
            await ExchangeHelloAsync(client.Stream);
            await SendOpenAsync(client.Stream, 1, prepared.RequestId, prepared.Record.ArtId);
            await client.Stream.WriteAsync(VatpFrameEncoder.ToSingleBuffer(VatpFrameEncoder.EncodeWindow(1, 4096)));
            _ = await ReceiveTransferAsync(client.Stream, 1, prepared.RequestId, prepared.Record.ArtId);
        }

        [Fact]
        public async Task Zero_window_on_one_stream_does_not_starve_another()
        {
            await using var context = await VatpListenerContext.StartAsync(maxBytes: 16 * 1024 * 1024);
            var largeBody = BuildLargeBody(LargeBodyBytes);
            var slow = RetainArticle(context.Authority, "<slow-large@example.test>", largeBody);
            var fast = RetainArticle(context.Authority, "<fast-small@example.test>", "tiny\r\n");

            await using var client = await ConnectAsync(context);
            await ExchangeHelloAsync(client.Stream);
            await SendOpenAsync(client.Stream, 1, slow.RequestId, slow.Record.ArtId);
            await SendOpenAsync(client.Stream, 2, fast.RequestId, fast.Record.ArtId);

            var slowReceiver = new ManualTransferReceiver(slow.Record.ArtId);
            var fastReceiver = new ManualTransferReceiver(fast.Record.ArtId);
            var fastCompleted = false;
            var slowDataFrames = 0;
            var slowStalled = false;

            while (!fastCompleted || !slowReceiver.IsComplete)
            {
                var frame = await ReadVatpFrameAsync(client.Stream);
                if (frame.Header.StreamId == 2)
                {
                    fastCompleted = ApplyFrame(client.Stream, frame, fastReceiver, grantServerWindow: true);
                }
                else if (frame.Header.StreamId == 1)
                {
                    var grant = slowStalled;
                    if (frame.Header.Type == VatpFrameType.Data)
                    {
                        slowDataFrames++;
                        if (slowDataFrames >= 4 && !slowStalled)
                        {
                            slowStalled = true;
                            grant = false;
                        }
                    }

                    ApplyFrame(client.Stream, frame, slowReceiver, grantServerWindow: grant);
                }
            }

            Assert.True(fastCompleted);
            Assert.True(slowReceiver.IsComplete);
            var slowRecord = slowReceiver.Record!.Value;
            var fastRecord = fastReceiver.Record!.Value;
            Assert.True(slowRecord.ArtData.Span.SequenceEqual(slow.ExpectedArtData));
            Assert.True(fastRecord.ArtData.Span.SequenceEqual(fast.ExpectedArtData));
        }

        [Fact]
        public async Task Cancel_mid_transfer_leaves_other_stream_complete()
        {
            await using var context = await VatpListenerContext.StartAsync(maxBytes: 4 * 1024 * 1024);
            var cancelBody = BuildLargeBody(64 * 1024);
            var cancelled = RetainArticle(context.Authority, "<cancel-me@example.test>", cancelBody);
            var completes = RetainArticle(context.Authority, "<complete-me@example.test>", "done\r\n");

            await using var client = await ConnectAsync(context);
            await ExchangeHelloAsync(client.Stream);
            await SendOpenAsync(client.Stream, 1, cancelled.RequestId, cancelled.Record.ArtId);
            await SendOpenAsync(client.Stream, 2, completes.RequestId, completes.Record.ArtId);

            var cancelReceiver = new ManualTransferReceiver(cancelled.Record.ArtId);
            var completeReceiver = new ManualTransferReceiver(completes.Record.ArtId);
            var sentCancel = false;
            var secondDone = false;

            while (!secondDone)
            {
                var frame = await ReadVatpFrameAsync(client.Stream);
                if (frame.Header.StreamId == 1)
                {
                    if (frame.Header.Type == VatpFrameType.Data && !sentCancel)
                    {
                        sentCancel = true;
                        await client.Stream.WriteAsync(VatpFrameEncoder.ToSingleBuffer(VatpFrameEncoder.EncodeCancel(1)));
                    }

                    ApplyFrame(client.Stream, frame, cancelReceiver, grantServerWindow: true);
                }
                else if (frame.Header.StreamId == 2)
                {
                    secondDone = ApplyFrame(client.Stream, frame, completeReceiver, grantServerWindow: true);
                }
            }

            Assert.True(completeReceiver.IsComplete);
            Assert.False(cancelReceiver.IsComplete);
        }

        [Fact]
        public async Task Retention_wrong_article_id_then_correct_open()
        {
            await using var context = await VatpListenerContext.StartAsync();
            var prepared = RetainArticle(context.Authority, "<retention-wrong-id@example.test>");
            await using var client = await ConnectAsync(context);
            await ExchangeHelloAsync(client.Stream);
            var wrong = ArticleId.FromMessageId("<wrong@example.test>"u8);
            await SendOpenAsync(client.Stream, 1, prepared.RequestId, wrong);
            var fail = await ReadVatpFrameAsync(client.Stream);
            Assert.Equal(VatpErrorCode.OpenRejected, ReadFailCode(fail));

            await SendOpenAsync(client.Stream, 2, prepared.RequestId, prepared.Record.ArtId);
            _ = await ReceiveTransferAsync(client.Stream, 2, prepared.RequestId, prepared.Record.ArtId);
        }

        [Fact]
        public async Task Retention_second_open_same_request_id_rejected()
        {
            await using var context = await VatpListenerContext.StartAsync();
            var prepared = RetainArticle(context.Authority, "<second-open@example.test>");
            await using var client = await ConnectAsync(context);
            await ExchangeHelloAsync(client.Stream);
            await SendOpenAsync(client.Stream, 1, prepared.RequestId, prepared.Record.ArtId);
            _ = await ReceiveTransferAsync(client.Stream, 1, prepared.RequestId, prepared.Record.ArtId);

            await SendOpenAsync(client.Stream, 2, prepared.RequestId, prepared.Record.ArtId);
            VatpErrorCode rejected = VatpErrorCode.None;
            while (rejected != VatpErrorCode.OpenRejected)
            {
                var frame = await ReadVatpFrameAsync(client.Stream);
                if (frame.Header.Type == VatpFrameType.Fail)
                {
                    rejected = ReadFailCode(frame);
                }
            }

            Assert.Equal(VatpErrorCode.OpenRejected, rejected);
        }

        [Fact]
        public async Task Retention_unknown_request_id_rejected()
        {
            await using var context = await VatpListenerContext.StartAsync();
            var prepared = RetainArticle(context.Authority, "<unknown-request@example.test>");
            await using var client = await ConnectAsync(context);
            await ExchangeHelloAsync(client.Stream);
            await SendOpenAsync(client.Stream, 1, Guid.NewGuid(), prepared.Record.ArtId);
            var fail = await ReadVatpFrameAsync(client.Stream);
            Assert.Equal(VatpErrorCode.OpenRejected, ReadFailCode(fail));
        }

        [Fact]
        public async Task Retention_expired_request_id_rejected()
        {
            var time = new ManualTimeProvider(new DateTimeOffset(2026, 9, 26, 12, 0, 0, TimeSpan.Zero));
            await using var context = await VatpListenerContext.StartAsync(time: time, ttl: TimeSpan.FromSeconds(20));
            var prepared = RetainArticle(context.Authority, "<expired-request@example.test>");
            time.Advance(TimeSpan.FromSeconds(20));
            await using var client = await ConnectAsync(context);
            await ExchangeHelloAsync(client.Stream);
            await SendOpenAsync(client.Stream, 1, prepared.RequestId, prepared.Record.ArtId);
            var fail = await ReadVatpFrameAsync(client.Stream);
            Assert.Equal(VatpErrorCode.OpenRejected, ReadFailCode(fail));
        }

        [Fact]
        public async Task Disconnect_mid_transfer_releases_lease_but_retains_entry()
        {
            var time = new ManualTimeProvider(new DateTimeOffset(2026, 9, 26, 12, 0, 0, TimeSpan.Zero));
            await using var context = await VatpListenerContext.StartAsync(time: time, maxBytes: 4 * 1024 * 1024);
            var prepared = RetainArticle(
                context.Authority,
                "<disconnect-mid@example.test>",
                BuildLargeBody(128 * 1024));
            Assert.Equal(1, context.Authority.RetainedCount);

            var client = await ConnectAsync(context);
            await ExchangeHelloAsync(client.Stream);
            await SendOpenAsync(client.Stream, 1, prepared.RequestId, prepared.Record.ArtId);
            _ = await ReadVatpFrameAsync(client.Stream);
            await client.DisposeAsync();
            await WaitForIdleAsync(context);
            Assert.Equal(0, context.Service.ActiveConnections);
            Assert.Equal(1, context.Authority.RetainedCount);

            // Entry remains retained; a fresh RequestId can reopen the same ArtData.
            var reattach = RetentionTestArticles.Create("<disconnect-mid@example.test>", BuildLargeBody(128 * 1024));
            Assert.Equal(ArticleRetentionKind.AlreadyPresent, context.Authority.RetainCanonical(
                reattach.MessageId,
                reattach.RequestId,
                reattach.Record,
                reattach.SelectedDateHeaderName).Kind);
            using var open = context.Authority.TryOpenTransfer(reattach.RequestId, prepared.Record.ArtId);
            Assert.Equal(VatpOpenKind.Opened, open.Kind);
            Assert.True(open.Lease!.Record.ArtData.Span.SequenceEqual(prepared.ExpectedArtData));
        }

        [Fact]
        public async Task Failure_malformed_frame_after_hello_closes_connection()
        {
            await using var context = await VatpListenerContext.StartAsync();
            await using var client = await ConnectAsync(context);
            await ExchangeHelloAsync(client.Stream);
            var header = new byte[VatpProtocol.HeaderLengthBytes];
            VatpFrameHeader.Create((VatpFrameType)0xFE, 1, 0).WriteTo(header);
            await client.Stream.WriteAsync(header);
            await client.DisposeAsync();
            await WaitForIdleAsync(context);
        }

        [Fact]
        public async Task Legacy_cache_get_as_first_frame_is_not_supported()
        {
            await using var context = await VatpListenerContext.StartAsync();
            _ = RetainArticle(context.Authority, ArticleWorkTestDeliveries.CanonicalMessageId);
            await using var client = await ConnectAsync(context);
            // Former MD5 cache GET request bytes are not a VATP HELLO.
            var legacyGet = new byte[VatpProtocol.HeaderLengthBytes + 32];
            legacyGet[0] = 0x01;
            BinaryPrimitives.WriteUInt32BigEndian(legacyGet.AsSpan(4, 4), 21);
            BinaryPrimitives.WriteUInt32BigEndian(legacyGet.AsSpan(8, 4), 32);
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"u8.CopyTo(legacyGet.AsSpan(VatpProtocol.HeaderLengthBytes));
            await client.Stream.WriteAsync(legacyGet);
            var fail = await ReadVatpFrameAsync(client.Stream);
            Assert.Equal(VatpFrameType.Fail, fail.Header.Type);
            await WaitForIdleAsync(context);
        }

        [Fact]
        public async Task Resource_duplicate_stream_id_is_rejected()
        {
            await using var context = await VatpListenerContext.StartAsync();
            var first = RetainArticle(context.Authority, "<stream-dup-a@example.test>", BuildLargeBody(128 * 1024));
            var second = RetainArticle(context.Authority, "<stream-dup-b@example.test>");
            await using var client = await ConnectAsync(context);
            await ExchangeHelloAsync(client.Stream);
            await SendOpenAsync(client.Stream, 1, first.RequestId, first.Record.ArtId);
            await SendOpenAsync(client.Stream, 1, second.RequestId, second.Record.ArtId);
            Assert.Equal(VatpErrorCode.StreamTableError, await DrainUntilFailAsync(client.Stream));
        }

        private static bool ApplyFrame(
            Stream stream,
            VatpParsedFrame frame,
            ManualTransferReceiver receiver,
            bool grantServerWindow)
        {
            switch (frame.Header.Type)
            {
                case VatpFrameType.Meta:
                    receiver.AcceptMeta(PayloadToArray(frame.Payload));
                    return false;
                case VatpFrameType.Data:
                    var chunk = PayloadToArray(frame.Payload);
                    receiver.AcceptData(chunk, frame.Header.HasFin);
                    if (grantServerWindow)
                    {
                        stream.Write(VatpFrameEncoder.ToSingleBuffer(
                            VatpFrameEncoder.EncodeWindow(frame.Header.StreamId, (uint)chunk.Length)));
                    }

                    return receiver.IsComplete;
                case VatpFrameType.End:
                    receiver.AcceptEnd();
                    return receiver.IsComplete;
                default:
                    return false;
            }
        }

        private static async Task ExchangeHelloAsync(SslStream stream)
        {
            await stream.WriteAsync(
                VatpFrameEncoder.ToSingleBuffer(VatpFrameEncoder.EncodeHello(VatpProtocol.DefaultMaxFramePayload)));
            var serverHello = await ReadVatpFrameAsync(stream);
            Assert.Equal(VatpFrameType.Hello, serverHello.Header.Type);
            Assert.True(
                VatpHello.TryDecode(PayloadToArray(serverHello.Payload), out var hello, out var error),
                error.ToString());
            Assert.Equal(VatpProtocol.DefaultMaxFramePayload, hello.MaxFramePayload);
        }

        private static async Task SendOpenAsync(Stream stream, uint streamId, Guid requestId, ArticleId articleId)
        {
            Span<byte> idBytes = stackalloc byte[VatpProtocol.ArticleIdLength];
            articleId.CopyTo(idBytes);
            await stream.WriteAsync(VatpFrameEncoder.ToSingleBuffer(VatpFrameEncoder.EncodeOpen(streamId, requestId, idBytes)));
        }

        private static async Task<ArticleRecord> ReceiveTransferAsync(
            Stream stream,
            uint streamId,
            Guid requestId,
            ArticleId expectedArtId)
        {
            var receiver = new ArticleTransferReceiveStream(streamId, requestId, expectedArtId);
            while (!receiver.IsTerminal)
            {
                var frame = await ReadVatpFrameAsync(stream);
                if (frame.Header.StreamId != streamId)
                {
                    continue;
                }

                switch (frame.Header.Type)
                {
                    case VatpFrameType.Meta:
                        Assert.True(receiver.TryAcceptMeta(PayloadToArray(frame.Payload)).Success);
                        break;
                    case VatpFrameType.Data:
                        var chunk = PayloadToArray(frame.Payload);
                        Assert.True(receiver.TryAcceptData(chunk, frame.Header.HasFin).Success);
                        if (!receiver.IsTerminal)
                        {
                            await stream.WriteAsync(
                                VatpFrameEncoder.ToSingleBuffer(VatpFrameEncoder.EncodeWindow(streamId, (uint)chunk.Length)));
                        }

                        break;
                    case VatpFrameType.End:
                        Assert.True(receiver.TryAcceptEnd().Success);
                        break;
                    case VatpFrameType.Fail:
                        Assert.Fail($"Unexpected FAIL {ReadFailCode(frame)}");
                        break;
                }
            }

            Assert.True(receiver.TryTakeRecord(out var record));
            return record;
        }

        private static PreparedArticle RetainArticle(
            ArticleRetentionAuthority authority,
            string messageId,
            string body = "body\r\n")
        {
            var parser = new NntpArticleParser("backfiller.test");
            var destuffed = BuildDestuffed(messageId, body);
            var created = ArticleRecordFactory.TryCreate(parser, destuffed);
            Assert.True(created.IsAccepted, created.ParseFailure.ToString());
            var requestId = Guid.NewGuid();
            var retained = authority.RetainCanonical(
                messageId,
                requestId,
                created.Record,
                created.SelectedDateHeaderName);
            Assert.Equal(ArticleRetentionKind.Retained, retained.Kind);
            return new PreparedArticle(
                requestId,
                created.Record,
                created.SelectedDateHeaderName,
                created.Record.ArtData.ToArray());
        }

        private static string BuildLargeBody(int minimumBytes)
        {
            var builder = new StringBuilder(minimumBytes + BodyLine.Length);
            while (builder.Length < minimumBytes)
            {
                builder.Append(BodyLine);
            }

            return builder.ToString();
        }

        private static async Task<VatpErrorCode> ReadUntilFailAsync(Stream stream)
        {
            while (true)
            {
                var frame = await ReadVatpFrameAsync(stream);
                if (frame.Header.Type == VatpFrameType.Fail)
                {
                    return ReadFailCode(frame);
                }
            }
        }

        private static async Task<VatpErrorCode> DrainUntilFailAsync(Stream stream)
        {
            while (true)
            {
                var frame = await ReadVatpFrameAsync(stream);
                switch (frame.Header.Type)
                {
                    case VatpFrameType.Fail:
                        return ReadFailCode(frame);
                    case VatpFrameType.Data:
                        var chunk = PayloadToArray(frame.Payload);
                        await stream.WriteAsync(
                            VatpFrameEncoder.ToSingleBuffer(
                                VatpFrameEncoder.EncodeWindow(frame.Header.StreamId, (uint)chunk.Length)));
                        break;
                }
            }
        }

        private static byte[] BuildDestuffed(string messageId, string body) =>
            Encoding.ASCII.GetBytes(
                "Path: peer.example\r\n"
                + "Date: Fri, 23 Aug 2024 07:30:10 +0000\r\n"
                + "Message-ID: " + messageId + "\r\n"
                + "Newsgroups: alt.test\r\n"
                + "From: user@example.test\r\n"
                + "Subject: s\r\n"
                + "\r\n"
                + body);

        private static async Task<VatpParsedFrame> ReadVatpFrameAsync(Stream stream)
        {
            var headerBytes = new byte[VatpProtocol.HeaderLengthBytes];
            await stream.ReadExactlyAsync(headerBytes);
            var header = VatpFrameHeader.ReadFrom(headerBytes);
            var payload = new byte[header.PayloadLength];
            if (payload.Length > 0)
            {
                await stream.ReadExactlyAsync(payload);
            }

            return new VatpParsedFrame(header, new ReadOnlySequence<byte>(payload));
        }

        private static VatpErrorCode ReadFailCode(VatpParsedFrame frame)
        {
            var payload = PayloadToArray(frame.Payload);
            Assert.True(payload.Length >= VatpProtocol.FailMinPayloadLength);
            return (VatpErrorCode)BinaryPrimitives.ReadUInt16BigEndian(payload);
        }

        private static byte[] PayloadToArray(in ReadOnlySequence<byte> payload)
        {
            if (payload.IsEmpty)
            {
                return [];
            }

            if (payload.IsSingleSegment)
            {
                return payload.FirstSpan.ToArray();
            }

            var buffer = new byte[payload.Length];
            payload.CopyTo(buffer);
            return buffer;
        }

        private static async Task WaitForIdleAsync(VatpListenerContext context) =>
            await ArticleWorkTestDeliveries.WaitUntilAsync(
                () => context.Service.State == CacheListenerState.Running && context.Service.ActiveConnections == 0,
                TimeSpan.FromSeconds(5));

        private static async Task<TlsClient> ConnectAsync(
            VatpListenerContext context,
            SslProtocols protocols = SslProtocols.Tls12 | SslProtocols.Tls13)
        {
            var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, context.Port);
            var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false, static (_, _, _, _) => true);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "localhost",
                EnabledSslProtocols = protocols,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            });
            return new TlsClient(tcp, ssl);
        }

        private static BackFillerRuntimeOptions CreateRuntime(int port, int maxConnections = 8)
        {
            var options = BackFillerTestOptions.CreateValid();
            options.BindPortTls = port;
            options.BindAddress = ["127.0.0.1"];
            var runtime = BackFillerRuntimeOptionsFactory.Create(
                options,
                BackFillerTestOptions.CreateValidNntpDb());
            return runtime with
            {
                Listener = runtime.Listener with { MaxActiveConnections = maxConnections },
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

        private sealed class ManualTransferReceiver(ArticleId expectedArtId)
        {
            private ArticleCanonicalTransferMeta _meta;
            private byte[]? _artData;
            private int _received;
            private bool _finSeen;

            public bool IsComplete { get; private set; }

            public ArticleRecord? Record { get; private set; }

            public void AcceptMeta(byte[] metaPayload)
            {
                Assert.True(VatpMetaCodec.TryDecode(metaPayload, out _meta, out _));
                _artData = new byte[_meta.ArtSize];
                _received = 0;
                _finSeen = false;
            }

            public void AcceptData(byte[] chunk, bool fin)
            {
                chunk.CopyTo(_artData!.AsSpan(_received));
                _received += chunk.Length;
                if (fin)
                {
                    _finSeen = true;
                }
            }

            public void AcceptEnd()
            {
                if (_finSeen && _received == _meta.ArtSize)
                {
                    FinalizeRecord();
                }
            }

            private void FinalizeRecord()
            {
                var created = ArticleRecordFactory.TryCreateFromCanonicalTransfer(_artData!, in _meta, in expectedArtId);
                Assert.True(created.IsAccepted, created.MaterializeFailure.ToString());
                Record = created.Record;
                IsComplete = true;
            }
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

        private sealed class VatpListenerContext : IAsyncDisposable
        {
            private VatpListenerContext(
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

            public TlsCertificateContextProvider Certificates { get; }

            public int Port { get; }

            public static async Task<VatpListenerContext> StartAsync(
                TimeProvider? time = null,
                TimeSpan? ttl = null,
                int maxBytes = 16 * 1024 * 1024,
                BackFillerRuntimeOptions? runtime = null)
            {
                runtime ??= CreateRuntime(GetFreePort());
                var authority = ArticleRetentionAuthorityTests.Create(time ?? TimeProvider.System, maxBytes, ttl);
                var certificates = TestListenerCertificates.CreatePublishedProvider();
                var service = new CacheListenerService(
                    runtime,
                    certificates,
                    authority,
                    NullLogger<CacheListenerService>.Instance);
                await service.StartAsync(CancellationToken.None);
                return new VatpListenerContext(service, authority, runtime.BindPortTls, certificates);
            }

            public async ValueTask DisposeAsync()
            {
                await Service.DisposeAsync();
                await Authority.DisposeAsync();
                await Certificates.DisposeAsync();
            }
        }

        private readonly record struct PreparedArticle(
            Guid RequestId,
            ArticleRecord Record,
            NntpArticleHeaderName SelectedDateHeaderName,
            byte[] ExpectedArtData);
    }
}
