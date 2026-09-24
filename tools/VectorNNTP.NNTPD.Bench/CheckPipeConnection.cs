using System.IO.Pipelines;
using System.Net;
using VectorNNTP.NNTPD.Networking.Certificates;
using VectorNNTP.NNTPD.Networking.Proxy;
using VectorNNTP.NNTPD.Networking.Transport;

namespace VectorNNTP.NNTPD.Bench;

/// <summary>In-process duplex pipes for the CHECK session benchmark. Not a TCP socket.</summary>
internal sealed class CheckPipeConnection(PipeReader input, PipeWriter output) : INntpConnection
{
    private readonly CancellationTokenSource _cts = new();

    public PipeReader Input { get; } = input;

    public PipeWriter Output { get; } = output;

    public EndPoint? RemoteEndPoint => new IPEndPoint(IPAddress.Loopback, 1);

    public EndPoint? LocalEndPoint => null;

    public ConnectionClientIdentity ClientIdentity { get; } =
        ConnectionClientIdentity.Direct(new IPEndPoint(IPAddress.Loopback, 1));

    public bool IsTls => false;

    public bool IsCompressed => false;

    public CancellationToken ConnectionClosed => _cts.Token;

    public bool IsCompleted => _cts.IsCancellationRequested;

    public long OutboundIdleVersion => 0;

    public bool TryGetNegotiatedTlsParameters(out string tlsVersion, out string cipher)
    {
        tlsVersion = string.Empty;
        cipher = string.Empty;
        return false;
    }

    public Task PauseReadsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task WaitForOutboundDeliveryAsync(
        long outboundIdleVersionBeforeFlush,
        CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task WaitForOutboundDeliveryAndPauseReadsAsync(
        long outboundIdleVersionBeforeFlush,
        CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task CompleteAsync(Exception? exception = null)
    {
        _cts.Cancel();
        return Task.CompletedTask;
    }

    public Task UpgradeToTlsAsync(
        ITlsCertificateContextProvider certificateProvider,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task UpgradeToDeflateAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public ValueTask DisposeAsync()
    {
        _cts.Dispose();
        return ValueTask.CompletedTask;
    }
}
