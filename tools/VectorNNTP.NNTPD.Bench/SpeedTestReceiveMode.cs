namespace VectorNNTP.NNTPD.Bench;

/// <summary>How the SPEEDTEST client consumes the multiline payload.</summary>
internal enum SpeedTestReceiveMode
{
    /// <summary>Diagnostic <see cref="StreamReader.ReadLineAsync(System.Threading.CancellationToken)"/> path.</summary>
    Line = 0,

    /// <summary>Diagnostic raw <see cref="System.Net.Sockets.Socket.ReceiveAsync(System.Memory{byte}, System.Net.Sockets.SocketFlags, System.Threading.CancellationToken)"/> drain to a byte count.</summary>
    Raw = 1,

    /// <summary>
    /// Default: reusable-buffer <see cref="System.Net.Sockets.Socket.ReceiveAsync(System.Memory{byte}, System.Net.Sockets.SocketFlags, System.Threading.CancellationToken)"/>
    /// with byte terminator scan. No payload strings.
    /// </summary>
    Byte = 2,
}
