using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace VectorNNTP.Common.Dns
{
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
        /// <summary>Maximum UDP clients retained in <see cref="Pool"/>. Additional returns dispose the client.</summary>
        private const int PoolMaxSize = 8;

        /// <summary>Process-wide bag of unbound UDP clients rented by address family.</summary>
        private static readonly ConcurrentBag<UdpClient> Pool = [];

        /// <summary>
        /// Outcome of a single UDP DNS exchange attempt.
        /// </summary>
        internal readonly struct Result
        {
            /// <summary>Initializes a successful exchange.</summary>
            /// <param name="buffer">Response datagram accepted from the expected nameserver.</param>
            internal Result(byte[] buffer)
            {
                Buffer = buffer;
                TimedOut = false;
                Failed = false;
            }

            /// <summary>Initializes a timeout or socket-failure outcome with no response buffer.</summary>
            /// <param name="timedOut"><see langword="true"/> when the receive window elapsed.</param>
            /// <param name="failed"><see langword="true"/> when a socket or disposal error prevented a response.</param>
            private Result(bool timedOut, bool failed)
            {
                Buffer = null;
                TimedOut = timedOut;
                Failed = failed;
            }

            /// <summary>Response bytes when the exchange succeeded; otherwise <see langword="null"/>.</summary>
            internal byte[]? Buffer { get; }

            /// <summary><see langword="true"/> when the receive window elapsed without a matching datagram.</summary>
            internal bool TimedOut { get; }

            /// <summary><see langword="true"/> when a socket error prevented a usable response.</summary>
            internal bool Failed { get; }

            /// <summary>Creates a timeout outcome.</summary>
            internal static Result Timeout() => new(timedOut: true, failed: false);

            /// <summary>Creates a hard-failure outcome.</summary>
            internal static Result Failure() => new(timedOut: false, failed: true);
        }

        /// <summary>
        /// Sends <paramref name="query"/> to <paramref name="destination"/>:53 and returns the first
        /// response whose remote endpoint matches that destination.
        /// </summary>
        /// <param name="destination">Nameserver address. The destination port is always 53.</param>
        /// <param name="query">DNS query bytes.</param>
        /// <param name="timeoutMilliseconds">Receive window. Non-matching datagrams are discarded until it elapses.</param>
        /// <param name="cancellationToken">Cancels send and receive. A timeout that is not caller cancellation returns <see cref="Result.Timeout"/>.</param>
        /// <returns>A success, timeout, or socket-failure outcome. The rented socket is always returned to the pool.</returns>
        internal static async Task<Result> QueryAsync(
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

        /// <summary>Returns whether <paramref name="remote"/> is an <see cref="IPEndPoint"/> with the expected address and port.</summary>
        /// <param name="remote">Datagram remote endpoint. Null and non-IP endpoints do not match.</param>
        /// <param name="expected">Nameserver endpoint, including port 53.</param>
        /// <returns><see langword="true"/> when both address and port match.</returns>
        private static bool RemoteEndpointMatches(EndPoint? remote, IPEndPoint expected)
        {
            if (remote is not IPEndPoint ip)
            {
                return false;
            }

            return ip.Port == expected.Port && ip.Address.Equals(expected.Address);
        }

        /// <summary>
        /// Takes a pooled unbound client of <paramref name="family"/>, disposing pooled clients of a different family, or creates a new one.
        /// </summary>
        /// <param name="family">Address family of the nameserver being queried.</param>
        /// <returns>A client the caller must pass to <see cref="Return"/>.</returns>
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

        /// <summary>
        /// Returns <paramref name="client"/> to <see cref="Pool"/> when fewer than <see cref="PoolMaxSize"/> clients are retained; otherwise disposes it.
        /// </summary>
        /// <param name="client">Client rented for one exchange.</param>
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
}
