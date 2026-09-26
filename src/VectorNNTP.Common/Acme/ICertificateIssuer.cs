namespace VectorNNTP.NNTPD.Acme;

/// <summary>Issues a TLS certificate for the given DNS identities via ACME.</summary>
public interface ICertificateIssuer
{
    /// <summary>Requests, validates, and returns certificate material (DER).</summary>
    Task<CertificateMaterial> IssueAsync(
        IReadOnlyList<string> domains,
        CancellationToken cancellationToken);
}
