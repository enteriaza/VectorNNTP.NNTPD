using System.Net.Sockets;
using System.Text;

namespace VectorNNTP.StandaloneTxPoc;

internal readonly record struct SocketSnapshot(
    AddressFamily AddressFamily,
    SocketType SocketType,
    ProtocolType ProtocolType,
    bool NoDelay,
    int SendBufferSize,
    int ReceiveBufferSize,
    bool Blocking,
    string LocalEndPoint,
    string RemoteEndPoint)
{
    public static SocketSnapshot Capture(Socket socket) =>
        new(
            socket.AddressFamily,
            socket.SocketType,
            socket.ProtocolType,
            socket.NoDelay,
            socket.SendBufferSize,
            socket.ReceiveBufferSize,
            socket.Blocking,
            socket.LocalEndPoint?.ToString() ?? "(none)",
            socket.RemoteEndPoint?.ToString() ?? "(none)");

    public string Format()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"  AddressFamily:     {AddressFamily}");
        sb.AppendLine($"  SocketType:        {SocketType}");
        sb.AppendLine($"  ProtocolType:      {ProtocolType}");
        sb.AppendLine($"  NoDelay:           {NoDelay}");
        sb.AppendLine($"  SendBufferSize:    {SendBufferSize}");
        sb.AppendLine($"  ReceiveBufferSize: {ReceiveBufferSize}");
        sb.AppendLine($"  Blocking:          {Blocking}");
        sb.AppendLine($"  LocalEndPoint:     {LocalEndPoint}");
        sb.AppendLine($"  RemoteEndPoint:    {RemoteEndPoint}");
        return sb.ToString();
    }
}
