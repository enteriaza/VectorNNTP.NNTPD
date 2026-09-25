using System.Net;

namespace VectorNNTP.NNTPD.Session.Commands.Posting;

/// <summary>
/// Minimum transport identity recovered from a server-owned POST <c>X-Trace</c> token.
/// </summary>
/// <remarks>
/// Intended for trusted server-side diagnostics only. Do not log this value.
/// </remarks>
public readonly struct PostingTracePayload
{
    /// <summary>Initializes a new payload.</summary>
    public PostingTracePayload(IPAddress address, int port, DateTimeOffset injectedAtUtc, Guid traceId)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
        Address = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        Port = port;
        InjectedAtUtc = injectedAtUtc.ToUniversalTime();
        TraceId = traceId;
    }

    /// <summary>Gets the effective transport peer address.</summary>
    public IPAddress Address { get; }

    /// <summary>Gets the effective transport peer port.</summary>
    public int Port { get; }

    /// <summary>Gets the injection-boundary UTC timestamp.</summary>
    public DateTimeOffset InjectedAtUtc { get; }

    /// <summary>Gets the unique identifier for this POST attempt.</summary>
    public Guid TraceId { get; }
}
