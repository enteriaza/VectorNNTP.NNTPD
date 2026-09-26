using VectorNNTP.Common.Hosting;
using VectorNNTP.NNTPD.Configuration;

namespace VectorNNTP.Common.Tests;

public sealed class LibraryContractTests
{
    [Fact]
    public void Common_IsAClassLibrary_WithNoEntryPoint()
    {
        var assembly = typeof(AcmeCloudflareOptions).Assembly;
        Assert.Equal("VectorNNTP.Common", assembly.GetName().Name);
        Assert.Null(assembly.EntryPoint);
        Assert.DoesNotContain(
            assembly.GetReferencedAssemblies().Select(static name => name.Name),
            static name => name is "VectorNNTP.NNTPD" or "VectorNNTP.BackFiller");
    }

    [Fact]
    public void Common_DoesNotReferenceApplicationHosts()
    {
        var referenced = typeof(AcmeCloudflareServiceCollectionExtensions).Assembly
            .GetReferencedAssemblies()
            .Select(static name => name.Name);
        Assert.DoesNotContain("VectorNNTP.NNTPD", referenced);
        Assert.DoesNotContain("VectorNNTP.BackFiller", referenced);
    }
}
