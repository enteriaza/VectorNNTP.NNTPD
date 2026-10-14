using System.Net;
using System.Net.Sockets;

namespace VectorNNTP.TftpSmoke.Transport;

/// <summary>One received UDP datagram. <see cref="Count"/> bytes were written at the start of the caller-supplied buffer.</summary>
/// <param name="Count">Bytes written into the receive buffer.</param>
/// <param name="Remote">Source endpoint, which is the peer transfer identifier.</param>
public readonly record struct DatagramReceive(int Count, IPEndPoint Remote);

/// <summary>
/// One UDP socket, or a test double with the same send and receive shape.
/// Implementations must not assume a datagram contains a complete article.
/// </summary>
public interface IDatagramTransport : IAsyncDisposable
{
    /// <summary>Bound local endpoint. The port is this side's transfer identifier.</summary>
    IPEndPoint LocalEndPoint { get; }

    /// <summary>Sends one datagram to <paramref name="remote"/>.</summary>
    ValueTask SendToAsync(ReadOnlyMemory<byte> datagram, IPEndPoint remote, CancellationToken cancellationToken);

    /// <summary>Receives one datagram into <paramref name="buffer"/>.</summary>
    ValueTask<DatagramReceive> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken);
}

/// <summary>Binds datagram endpoints. Production uses UDP. Tests supply an in-memory factory.</summary>
public interface IDatagramTransportFactory
{
    /// <summary>Binds <paramref name="localEndPoint"/>. Port 0 selects an ephemeral port.</summary>
    IDatagramTransport Bind(IPEndPoint localEndPoint);
}

/// <summary>UDP sockets used by the client and server. Sockets are not connected, so an unexpected source can be answered.</summary>
public sealed class UdpDatagramTransportFactory : IDatagramTransportFactory
{
    /// <summary>Binds one IPv4 or IPv6 UDP socket.</summary>
    public IDatagramTransport Bind(IPEndPoint localEndPoint)
    {
        ArgumentNullException.ThrowIfNull(localEndPoint);
        return new UdpDatagramTransport(localEndPoint);
    }
}

/// <summary>One bound UDP socket.</summary>
public sealed class UdpDatagramTransport : IDatagramTransport
{
    private readonly Socket _socket;

    /// <summary>Binds <paramref name="localEndPoint"/> and enlarges the kernel socket buffers.</summary>
    public UdpDatagramTransport(IPEndPoint localEndPoint)
    {
        ArgumentNullException.ThrowIfNull(localEndPoint);
        _socket = new Socket(localEndPoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        _socket.Bind(localEndPoint);
        _socket.SendBufferSize = 4 * 1024 * 1024;
        _socket.ReceiveBufferSize = 8 * 1024 * 1024;
        LocalEndPoint = (IPEndPoint)_socket.LocalEndPoint!;
    }

    /// <inheritdoc />
    public IPEndPoint LocalEndPoint { get; }

    /// <inheritdoc />
    public async ValueTask SendToAsync(ReadOnlyMemory<byte> datagram, IPEndPoint remote, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(remote);
        await _socket.SendToAsync(datagram, SocketFlags.None, remote, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<DatagramReceive> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var remote = new IPEndPoint(LocalEndPoint.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
        var result = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, remote, cancellationToken).ConfigureAwait(false);
        return new DatagramReceive(result.ReceivedBytes, (IPEndPoint)result.RemoteEndPoint);
    }

    /// <summary>Closes the socket. A pending receive completes with an exception the caller treats as shutdown.</summary>
    public ValueTask DisposeAsync()
    {
        _socket.Dispose();
        return ValueTask.CompletedTask;
    }
}
