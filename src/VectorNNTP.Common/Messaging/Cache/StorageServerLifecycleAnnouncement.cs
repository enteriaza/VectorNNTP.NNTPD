namespace VectorNNTP.Common.Messaging.Cache
{
    /// <summary>Explicit StorageServer fleet announcement that the server is leaving selection.</summary>
    public enum StorageServerLifecycleState
    {
        /// <summary>The server is shutting down and must not be selected for new work.</summary>
        Draining = 1,
    }

    /// <summary>
    /// One transient StorageServer lifecycle announcement on <c>cache.broadcast</c>.
    /// </summary>
    /// <param name="Version">Wire protocol version. Current is <c>1</c>.</param>
    /// <param name="ServerId">Numeric StorageServer identity.</param>
    /// <param name="Fqdn">StorageServer FQDN. This is the fleet registry key.</param>
    /// <param name="State">Draining. Version 1 has no other state.</param>
    /// <param name="Timestamp">UTC time when this process published the announcement.</param>
    /// <param name="VatpPort">TLS VATP listen port (<c>1</c>–<c>65535</c>).</param>
    /// <remarks>
    /// This is not a capacity advertisement, not a liveness heartbeat, and not a readiness signal.
    /// Periodic advertisements remain the readiness, capacity, and liveness source.
    /// <paramref name="Timestamp"/> orders this announcement against advertisements for the same FQDN.
    /// It does not identify a process.
    /// </remarks>
    public sealed record StorageServerLifecycleAnnouncement(
        int Version,
        int ServerId,
        string Fqdn,
        StorageServerLifecycleState State,
        DateTimeOffset Timestamp,
        int VatpPort);
}
