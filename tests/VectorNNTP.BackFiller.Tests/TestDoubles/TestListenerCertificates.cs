using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using VectorNNTP.BackFiller.Listener;

namespace VectorNNTP.BackFiller.Tests.TestDoubles;

internal static class TestListenerCertificates
{
    internal static CacheListenerCertificateMaterial CreateMaterial() =>
        new(CreateSelfSigned());

    internal static X509Certificate2 CreateSelfSigned()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=backfiller.test",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                critical: true));
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") },
                critical: false));
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddDnsName("backfiller.test");
        san.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        using var created = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(30));
        return X509CertificateLoader.LoadPkcs12(
            created.Export(X509ContentType.Pfx, "test"),
            "test",
            CacheListenerCertificateMaterial.TlsServerKeyStorageFlags);
    }
}

internal sealed class StaticCacheListenerCertificateSource : ICacheListenerCertificateSource
{
    private readonly byte[]? _pfx;

    public StaticCacheListenerCertificateSource(bool available = true)
    {
        if (available)
        {
            using var cert = TestListenerCertificates.CreateSelfSigned();
            _pfx = cert.Export(X509ContentType.Pfx, "test");
        }
    }

    public bool Fail { get; set; }

    public bool TryGetCurrent(out CacheListenerCertificateMaterial result)
    {
        if (Fail || _pfx is null)
        {
            result = null!;
            return false;
        }

        result = new CacheListenerCertificateMaterial(
            X509CertificateLoader.LoadPkcs12(
                _pfx,
                "test",
                CacheListenerCertificateMaterial.TlsServerKeyStorageFlags));
        return true;
    }
}
