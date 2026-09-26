using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;
using VectorNNTP.BackFiller.Configuration;
using VectorNNTP.BackFiller.Listener;
using VectorNNTP.BackFiller.Tests.Fixtures;
using VectorNNTP.BackFiller.Tests.Retention;
using VectorNNTP.BackFiller.Tests.TestDoubles;

namespace VectorNNTP.BackFiller.Tests.Listener;

public sealed class DirectoryCacheListenerCertificateSourceTests(ITestOutputHelper output)
{
    [Fact]
    public void Valid_pfx_loads_and_disposes_without_copying_the_password_into_exceptions()
    {
        using var workspace = CertificateWorkspace.Create();
        workspace.WriteValidPfx();
        var source = new DirectoryCacheListenerCertificateSource(workspace.Runtime);

        Assert.True(source.TryGetCurrent(out var material));
        using (material)
        {
            Assert.True(material.Certificate.HasPrivateKey);
            Assert.Equal("backfiller.test", material.Certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false));
        }

        material.Dispose();
    }

    [Fact]
    public async Task Missing_pfx_returns_false_and_listener_names_the_resolved_path()
    {
        using var workspace = CertificateWorkspace.Create();
        var source = new DirectoryCacheListenerCertificateSource(workspace.Runtime);
        Assert.False(source.TryGetCurrent(out _));

        var service = new CacheListenerService(
            workspace.Runtime,
            source,
            ArticleRetentionAuthorityTests.Create(TimeProvider.System, 1024),
            NullLogger<CacheListenerService>.Instance);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.StartAsync(CancellationToken.None));
        Assert.Contains(workspace.PfxPath, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(BackFillerTestOptions.SecretPfx, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Invalid_pfx_throws_without_leaking_the_password()
    {
        using var workspace = CertificateWorkspace.Create();
        File.WriteAllBytes(workspace.PfxPath, [0x00, 0x01, 0x02, 0xFF]);
        var source = new DirectoryCacheListenerCertificateSource(workspace.Runtime);

        var ex = Assert.Throws<InvalidOperationException>(() => source.TryGetCurrent(out _));
        Assert.Contains(workspace.PfxPath, ex.Message, StringComparison.Ordinal);
        Assert.Contains("could not be loaded", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(BackFillerTestOptions.SecretPfx, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(BackFillerTestOptions.SecretPfx, ex.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Wrong_pfx_password_throws_without_leaking_the_password()
    {
        using var workspace = CertificateWorkspace.Create();
        TestListenerCertificates.WritePkcs12(workspace.PfxPath, "correct-password-not-logged");
        var source = new DirectoryCacheListenerCertificateSource(workspace.Runtime);

        var ex = Assert.Throws<InvalidOperationException>(() => source.TryGetCurrent(out _));
        Assert.Contains(workspace.PfxPath, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(BackFillerTestOptions.SecretPfx, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("correct-password-not-logged", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("correct-password-not-logged", ex.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Public_only_pfx_is_rejected()
    {
        using var workspace = CertificateWorkspace.Create();
        TestListenerCertificates.WritePublicOnlyPkcs12(workspace.PfxPath, BackFillerTestOptions.SecretPfx);
        var source = new DirectoryCacheListenerCertificateSource(workspace.Runtime);

        var ex = Assert.Throws<InvalidOperationException>(() => source.TryGetCurrent(out _));
        Assert.Contains("private key", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(BackFillerTestOptions.SecretPfx, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Inaccessible_pfx_throws_without_leaking_the_password()
    {
        using var workspace = CertificateWorkspace.Create();
        workspace.WriteValidPfx();
        using var exclusive = new FileStream(
            workspace.PfxPath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);
        var source = new DirectoryCacheListenerCertificateSource(workspace.Runtime);
        try
        {
            Assert.True(source.TryGetCurrent(out var material));
            material.Dispose();
            output.WriteLine("SKIP: this platform allowed LoadPkcs12FromFile to read a file opened with FileShare.None.");
            return;
        }
        catch (InvalidOperationException ex)
        {
            Assert.Contains(workspace.PfxPath, ex.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(BackFillerTestOptions.SecretPfx, ex.Message, StringComparison.Ordinal);
        }
    }

    private sealed class CertificateWorkspace : IDisposable
    {
        private CertificateWorkspace(string root, BackFillerRuntimeOptions runtime, string pfxPath)
        {
            Root = root;
            Runtime = runtime;
            PfxPath = pfxPath;
        }

        public string Root { get; }

        public BackFillerRuntimeOptions Runtime { get; }

        public string PfxPath { get; }

        public static CertificateWorkspace Create()
        {
            var root = Directory.CreateTempSubdirectory("bf-certs-").FullName;
            var options = BackFillerTestOptions.CreateValid();
            options.CertificateDirectory = root;
            options.LogDirectory = Path.Combine(root, "logs");
            var runtime = BackFillerRuntimeOptionsFactory.Create(
                options,
                BackFillerTestOptions.CreateValidConnectionStrings(),
                root);
            return new CertificateWorkspace(
                root,
                runtime,
                Path.Combine(runtime.CertificateDirectory, ListenerProtocol.ListenerPfxFileName));
        }

        public void WriteValidPfx() =>
            TestListenerCertificates.WritePkcs12(PfxPath, BackFillerTestOptions.SecretPfx);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
