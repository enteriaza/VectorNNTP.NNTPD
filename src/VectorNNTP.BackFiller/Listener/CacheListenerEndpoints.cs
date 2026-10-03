using System.Net;
using System.Net.Sockets;
using VectorNNTP.NNTPD.Networking.Listeners;

namespace VectorNNTP.BackFiller.Listener;

/// <summary>
/// Binds listen sockets from Common <see cref="ListenBinding"/> plans.
/// </summary>
internal static class CacheListenerEndpoints
{
    /// <summary>Creates, binds, and listens on <paramref name="binding"/>.</summary>
    internal static Socket CreateBoundListenSocket(ListenBinding binding)
    {
        return CreateBoundListenSocket(binding.EndPoint, binding.DualMode);
    }

    /// <summary>Creates, binds, and listens on <paramref name="endpoint"/>.</summary>
    internal static Socket CreateBoundListenSocket(IPEndPoint endpoint, bool dualMode = false)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true,
        };

        if (endpoint.AddressFamily == AddressFamily.InterNetworkV6)
        {
            socket.DualMode = dualMode;
        }

        try
        {
            socket.Bind(endpoint);
            socket.Listen(512);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
