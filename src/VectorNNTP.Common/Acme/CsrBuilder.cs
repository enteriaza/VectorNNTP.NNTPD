using System.Net;
using System.Security.Cryptography.X509Certificates;
using VectorNNTP.Common.Acme.Protocol;

namespace VectorNNTP.Common.Acme
{
    /// <summary>Builds ACME certificate signing requests from DNS identities.</summary>
    internal static class CsrBuilder
    {
        /// <summary>Creates a PKCS#10 CSR covering the given DNS names (first name is CN when ≤64 chars).</summary>
        /// <param name="dnsNames">SAN values. The first entry is the CN when its length is at most 64 characters; otherwise the subject is empty. Values that parse as IP addresses are IP SANs.</param>
        /// <param name="key">Leaf key that signs the request. Not disposed.</param>
        /// <returns>The DER-encoded PKCS#10 request.</returns>
        /// <exception cref="ArgumentException">Thrown when <paramref name="dnsNames"/> is empty.</exception>
        internal static byte[] CreateDnsSigningRequest(IReadOnlyList<string> dnsNames, AcmeCertificateKey key)
        {
            ArgumentNullException.ThrowIfNull(dnsNames);
            ArgumentNullException.ThrowIfNull(key);
            if (dnsNames.Count == 0)
            {
                throw new ArgumentException("At least one DNS name is required.", nameof(dnsNames));
            }

            string cn = dnsNames[0].Length <= 64 ? dnsNames[0] : string.Empty;
            var subject = string.IsNullOrEmpty(cn)
                ? new X500DistinguishedName(string.Empty)
                : new X500DistinguishedName($"CN={cn}");

            CertificateRequest request = key.CreateRequest(subject);
            var san = new SubjectAlternativeNameBuilder();
            foreach (string name in dnsNames)
            {
                if (IPAddress.TryParse(name, out IPAddress? ip))
                {
                    san.AddIpAddress(ip);
                }
                else
                {
                    san.AddDnsName(name);
                }
            }

            request.CertificateExtensions.Add(san.Build());
            return request.CreateSigningRequest();
        }
    }
}
