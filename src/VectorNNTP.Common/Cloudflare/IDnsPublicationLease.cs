namespace VectorNNTP.Common.Cloudflare
{
    /// <summary>
    /// Decides whether this process publishes Cloudflare records during service start or after its listener binds.
    /// </summary>
    /// <remarks>
    /// NNTPD, BackFiller, and StorageServer register <see cref="ListenerBoundDnsPublicationLease"/>.
    /// A failed bind never mutates DNS. Callers that do not register a lease still publish from
    /// <see cref="CloudflareDnsReconciliationService.StartAsync"/>.
    /// </remarks>
    internal interface IDnsPublicationLease
    {
        /// <summary>
        /// Gets whether <see cref="CloudflareDnsReconciliationService.StartAsync"/> may call Cloudflare.
        /// </summary>
        /// <remarks>
        /// When <see langword="false"/>, start validates the FQDN, zone, and bind addresses and returns
        /// without creating or deleting records. Publication is
        /// <see cref="CloudflareDnsReconciliationService.PublishAfterListenerBoundAsync"/>.
        /// </remarks>
        bool PublishDuringStart { get; }
    }
}
