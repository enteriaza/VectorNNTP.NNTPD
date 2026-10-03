using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using VectorNNTP.Common.Acme;
using VectorNNTP.Common.Acme.Protocol;

namespace VectorNNTP.Common.Tests.Acme
{
    /// <summary>
    /// Owned ACME protocol contracts: account DER keys, JWS thumbprints, CSR, PFX, JSON source-gen.
    /// </summary>
    public sealed class OwnedAcmeProtocolTests
    {
        [Fact]
        public void AccountKey_DerImportExport_PreservesRsaMaterial()
        {
            using var rsa = RSA.Create(2048);
            byte[] der = rsa.ExportPkcs8PrivateKey();
            AcmeAccountKey.AssertPkcs8DerRoundTripPreservesRsaMaterial(der);

            using AcmeAccountKey key = AcmeAccountKey.ImportPkcs8Der(der);
            Assert.Equal("RS256", key.SignatureAlgorithm);
            Assert.False(string.IsNullOrWhiteSpace(key.Thumbprint));
            Assert.Contains("\"kty\":\"RSA\"", key.Jwk, StringComparison.Ordinal);
        }

        [Fact]
        public void AccountKey_Dns01Txt_IsBase64UrlSha256OfKeyAuthorization()
        {
            using var rsa = RSA.Create(2048);
            using AcmeAccountKey key = AcmeAccountKey.ImportPkcs8Der(rsa.ExportPkcs8PrivateKey());
            const string token = "test-token-value";
            string txt = key.GetDnsRecordValue(token);
            Assert.False(string.IsNullOrWhiteSpace(txt));
            Assert.DoesNotContain('=', txt);
            Assert.DoesNotContain('+', txt);
            Assert.DoesNotContain('/', txt);
            Assert.Equal(token + "." + key.Thumbprint, key.GetKeyAuthorization(token));
        }

        [Fact]
        public void CertificateKey_Csr_IsNonEmptyPkcs10()
        {
            using AcmeCertificateKey certKey = AcmeCertificateKey.CreateRsa(2048);
            byte[] csr = CsrBuilder.CreateDnsSigningRequest(["host.example.com", "news.example.com"], certKey);
            Assert.True(csr.Length > 32);
            // PKCS#10 DER SEQUENCE tag
            Assert.Equal(0x30, csr[0]);
        }

        [Fact]
        public void Pfx_FromPemChain_LoadsWithBcl()
        {
            using AcmeCertificateKey certKey = AcmeCertificateKey.CreateRsa(2048);
            string keyPem = certKey.ExportPem();
            CertificateRequest request = certKey.CreateRequest(new X500DistinguishedName("CN=host.example.com"));
            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName("host.example.com");
            request.CertificateExtensions.Add(san.Build());
            using X509Certificate2 selfSigned = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddMinutes(-1),
                DateTimeOffset.UtcNow.AddDays(1));

            string chainPem = selfSigned.ExportCertificatePem();
            using X509Certificate2 leafWithKey = X509Certificate2.CreateFromPem(chainPem, keyPem);
            byte[] pfx = PfxCrypto.ExportPfx(leafWithKey, [], "test-password");
            using X509Certificate2 loaded = PfxCrypto.LoadCertificate(pfx, "test-password");
            Assert.True(loaded.HasPrivateKey);
            Assert.Contains("host.example.com", loaded.Subject, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void AcmeJsonContext_RoundTripsDirectoryAndOrder()
        {
            const string directoryJson =
                """
                {
                  "newNonce": "https://acme.example/new-nonce",
                  "newAccount": "https://acme.example/new-account",
                  "newOrder": "https://acme.example/new-order"
                }
                """;
            AcmeDirectoryResource? directory = JsonSerializer.Deserialize(
                directoryJson,
                AcmeJsonContext.Default.AcmeDirectoryResource);
            Assert.NotNull(directory);
            Assert.Equal("https://acme.example/new-order", directory.NewOrder?.AbsoluteUri);

            const string orderJson =
                """
                {
                  "status": "pending",
                  "identifiers": [ { "type": "dns", "value": "host.example.com" } ],
                  "authorizations": [ "https://acme.example/authz/1" ],
                  "finalize": "https://acme.example/finalize/1"
                }
                """;
            AcmeOrderResource? order = JsonSerializer.Deserialize(orderJson, AcmeJsonContext.Default.AcmeOrderResource);
            Assert.NotNull(order);
            Assert.Equal("pending", order.Status);
            Assert.Equal("host.example.com", order.Identifiers![0].Value);
            Assert.Equal(AcmeIdentifierTypes.Dns, "dns");
        }

        [Fact]
        public void AcmeClient_BindExistingAccount_ReusesPersistedAccountUri()
        {
            using var rsa = RSA.Create(2048);
            using AcmeAccountKey key = AcmeAccountKey.ImportPkcs8Der(rsa.ExportPkcs8PrivateKey());
            var http = new AcmeHttpTransport(
                new ThrowingHttpClientFactory(),
                "unused",
                new Uri("https://acme.example/directory"),
                Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
                TimeProvider.System);
            var client = new AcmeClient(
                http,
                key,
                Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
                TimeProvider.System);

            client.BindExistingAccount("https://acme.example/acme/acct/42");
            Assert.Equal("https://acme.example/acme/acct/42", client.KeyId);
        }

        private sealed class ThrowingHttpClientFactory : IHttpClientFactory
        {
            public HttpClient CreateClient(string name) =>
                throw new InvalidOperationException("HTTP must not be used in this unit test.");
        }
    }
}
