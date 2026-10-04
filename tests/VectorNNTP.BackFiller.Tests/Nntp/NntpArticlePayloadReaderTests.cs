using VectorNNTP.BackFiller.Nntp;

namespace VectorNNTP.BackFiller.Tests.Nntp
{
    public sealed class NntpArticlePayloadReaderTests
    {
        [Fact]
        public async Task DoubledDotAtLineStart_StoresOneDot()
        {
            var payload = await ReadAsync("..foo\r\n.\r\n"u8.ToArray());
            Assert.Equal(".foo\r\n"u8.ToArray(), payload);
        }

        [Fact]
        public async Task SingleDotAtLineStart_IsStored()
        {
            var payload = await ReadAsync(".foo\r\n.\r\n"u8.ToArray());
            Assert.Equal(".foo\r\n"u8.ToArray(), payload);
        }

        [Fact]
        public async Task DotLaterInLine_IsStored()
        {
            var payload = await ReadAsync("a.b\r\n.\r\n"u8.ToArray());
            Assert.Equal("a.b\r\n"u8.ToArray(), payload);
        }

        [Fact]
        public async Task CrlfLfCrAndEmptyLine_ArePreserved()
        {
            var payload = await ReadAsync("a\r\n\r\nb\nc\rd\r\n.\r\n"u8.ToArray());
            Assert.Equal("a\r\n\r\nb\nc\rd\r\n"u8.ToArray(), payload);
        }

        [Fact]
        public async Task FinalFragmentWithoutLineEnding_IsNotATerminator()
        {
            await Assert.ThrowsAsync<EndOfStreamException>(() => ReadAsync("abc.\r\n"u8.ToArray()));
        }

        [Fact]
        public async Task FinalLineWithoutCrlf_PreservesTrailingCr()
        {
            var payload = await ReadAsync("last\r.\r\n"u8.ToArray());
            Assert.Equal("last\r"u8.ToArray(), payload);
        }

        [Fact]
        public async Task OrdinaryRunLargerThanFourKibibytes_MatchesChunkedRead()
        {
            var payload = new byte[8192];
            for (var i = 0; i < payload.Length; i++)
            {
                payload[i] = (byte)(32 + (i % 90));
            }

            payload[100] = (byte)'\n';
            payload[101] = (byte)'.';
            payload[102] = (byte)'x';
            payload[200] = (byte)'.';
            payload[^2] = (byte)'\r';
            payload[^1] = (byte)'\n';
            var framed = new byte[payload.Length + 4];
            payload.AsSpan(..101).CopyTo(framed);
            framed[101] = (byte)'.';
            payload.AsSpan(101).CopyTo(framed.AsSpan(102));
            framed[^3] = (byte)'.';
            framed[^2] = (byte)'\r';
            framed[^1] = (byte)'\n';

            var contiguous = await ReadAsync(framed);
            var split = await ReadAsync(framed, chunk: 1);
            Assert.Equal(payload, contiguous);
            Assert.Equal(payload, split);
        }

        [Fact]
        public async Task TerminatorAfterCrLf_OmitsTerminator()
        {
            var payload = await ReadAsync("abc\r\n.\r\n"u8.ToArray());
            Assert.Equal("abc\r\n"u8.ToArray(), payload);
        }

        [Fact]
        public async Task LfTerminator_OmitsTerminator()
        {
            var payload = await ReadAsync("abc\n.\n"u8.ToArray());
            Assert.Equal("abc\n"u8.ToArray(), payload);
        }

        [Fact]
        public async Task EmptyPayload_IsEmptyArray()
        {
            var payload = await ReadAsync(".\r\n"u8.ToArray());
            Assert.Empty(payload);
        }

        [Fact]
        public async Task ExactlyMaxBytes_Succeeds()
        {
            var payload = new byte[32];
            payload.AsSpan(..30).Fill((byte)'x');
            payload[30] = (byte)'\r';
            payload[31] = (byte)'\n';
            var framed = new byte[payload.Length + 3];
            payload.CopyTo(framed);
            framed[^3] = (byte)'.';
            framed[^2] = (byte)'\r';
            framed[^1] = (byte)'\n';
            var actual = await ReadAsync(framed, maxBytes: payload.Length);
            Assert.Equal(payload, actual);
        }

        [Fact]
        public async Task ExactlyConfiguredMaximum_Succeeds()
        {
            const int maxBytes = 5 * 1024 * 1024;
            var payload = new byte[maxBytes];
            payload.AsSpan(..^2).Fill((byte)'x');
            payload[^2] = (byte)'\r';
            payload[^1] = (byte)'\n';
            var framed = new byte[payload.Length + 3];
            payload.CopyTo(framed);
            framed[^3] = (byte)'.';
            framed[^2] = (byte)'\r';
            framed[^1] = (byte)'\n';
            var actual = await ReadAsync(framed, maxBytes);
            Assert.Equal(maxBytes, actual.Length);
            Assert.Equal(payload, actual);
        }

        [Fact]
        public async Task OnePastConfiguredMaximum_Throws()
        {
            const int maxBytes = 5 * 1024 * 1024;
            var payload = new byte[maxBytes + 1];
            payload.AsSpan(..^2).Fill((byte)'y');
            payload[^2] = (byte)'\r';
            payload[^1] = (byte)'\n';
            var framed = new byte[payload.Length + 3];
            payload.CopyTo(framed);
            framed[^3] = (byte)'.';
            framed[^2] = (byte)'\r';
            framed[^1] = (byte)'\n';
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => ReadAsync(framed, maxBytes));
            Assert.Contains("MaxArticleBytes", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task OnePastMaxBytes_ThrowsAndDoesNotReturnPayload()
        {
            var payload = new byte[33];
            payload.AsSpan(..31).Fill((byte)'x');
            payload[31] = (byte)'\r';
            payload[32] = (byte)'\n';
            var framed = new byte[payload.Length + 3];
            payload.CopyTo(framed);
            framed[^3] = (byte)'.';
            framed[^2] = (byte)'\r';
            framed[^1] = (byte)'\n';
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => ReadAsync(framed, maxBytes: 32));
            Assert.Contains("MaxArticleBytes", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task EndOfStreamBeforeTerminator_Throws()
        {
            await Assert.ThrowsAsync<EndOfStreamException>(() => ReadAsync("no-terminator\r\n"u8.ToArray()));
        }

        [Fact]
        public async Task Cancellation_Throws()
        {
            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                ReadAsync("abc\r\n.\r\n"u8.ToArray(), cancellationToken: cts.Token));
        }

        [Fact]
        public async Task Timeout_Throws()
        {
            var stream = new HoldStream([(byte)'a']);
            var reader = new NntpStreamReader(stream, 1024);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                reader.ReadArticlePayloadAsync(1024, TimeSpan.FromMilliseconds(50), CancellationToken.None));
        }

        [Fact]
        public async Task SplitDotClassification_MatchesContiguousRead()
        {
            var wire = "..x\r\n.y\r\na.b\r\n.\r\n"u8.ToArray();
            var contiguous = await ReadAsync(wire);
            var split = await ReadAsync(wire, chunk: 1);
            Assert.Equal(contiguous, split);
            Assert.Equal(".x\r\n.y\r\na.b\r\n"u8.ToArray(), split);
        }

        [Fact]
        public async Task SecondArticleAfterFailure_DoesNotIncludePartialBytes()
        {
            var stream = new FailThenServeStream("PARTIAL"u8.ToArray(), "ok\r\n.\r\n"u8.ToArray());
            var reader = new NntpStreamReader(stream, 1024);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                reader.ReadArticlePayloadAsync(1024, TimeSpan.FromSeconds(5), CancellationToken.None));
            var payload = await reader.ReadArticlePayloadAsync(1024, TimeSpan.FromSeconds(5), CancellationToken.None);
            Assert.Equal("ok\r\n"u8.ToArray(), payload);
        }

        [Fact]
        public async Task Callback_ReceivesExactPayload_BeforeScratchIsReused()
        {
            var first = "aaaa\r\n.\r\n"u8.ToArray();
            var second = "bbbb\r\n.\r\n"u8.ToArray();
            var wire = new byte[first.Length + second.Length];
            first.CopyTo(wire);
            second.CopyTo(wire.AsSpan(first.Length));
            var reader = new NntpStreamReader(new MemoryStream(wire, writable: false), 1024);
            ReadOnlyMemory<byte> exposed = default;
            var seen = false;
            var marker = await reader.ReadArticlePayloadAsync(
                1024,
                TimeSpan.FromSeconds(5),
                memory =>
                {
                    seen = true;
                    exposed = memory;
                    Assert.Equal("aaaa\r\n"u8.ToArray(), memory.ToArray());
                    return memory.Length;
                },
                CancellationToken.None);

            Assert.True(seen);
            Assert.Equal(6, marker);
            var next = await reader.ReadArticlePayloadAsync(1024, TimeSpan.FromSeconds(5), CancellationToken.None);
            Assert.Equal("bbbb\r\n"u8.ToArray(), next);
            Assert.Equal("bbbb\r\n"u8.ToArray(), exposed.ToArray());
        }

        [Fact]
        public async Task Callback_Exception_StillAllowsTheNextArticle()
        {
            var first = "aaaa\r\n.\r\n"u8.ToArray();
            var second = "ok\r\n.\r\n"u8.ToArray();
            var wire = new byte[first.Length + second.Length];
            first.CopyTo(wire);
            second.CopyTo(wire.AsSpan(first.Length));
            var reader = new NntpStreamReader(new MemoryStream(wire, writable: false), 1024);
            var called = false;
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                reader.ReadArticlePayloadAsync(
                    1024,
                    TimeSpan.FromSeconds(5),
                    Fail,
                    CancellationToken.None));

            int Fail(ReadOnlyMemory<byte> memory)
            {
                called = true;
                Assert.Equal("aaaa\r\n"u8.ToArray(), memory.ToArray());
                throw new InvalidOperationException("parse failed");
            }

            Assert.True(called);
            Assert.Equal("parse failed", error.Message);
            var next = await reader.ReadArticlePayloadAsync(1024, TimeSpan.FromSeconds(5), CancellationToken.None);
            Assert.Equal("ok\r\n"u8.ToArray(), next);
        }

        [Fact]
        public async Task Callback_IsNotInvoked_WhenPayloadExceedsMaximumOrIsCancelled()
        {
            var payload = new byte[33];
            payload.AsSpan(..31).Fill((byte)'x');
            payload[31] = (byte)'\r';
            payload[32] = (byte)'\n';
            var framed = new byte[payload.Length + 3];
            payload.CopyTo(framed);
            framed[^3] = (byte)'.';
            framed[^2] = (byte)'\r';
            framed[^1] = (byte)'\n';
            var oversizeCalled = false;
            var oversize = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new NntpStreamReader(new MemoryStream(framed, writable: false), 1024).ReadArticlePayloadAsync(
                    32,
                    TimeSpan.FromSeconds(5),
                    _ =>
                    {
                        oversizeCalled = true;
                        return 0;
                    },
                    CancellationToken.None));
            Assert.Contains("MaxArticleBytes", oversize.Message, StringComparison.Ordinal);
            Assert.False(oversizeCalled);

            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();
            var cancelCalled = false;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                new NntpStreamReader(new MemoryStream("abc\r\n.\r\n"u8.ToArray(), writable: false), 1024)
                    .ReadArticlePayloadAsync(
                        1024,
                        TimeSpan.FromSeconds(5),
                        _ =>
                        {
                            cancelCalled = true;
                            return 0;
                        },
                        cts.Token));
            Assert.False(cancelCalled);
        }

        [Fact]
        public async Task TwoArticlesOnOneReader_AreIndependent()
        {
            var first = "one\r\n.\r\n"u8.ToArray();
            var second = "..two\r\n.\r\n"u8.ToArray();
            var wire = new byte[first.Length + second.Length];
            first.CopyTo(wire);
            second.CopyTo(wire.AsSpan(first.Length));
            var reader = new NntpStreamReader(new MemoryStream(wire, writable: false), 1024);
            var a = await reader.ReadArticlePayloadAsync(1024, TimeSpan.FromSeconds(5), CancellationToken.None);
            var b = await reader.ReadArticlePayloadAsync(1024, TimeSpan.FromSeconds(5), CancellationToken.None);
            Assert.Equal("one\r\n"u8.ToArray(), a);
            Assert.Equal(".two\r\n"u8.ToArray(), b);
        }

        private static async Task<byte[]> ReadAsync(
            byte[] wire,
            int maxBytes = 1024 * 1024,
            int chunk = int.MaxValue,
            CancellationToken cancellationToken = default)
        {
            Stream stream = chunk == int.MaxValue
                ? new MemoryStream(wire, writable: false)
                : new ChunkStream(wire, chunk);
            var reader = new NntpStreamReader(stream, 1024);
            return await reader.ReadArticlePayloadAsync(maxBytes, TimeSpan.FromSeconds(5), cancellationToken);
        }

        private sealed class ChunkStream(byte[] data, int chunk) : Stream
        {
            private int _pos;

            public override bool CanRead => true;

            public override bool CanSeek => false;

            public override bool CanWrite => false;

            public override long Length => data.Length;

            public override long Position { get => _pos; set => throw new NotSupportedException(); }

            public override void Flush()
            {
            }

            public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

            public override int Read(Span<byte> buffer)
            {
                if (_pos >= data.Length)
                {
                    return 0;
                }

                var n = Math.Min(chunk, Math.Min(buffer.Length, data.Length - _pos));
                data.AsSpan(_pos, n).CopyTo(buffer);
                _pos += n;
                return n;
            }

            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return new ValueTask<int>(Read(buffer.Span));
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        private sealed class HoldStream(byte[] prefix) : Stream
        {
            private int _pos;

            public override bool CanRead => true;

            public override bool CanSeek => false;

            public override bool CanWrite => false;

            public override long Length => prefix.Length;

            public override long Position { get => _pos; set => throw new NotSupportedException(); }

            public override void Flush()
            {
            }

            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
            {
                if (_pos < prefix.Length)
                {
                    var n = Math.Min(buffer.Length, prefix.Length - _pos);
                    prefix.AsSpan(_pos, n).CopyTo(buffer.Span);
                    _pos += n;
                    return n;
                }

                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
                return 0;
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        private sealed class FailThenServeStream(byte[] partial, byte[] article) : Stream
        {
            private int _phase;
            private int _pos;

            public override bool CanRead => true;

            public override bool CanSeek => false;

            public override bool CanWrite => false;

            public override long Length => partial.Length + article.Length;

            public override long Position { get => 0; set => throw new NotSupportedException(); }

            public override void Flush()
            {
            }

            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_phase == 0)
                {
                    if (_pos < partial.Length)
                    {
                        var n = Math.Min(buffer.Length, partial.Length - _pos);
                        partial.AsSpan(_pos, n).CopyTo(buffer.Span);
                        _pos += n;
                        return new ValueTask<int>(n);
                    }

                    _phase = 1;
                    _pos = 0;
                    throw new OperationCanceledException();
                }

                if (_pos >= article.Length)
                {
                    return new ValueTask<int>(0);
                }

                var count = Math.Min(buffer.Length, article.Length - _pos);
                article.AsSpan(_pos, count).CopyTo(buffer.Span);
                _pos += count;
                return new ValueTask<int>(count);
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
