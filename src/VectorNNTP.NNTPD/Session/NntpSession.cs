using System.Net;
using VectorNNTP.NNTPD.Networking.Proxy;
using VectorNNTP.NNTPD.Networking.Transport;

namespace VectorNNTP.NNTPD.Session;

/// <summary>
/// Minimal NNTP session boundary that exposes established client identity.
/// </summary>
/// <remarks>
/// This type intentionally does not implement NNTP commands, greets, or authentication.
/// It only makes the connection's effective client endpoint available to future session work.
/// </remarks>
public sealed class NntpSession
{
    /// <summary>Initializes a new instance of the <see cref="NntpSession"/> class.</summary>
    public NntpSession(INntpConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        Connection = connection;
        ClientIdentity = connection.ClientIdentity;
    }

    /// <summary>Gets the underlying transport connection.</summary>
    public INntpConnection Connection { get; }

    /// <summary>Gets the immutable client identity established at connection start.</summary>
    public ConnectionClientIdentity ClientIdentity { get; }

    /// <summary>Gets the effective client IP address for this session.</summary>
    public IPAddress ClientAddress => ClientIdentity.ClientAddress;

    /// <summary>Gets the effective client TCP source port for this session.</summary>
    public int ClientPort => ClientIdentity.ClientPort;

    /// <summary>Gets the actual TCP peer endpoint of the accepted socket.</summary>
    public IPEndPoint TcpPeer => ClientIdentity.TcpPeer;
}
