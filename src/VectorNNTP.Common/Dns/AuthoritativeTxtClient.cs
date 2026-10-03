using System.Buffers;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace VectorNNTP.Common.Dns
{
    /// <summary>
    /// Sends TXT queries directly to one authoritative nameserver (UDP, then TCP when needed).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Queries use RD=0. UDP is attempted first; TCP (RFC 7766 length-prefixed framing) is used when the
    /// UDP response is truncated, missing, or yields no TXT answers.
    /// </para>
    /// <para>
    /// UDP responses are accepted only when the datagram remote endpoint matches the queried nameserver
    /// address and port 53 (see <see cref="DnsUdpExchange"/>). TCP connects explicitly to that address.
    /// </para>
    /// <para>
    /// This type does not perform recursive resolution and does not discover nameservers. Pair with
    /// <see cref="ZoneApexNameserverDiscovery"/> for ACME DNS-01 visibility checks.
    /// </para>
    /// </remarks>
    internal static class AuthoritativeTxtClient
    {
        private const int ReceiveTimeoutMs = 5_000;

        /// <summary>
        /// Queries TXT for <paramref name="recordName"/> at <paramref name="nameserver"/> using UDP, then TCP
        /// if truncated or empty answers.
        /// </summary>
        /// <returns>Decoded TXT strings; empty when the nameserver returns no usable TXT.</returns>
        public static async Task<List<string>> QueryTxtAsync(
            IPAddress nameserver,
            string recordName,
            CancellationToken cancellationToken,
            ILogger? logger = null)
        {
            ArgumentNullException.ThrowIfNull(nameserver);
            ArgumentException.ThrowIfNullOrWhiteSpace(recordName);

            byte[] queryPacket = DnsQueryBuilder.Build(recordName, DnsRecordType.Txt, out ushort queryId);
            byte[]? udpResponse = await TryUdpQueryAsync(nameserver, recordName, queryPacket, logger, cancellationToken)
                .ConfigureAwait(false);
            List<string> results = udpResponse is null ? [] : DnsTxtParser.ParseTxtStrings(udpResponse, queryId);
            if (udpResponse is null || ShouldRetryOverTcp(udpResponse, results))
            {
                byte[]? tcpResponse = await TryTcpQueryAsync(nameserver, recordName, queryPacket, logger, cancellationToken)
                    .ConfigureAwait(false);
                if (tcpResponse is not null)
                {
                    List<string> tcpParsed = DnsTxtParser.ParseTxtStrings(tcpResponse, queryId);
                    if (tcpParsed.Count > 0)
                    {
                        return tcpParsed;
                    }
                }
            }

            return results;
        }

        private static bool ShouldRetryOverTcp(byte[] udpResponse, List<string> parsedTxt)
            => DnsResponseHeader.IsTruncated(udpResponse) || parsedTxt.Count == 0;

        private static async Task<byte[]?> TryUdpQueryAsync(
            IPAddress nameserver,
            string recordName,
            byte[] queryPacket,
            ILogger? logger,
            CancellationToken cancellationToken)
        {
            DnsUdpExchange.Result exchange = await DnsUdpExchange
                .QueryAsync(nameserver, queryPacket, ReceiveTimeoutMs, cancellationToken)
                .ConfigureAwait(false);

            if (exchange.TimedOut && logger is not null)
            {
                DnsLogMessages.AuthoritativeUdpTimeout(logger, nameserver.ToString(), recordName);
            }

            return exchange.Buffer;
        }

        private static async Task<byte[]?> TryTcpQueryAsync(
            IPAddress nameserver,
            string recordName,
            byte[] queryPacket,
            ILogger? logger,
            CancellationToken cancellationToken)
        {
            using var tcp = new TcpClient();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(ReceiveTimeoutMs);

            byte[]? rentedResponse = null;
            try
            {
                await tcp.ConnectAsync(nameserver, 53, timeoutCts.Token).ConfigureAwait(false);
                NetworkStream stream = tcp.GetStream();
                int qLen = queryPacket.Length;
                byte[] lengthPrefix = new byte[2];
                BinaryPrimitives.WriteUInt16BigEndian(lengthPrefix, (ushort)qLen);
                await stream.WriteAsync(lengthPrefix.AsMemory(0, 2), timeoutCts.Token).ConfigureAwait(false);
                await stream.WriteAsync(queryPacket, timeoutCts.Token).ConfigureAwait(false);
                await stream.FlushAsync(timeoutCts.Token).ConfigureAwait(false);

                byte[] lenBuf = new byte[2];
                await stream.ReadExactlyAsync(lenBuf.AsMemory(0, 2), timeoutCts.Token).ConfigureAwait(false);
                int msgLen = BinaryPrimitives.ReadUInt16BigEndian(lenBuf);
                if (msgLen is <= 0 or > 65535)
                {
                    return null;
                }

                rentedResponse = ArrayPool<byte>.Shared.Rent(msgLen);
                await stream.ReadExactlyAsync(rentedResponse.AsMemory(0, msgLen), timeoutCts.Token).ConfigureAwait(false);
                byte[] response = new byte[msgLen];
                rentedResponse.AsSpan(0, msgLen).CopyTo(response);
                return response;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                if (logger is not null)
                {
                    DnsLogMessages.AuthoritativeTcpTimeout(logger, nameserver.ToString(), recordName);
                }

                return null;
            }
            catch (SocketException)
            {
                return null;
            }
            catch (ObjectDisposedException)
            {
                return null;
            }
            catch (IOException)
            {
                return null;
            }
            finally
            {
                if (rentedResponse is not null)
                {
                    ArrayPool<byte>.Shared.Return(rentedResponse);
                }
            }
        }
    }
}
