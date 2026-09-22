using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTP.NNTPD.Acme;
using VectorNNTP.NNTPD.Networking.Certificates;
using VectorNNTP.NNTPD.Tests.Acme;

namespace VectorNNTP.NNTPD.Tests.Networking.Transport;

public sealed class TlsCertificateLifetimeTests
{
    [Fact]
    public async Task ExistingLease_SurvivesPublication_UntilReleased()
    {
        await using var provider = CreateProvider();
        provider.PublishFromPfx(CreatePfx("a.usenet.ninja"), AcmeConfigurationTests.TestPfxPassword);
        var leaseA = provider.Acquire();
        var holderA = leaseA.Holder;
        Assert.Equal(0, holderA.DisposeCount);

        provider.PublishFromPfx(CreatePfx("b.usenet.ninja"), AcmeConfigurationTests.TestPfxPassword);
        Assert.Contains(
            "a.usenet.ninja",
            leaseA.Context.TargetCertificate.Subject,
            StringComparison.OrdinalIgnoreCase);
        Assert.False(holderA.IsDisposed);
        Assert.Equal(0, holderA.DisposeCount);

        leaseA.Dispose();
        Assert.Equal(1, holderA.DisposeCount);
        Assert.True(holderA.IsDisposed);
    }

    [Fact]
    public async Task NewAcquire_AfterPublish_ReceivesNewGeneration()
    {
        await using var provider = CreateProvider();
        provider.PublishFromPfx(CreatePfx("a.usenet.ninja"), AcmeConfigurationTests.TestPfxPassword);
        int generationA;
        using (var leaseA = provider.Acquire())
        {
            generationA = leaseA.Generation;
        }

        provider.PublishFromPfx(CreatePfx("b.usenet.ninja"), AcmeConfigurationTests.TestPfxPassword);
        using var leaseB = provider.Acquire();
        Assert.NotEqual(generationA, leaseB.Generation);
        Assert.Contains(
            "b.usenet.ninja",
            leaseB.Context.TargetCertificate.Subject,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FailedPublication_LeavesPriorContextActive()
    {
        await using var provider = CreateProvider();
        provider.PublishFromPfx(CreatePfx("a.usenet.ninja"), AcmeConfigurationTests.TestPfxPassword);
        using var leaseBefore = provider.Acquire();
        var generationA = leaseBefore.Generation;

        Assert.Throws<AcmeCertificateException>(() =>
            provider.PublishFromPfx([0x01, 0x02, 0x03, 0x04], AcmeConfigurationTests.TestPfxPassword));

        using var leaseAfter = provider.Acquire();
        Assert.Equal(generationA, leaseAfter.Generation);
        Assert.Contains(
            "a.usenet.ninja",
            leaseAfter.Context.TargetCertificate.Subject,
            StringComparison.OrdinalIgnoreCase);
        Assert.False(leaseBefore.HolderIsDisposed);
    }

    [Fact]
    public async Task LeaseDoubleDispose_DoesNotDoubleDisposeHolder()
    {
        await using var provider = CreateProvider();
        provider.PublishFromPfx(CreatePfx("a.usenet.ninja"), AcmeConfigurationTests.TestPfxPassword);
        var lease = provider.Acquire();
        var holder = lease.Holder;

        lease.Dispose();
        lease.Dispose();
        Assert.Equal(0, holder.DisposeCount);

        provider.PublishFromPfx(CreatePfx("b.usenet.ninja"), AcmeConfigurationTests.TestPfxPassword);
        Assert.Equal(1, holder.DisposeCount);
    }

    [Fact]
    public async Task ProviderDispose_BlocksAcquire_AndDisposesAfterLeasesReleased()
    {
        var provider = CreateProvider();
        provider.PublishFromPfx(CreatePfx("a.usenet.ninja"), AcmeConfigurationTests.TestPfxPassword);
        var lease = provider.Acquire();
        var holder = lease.Holder;

        await provider.DisposeAsync();
        Assert.Throws<ObjectDisposedException>(() => provider.Acquire());
        Assert.False(holder.IsDisposed);

        lease.Dispose();
        Assert.True(holder.IsDisposed);
        Assert.Equal(1, holder.DisposeCount);
    }

    [Fact]
    public void RetiredHolder_RejectsAddRef()
    {
        var holder = TlsCertificateHolder.CreateFromPfx(
            CreatePfx("a.usenet.ninja"),
            AcmeConfigurationTests.TestPfxPassword);
        holder.Retire();
        Assert.Throws<ObjectDisposedException>(holder.AddRef);
        holder.Release();
        Assert.Equal(1, holder.DisposeCount);
    }

    [Fact]
    public async Task ConcurrentAcquirePublishRelease_NeverUsesDisposedHolder()
    {
        await using var provider = CreateProvider();
        var pfxA = CreatePfx("a.usenet.ninja");
        var pfxB = CreatePfx("b.usenet.ninja");
        provider.PublishFromPfx(pfxA, AcmeConfigurationTests.TestPfxPassword);

        var errors = new ConcurrentBag<Exception>();
        var observedGenerations = new ConcurrentBag<int>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var tasks = new Task[8];

        for (var t = 0; t < tasks.Length; t++)
        {
            var publish = t % 2 == 0;
            tasks[t] = Task.Run(() =>
            {
                try
                {
                    var flip = 0;
                    while (!cts.IsCancellationRequested)
                    {
                        if (publish)
                        {
                            var pfx = Interlocked.Increment(ref flip) % 2 == 0 ? pfxA : pfxB;
                            provider.PublishFromPfx(pfx, AcmeConfigurationTests.TestPfxPassword);
                        }
                        else
                        {
                            TlsCertificateLease? lease = null;
                            try
                            {
                                lease = provider.Acquire();
                                observedGenerations.Add(lease.Generation);
                                if (lease.HolderIsDisposed)
                                {
                                    throw new InvalidOperationException("Acquired a disposed holder.");
                                }

                                _ = lease.Context.TargetCertificate.Thumbprint;
                            }
                            finally
                            {
                                lease?.Dispose();
                            }
                        }
                    }
                }
                catch (ObjectDisposedException)
                {
                    // Provider disposal at test end can race the last iterations.
                }
                catch (Exception ex)
                {
                    errors.Add(ex);
                }
            }, CancellationToken.None);
        }

        await Task.Delay(200);
        await cts.CancelAsync();
        await Task.WhenAll(tasks);

        Assert.Empty(errors);
        Assert.NotEmpty(observedGenerations);
    }

    private static TlsCertificateContextProvider CreateProvider() =>
        new(NullLogger<TlsCertificateContextProvider>.Instance);

    private static byte[] CreatePfx(string cn) =>
        TestCertificateFactory.CreateMaterial(
                [cn, "news.usenet.ninja"],
                AcmeConfigurationTests.TestPfxPassword,
                DateTimeOffset.UtcNow.AddDays(30))
            .PfxBytes;
}
