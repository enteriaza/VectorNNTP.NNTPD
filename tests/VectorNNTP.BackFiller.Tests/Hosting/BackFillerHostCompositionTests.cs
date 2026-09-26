using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using VectorNNTP.BackFiller.Hosting;
using VectorNNTP.BackFiller.Logging;

namespace VectorNNTP.BackFiller.Tests.Hosting;

public sealed class BackFillerHostCompositionTests
{
    [Fact]
    public void AddBackFillerHosting_registers_system_time_and_placeholder_hosted_service()
    {
        var builder = Host.CreateApplicationBuilder([]);
        builder.ConfigureBackFillerLogging();
        builder.ConfigureBackFillerPlatformHosting();
        builder.Services.AddBackFillerHosting();

        using var host = builder.Build();

        Assert.Same(TimeProvider.System, host.Services.GetRequiredService<TimeProvider>());
        Assert.Contains(host.Services.GetServices<IHostedService>(), static service => service is BackFillerHostedService);
    }

    [Fact]
    public void BackFiller_assembly_does_not_reference_NNTPD()
    {
        var referenced = typeof(BackFillerServiceCollectionExtensions).Assembly
            .GetReferencedAssemblies()
            .Select(static name => name.Name);

        Assert.DoesNotContain("VectorNNTP.NNTPD", referenced);
    }
}
