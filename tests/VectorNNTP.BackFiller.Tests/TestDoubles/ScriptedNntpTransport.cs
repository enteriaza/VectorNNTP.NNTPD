using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Text;
using VectorNNTP.BackFiller.Nntp;

namespace VectorNNTP.BackFiller.Tests.TestDoubles;

internal sealed class ScriptedNntpTransportFactory : INntpTransportFactory
{
    private readonly ConcurrentQueue<ScriptedNntpServer> _servers = new();

    public List<BackFillerProviderDefinition> ConnectAttempts { get; } = [];

    public Exception? ConnectException { get; set; }

    public TimeSpan? ConnectDelay { get; set; }

    public TaskCompletionSource? ConnectStarted { get; set; }

    public TaskCompletionSource? BlockConnect { get; set; }

    public void Enqueue(ScriptedNntpServer server)
    {
        ArgumentNullException.ThrowIfNull(server);
        _servers.Enqueue(server);
    }

    public async Task<Stream> ConnectAsync(
        BackFillerProviderDefinition provider,
        NntpSessionOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ConnectAttempts.Add(provider);
        ConnectStarted?.TrySetResult();
        if (BlockConnect is not null)
        {
            await BlockConnect.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        if (ConnectDelay is { } delay)
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (ConnectException is not null)
        {
            throw ConnectException;
        }

        if (!_servers.TryDequeue(out var server))
        {
            throw new InvalidOperationException("No scripted NNTP server was queued.");
        }

        return await server.AcceptClientAsync().ConfigureAwait(false);
    }
}

internal sealed class ScriptedNntpServer
{
    private readonly Pipe _toClient = new();
    private readonly Pipe _toServer = new();
    private readonly List<byte[]> _commands = [];
    private Func<string, byte[]> _onCommand;

    public ScriptedNntpServer(string greeting = "200 posting allowed")
    {
        _onCommand = _ => "430 no such article\r\n"u8.ToArray();
        Greeting = greeting;
    }

    public string Greeting { get; }

    public IReadOnlyList<string> Commands =>
        _commands.Select(static bytes => Encoding.ASCII.GetString(bytes)).ToArray();

    public bool UseTlsRequested { get; set; }

    public bool CompleteWithoutGreeting { get; set; }

    public bool CompleteAfterResponse { get; set; }

    public bool LastCommandUsedCrlf { get; private set; }

    /// <summary>Set when an ARTICLE command is read, before any optional block.</summary>
    public TaskCompletionSource? ArticleStarted { get; set; }

    /// <summary>When set, ARTICLE waits here before the scripted response is written.</summary>
    public TaskCompletionSource? BlockArticle { get; set; }

    public void Respond(Func<string, string> responder)
    {
        ArgumentNullException.ThrowIfNull(responder);
        _onCommand = command => Encoding.ASCII.GetBytes(responder(command));
    }

    public void RespondBytes(Func<string, byte[]> responder)
    {
        ArgumentNullException.ThrowIfNull(responder);
        _onCommand = responder;
    }

    public async Task<Stream> AcceptClientAsync()
    {
        if (CompleteWithoutGreeting)
        {
            await _toClient.Writer.CompleteAsync().ConfigureAwait(false);
            return new DuplexPipeStream(_toClient.Reader, _toServer.Writer);
        }

        var greeting = Encoding.ASCII.GetBytes(Greeting.EndsWith("\r\n", StringComparison.Ordinal)
            ? Greeting
            : Greeting + "\r\n");
        _toClient.Writer.Write(greeting);
        await _toClient.Writer.FlushAsync().ConfigureAwait(false);
        _ = RunServerAsync();
        return new DuplexPipeStream(_toClient.Reader, _toServer.Writer);
    }

    private async Task RunServerAsync()
    {
        var reader = new NntpTestLineReader(_toServer.Reader);
        try
        {
            while (true)
            {
                var line = await reader.ReadLineAsync().ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                LastCommandUsedCrlf = reader.LastLineUsedCrlf;
                _commands.Add(line);
                var text = Encoding.ASCII.GetString(line);
                if (text.StartsWith("QUIT", StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                if (text.StartsWith("ARTICLE", StringComparison.OrdinalIgnoreCase))
                {
                    ArticleStarted?.TrySetResult();
                    if (BlockArticle is not null)
                    {
                        await BlockArticle.Task.ConfigureAwait(false);
                    }
                }

                var response = _onCommand(text);
                _toClient.Writer.Write(response);
                var flush = await _toClient.Writer.FlushAsync().ConfigureAwait(false);
                if (CompleteAfterResponse || flush.IsCompleted)
                {
                    break;
                }
            }
        }
        catch (Exception)
        {
        }
        finally
        {
            await _toClient.Writer.CompleteAsync().ConfigureAwait(false);
        }
    }
}

internal sealed class DuplexPipeStream(PipeReader reader, PipeWriter writer) : Stream
{
    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            var result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            var taken = (int)Math.Min(buffer.Length, result.Buffer.Length);
            if (taken == 0)
            {
                reader.AdvanceTo(result.Buffer.End);
                if (result.IsCompleted)
                {
                    return 0;
                }

                continue;
            }

            result.Buffer.Slice(0, taken).CopyTo(buffer.Span);
            reader.AdvanceTo(result.Buffer.GetPosition(taken));
            return taken;
        }
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        writer.Write(buffer.Span);
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            reader.Complete();
            writer.Complete();
        }

        base.Dispose(disposing);
    }
}

internal sealed class NntpTestLineReader(PipeReader reader)
{
    public bool LastLineUsedCrlf { get; private set; }

    public async Task<byte[]?> ReadLineAsync()
    {
        while (true)
        {
            var result = await reader.ReadAsync().ConfigureAwait(false);
            var buffer = result.Buffer;
            var position = buffer.PositionOf((byte)'\n');
            if (position is null)
            {
                reader.AdvanceTo(buffer.Start, buffer.End);
                if (result.IsCompleted)
                {
                    return null;
                }

                continue;
            }

            var line = buffer.Slice(0, position.Value);
            var bytes = line.ToArray();
            LastLineUsedCrlf = bytes.Length > 0 && bytes[^1] == (byte)'\r';
            if (LastLineUsedCrlf)
            {
                bytes = bytes[..^1];
            }

            reader.AdvanceTo(buffer.GetPosition(1, position.Value));
            return bytes;
        }
    }
}
