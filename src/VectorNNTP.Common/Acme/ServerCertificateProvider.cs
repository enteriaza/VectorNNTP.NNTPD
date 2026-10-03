using System.Security.Cryptography.X509Certificates;

namespace VectorNNTP.Common.Acme
{
    /// <summary>
    /// Provides the validated TLS server certificate to listeners without ACME protocol coupling.
    /// </summary>
    internal interface IServerCertificateProvider
    {
        /// <summary>Gets a value indicating whether a usable certificate is loaded.</summary>
        bool IsAvailable { get; }

        /// <summary>
        /// Returns a new <see cref="X509Certificate2"/> including the private key for TLS.
        /// Caller owns disposal.
        /// </summary>
        X509Certificate2 GetCertificate();
    }

    /// <summary>Certificate provider backed by <see cref="CertificateManager"/>.</summary>
    internal sealed class ServerCertificateProvider : IServerCertificateProvider
    {
        private readonly CertificateManager _manager;

        /// <summary>Initializes a new instance of the <see cref="ServerCertificateProvider"/> class.</summary>
        internal ServerCertificateProvider(CertificateManager manager)
        {
            ArgumentNullException.ThrowIfNull(manager);
            _manager = manager;
        }

        /// <inheritdoc />
        public bool IsAvailable => _manager.CurrentMaterial is not null
                                   || _manager.EvaluateExisting().Usable;

        /// <inheritdoc />
        public X509Certificate2 GetCertificate() => _manager.CreateTlsCertificate();
    }
}
