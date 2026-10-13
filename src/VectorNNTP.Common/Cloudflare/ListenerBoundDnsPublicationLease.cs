namespace VectorNNTP.Common.Cloudflare
{
    /// <summary>
    /// Withholds Cloudflare publication until the process has bound the listener that owns the address.
    /// </summary>
    /// <remarks>
    /// NNTPD, BackFiller, and StorageServer register this lease. A process that loses the bind never
    /// reconciles, so its startup rollback cannot delete records belonging to the process that is listening.
    /// Callers that do not register a lease still publish from
    /// <see cref="CloudflareDnsReconciliationService.StartAsync"/>.
    /// </remarks>
    internal sealed class ListenerBoundDnsPublicationLease : IDnsPublicationLease
    {
        /// <inheritdoc />
        public bool PublishDuringStart => false;
    }
}
