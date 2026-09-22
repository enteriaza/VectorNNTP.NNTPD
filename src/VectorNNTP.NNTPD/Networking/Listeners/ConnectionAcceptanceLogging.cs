using System.Net;
using Microsoft.Extensions.Logging;
using VectorNNTP.NNTPD.Networking.Proxy;

namespace VectorNNTP.NNTPD.Networking.Listeners;

/// <summary>Formats connection-acceptance Information logs (TCP peer vs PROXY effective client).</summary>
internal static class ConnectionAcceptanceLogging
{
    /// <summary>Logs a successful plain (or plain+PROXY) connection acceptance.</summary>
    public static void LogPlainAccepted(ILogger logger, ConnectionClientIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(identity);

        if (identity.UsedProxyHeader)
        {
            logger.LogInformation(
                "Proxy connection accepted from {TcpPeer}; proxy {Client}",
                FormatEndpoint(identity.TcpPeer),
                FormatEndpoint(identity.Client));
        }
        else
        {
            logger.LogInformation(
                "Plain connection accepted from {TcpPeer}",
                FormatEndpoint(identity.TcpPeer));
        }
    }

    /// <summary>Logs a successful TLS (or TLS+PROXY) connection acceptance after handshake.</summary>
    public static void LogTlsAccepted(
        ILogger logger,
        ConnectionClientIdentity identity,
        string tlsVersion,
        string cipher)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(tlsVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(cipher);

        if (identity.UsedProxyHeader)
        {
            logger.LogInformation(
                "TLS/Proxy connection accepted from {TcpPeer}; proxy {Client} (TlsVersion={TlsVersion}, Cipher={Cipher})",
                FormatEndpoint(identity.TcpPeer),
                FormatEndpoint(identity.Client),
                tlsVersion,
                cipher);
        }
        else
        {
            logger.LogInformation(
                "TLS connection accepted from {TcpPeer} (TlsVersion={TlsVersion}, Cipher={Cipher})",
                FormatEndpoint(identity.TcpPeer),
                tlsVersion,
                cipher);
        }
    }

    /// <summary>Formats an endpoint as <c>ip:port</c> for connection acceptance logs.</summary>
    public static string FormatEndpoint(IPEndPoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        return $"{endpoint.Address}:{endpoint.Port}";
    }
}
