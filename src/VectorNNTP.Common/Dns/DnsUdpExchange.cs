using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace VectorNNTP.NNTPD.Dns;

/// <summary>
/// Pooled UDP DNS exchange with remote-endpoint source validation.
/// </summary>
/// <remarks>
/// <para>
/// After sending to a specific nameserver, datagrams are accepted only when
/// <see cref="UdpReceiveResult.RemoteEndPoint"/> matches that address and port 53.
/// Non-matching datagrams are discarded until the receive timeout elapses.
/// </para>
/// <para>
/// Validation uses <see cref="IPEndPoint"/> equality on the address and port returned by
/// <see cref="UdpClient.ReceiveAsync(System.Threading.CancellationToken)"/>, which is straightforward
/// on win-x64 / .NET 10 with unbound pooled sockets.
/// </para>
/// </remarks>
internal static class DnsUdpExchange
{
    private const int PoolMaxSize = 8;

    private static readonly ConcurrentBag<UdpClient> Pool = [];

    /// <summary>
    /// Outcome of a single UDP DNS exchange attempt.
    /// </summary>
    public readonly struct Result
    {
        /// <summary>Initializes a successful exchange.</summary>
        public Result(byte[] buffer)
        {
            Buffer = buffer;
            TimedOut = false;
            Failed = false;
        }

        private Result(bool timedOut, bool failed)
        {
            Buffer = null;
            TimedOut = timedOut;
            Failed = failed;
        }

        /// <summary>Response bytes when the exchange succeeded; otherwise <see langword="null"/>.</summary>
        public byte[]? Buffer { get; }

        /// <summary><see langword="true"/> when the receive window elapsed without a matching datagram.</summary>
        public bool TimedOut { get; }

        /// <summary><see langword="true"/> when a socket error prevented a usable response.</summary>
        public bool Failed { get; }

        /// <summary>Creates a timeout outcome.</summary>
        public static Result Timeout() => new(timedOut: true, failed: false);

        /// <summary>Creates a hard-failure outcome.</summary>
        public static Result Failure() => new(timedOut: false, failed: true);
    }

    /// <summary>
    /// Sends <paramref name="query"/> to <paramref name="destination"/>:53 and returns the first
    /// response whose remote endpoint matches that destination.
    /// </summary>
    public static async Task<Result> QueryAsync(
        IPAddress destination,
        byte[] query,
        int timeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(query);

        var expected = new IPEndPoint(destination, 53);
        UdpClient udp = Rent(destination.AddressFamily);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeoutMilliseconds);

        try
        {
            _ = await udp.SendAsync(query, expected, timeoutCts.Token).ConfigureAwait(false);

            while (!timeoutCts.IsCancellationRequested)
            {
                UdpReceiveResult result = await udp.ReceiveAsync(timeoutCts.Token).ConfigureAwait(false);
                if (RemoteEndpointMatches(result.RemoteEndPoint, expected))
                {
                    return new Result(result.Buffer);
                }
            }

            return Result.Timeout();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Result.Timeout();
        }
        catch (SocketException)
        {
            return Result.Failure();
        }
        catch (ObjectDisposedException)
        {
            return Result.Failure();
        }
        finally
        {
            Return(udp);
        }
    }

    private static bool RemoteEndpointMatches(EndPoint? remote, IPEndPoint expected)
    {
        if (remote is not IPEndPoint ip)
        {
            return false;
        }

        return ip.Port == expected.Port && ip.Address.Equals(expected.Address);
    }

    private static UdpClient Rent(AddressFamily family)
    {
        while (Pool.TryTake(out UdpClient? client))
        {
            if (client.Client.AddressFamily == family)
            {
                return client;
            }

            client.Dispose();
        }

        return new UdpClient(family);
    }

    private static void Return(UdpClient client)
    {
        if (Pool.Count < PoolMaxSize)
        {
            Pool.Add(client);
        }
        else
        {
            client.Dispose();
        }
    }
}
