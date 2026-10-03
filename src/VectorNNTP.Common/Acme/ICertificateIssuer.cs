namespace VectorNNTP.Common.Acme
{
    /// <summary>Issues a TLS certificate for the given DNS identities via ACME.</summary>
    internal interface ICertificateIssuer
    {
        /// <summary>
        /// Requests a certificate for <paramref name="domains"/> and returns validated PKCS#12 material.
        /// </summary>
        /// <param name="domains">DNS identities to place on the certificate.</param>
        /// <param name="cancellationToken">Cancels the issuance attempt.</param>
        /// <returns>Password-protected PKCS#12 bytes plus the leaf DNS names and validity window.</returns>
        Task<CertificateMaterial> IssueAsync(
            IReadOnlyList<string> domains,
            CancellationToken cancellationToken);
    }
}
