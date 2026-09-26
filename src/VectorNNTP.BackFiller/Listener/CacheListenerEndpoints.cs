using System.Net;
using System.Net.Sockets;
using VectorNNTP.BackFiller.Configuration;
using VectorNNTP.NNTPD.Networking.Listeners;

namespace VectorNNTP.BackFiller.Listener;

/// <summary>
/// Expands Phase 1 bind-address tokens into Listener TCP endpoints.
/// </summary>
public static class CacheListenerEndpoints
{
    /// <summary>
    /// Builds the listen set. Empty tokens bind IPv4 and IPv6 wildcards.
    /// Explicit wildcard tokens do the same. Explicit addresses bind only that family.
    /// </summary>
    public static IReadOnlyList<IPEndPoint> Build(BackFillerRuntimeOptions runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        var port = runtime.BindPort;
        if (port is <= 0 or > 65535)
        {
            throw new InvalidOperationException($"Configured bind port is invalid: {port}");
        }

        var endpoints = new HashSet<IPEndPoint>(new EndpointComparer());
        var tokens = runtime.BindAddressTokens;
        if (tokens.Count == 0)
        {
            endpoints.Add(new IPEndPoint(IPAddress.Any, port));
            endpoints.Add(new IPEndPoint(IPAddress.IPv6Any, port));
            return [.. endpoints];
        }

        foreach (var token in tokens)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                continue;
            }

            var trimmed = token.Trim();
            if (BackFillerOptions.IsBindAddressWildcard(trimmed))
            {
                endpoints.Add(new IPEndPoint(IPAddress.Any, port));
                endpoints.Add(new IPEndPoint(IPAddress.IPv6Any, port));
                continue;
            }

            if (!IPAddress.TryParse(trimmed, out var address))
            {
                throw new InvalidOperationException($"Configured bind address token is invalid: '{trimmed}'.");
            }

            endpoints.Add(new IPEndPoint(address, port));
        }

        return [.. endpoints];
    }

    /// <summary>Creates, binds, and listens on <paramref name="binding"/>.</summary>
    public static Socket CreateBoundListenSocket(ListenBinding binding)
    {
        return CreateBoundListenSocket(binding.EndPoint, binding.DualMode);
    }

    /// <summary>Creates, binds, and listens on <paramref name="endpoint"/>.</summary>
    public static Socket CreateBoundListenSocket(IPEndPoint endpoint, bool dualMode = false)
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

    private sealed class EndpointComparer : IEqualityComparer<IPEndPoint>
    {
        public bool Equals(IPEndPoint? x, IPEndPoint? y) =>
            x is not null && y is not null && x.Address.Equals(y.Address) && x.Port == y.Port;

        public int GetHashCode(IPEndPoint obj) => HashCode.Combine(obj.Address, obj.Port);
    }
}
