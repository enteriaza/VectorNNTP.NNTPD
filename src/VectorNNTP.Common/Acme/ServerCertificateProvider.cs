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
        /// <summary>Manager that loads the live PFX. Not disposed by this provider.</summary>
        private readonly CertificateManager _manager;

        /// <summary>Stores the manager used for availability checks and certificate loads.</summary>
        /// <param name="manager">Certificate manager. Null throws <see cref="ArgumentNullException"/>.</param>
        internal ServerCertificateProvider(CertificateManager manager)
        {
            ArgumentNullException.ThrowIfNull(manager);
            _manager = manager;
        }

        /// <inheritdoc />
        public bool IsAvailable => _manager.CurrentMaterial is not null
                                   || _manager.EvaluateExisting().Usable;

        /// <summary>
        /// Returns a new <see cref="X509Certificate2"/> including the private key for TLS.
        /// Caller owns disposal.
        /// </summary>
        /// <returns>The live certificate.</returns>
        /// <exception cref="AcmeCertificateException">Thrown with category <c>no_certificate</c> when no in-memory or on-disk material exists.</exception>
        public X509Certificate2 GetCertificate() => _manager.CreateTlsCertificate();
    }
}
