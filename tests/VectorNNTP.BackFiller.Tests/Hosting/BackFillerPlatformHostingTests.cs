using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Options;
using VectorNNTP.BackFiller.Configuration;
using VectorNNTP.BackFiller.Hosting;
using VectorNNTP.BackFiller.Listener;
using VectorNNTP.BackFiller.RabbitMq;
using VectorNNTP.BackFiller.Tests.Fixtures;
using VectorNNTP.BackFiller.Tests.TestDoubles;
using VectorNNTP.NNTPD.Configuration;

namespace VectorNNTP.BackFiller.Tests.Hosting;

public sealed class BackFillerPlatformHostingTests
{
    [Fact]
    public void CreateApplicationBuilder_uses_the_application_base_as_content_root()
    {
        var builder = Host.CreateApplicationBuilder([]);
        Assert.Equal(
            Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(builder.Environment.ContentRootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    }

    [Fact]
    public void Platform_hosting_sets_the_windows_service_name_and_strips_console_formatters()
    {
        var builder = Host.CreateApplicationBuilder([]);
        builder.ConfigureBackFillerPlatformHosting();

        Assert.Contains(
            "options.ServiceName = \"VectorNNTP.BackFiller\"",
            File.ReadAllText(FindPlatformHostingSource()),
            StringComparison.Ordinal);
        Assert.False(
            WindowsServiceHelpers.IsWindowsService(),
            "This testhost is not a Windows Service; AddWindowsService activates the lifetime only under SCM.");
        Assert.DoesNotContain(
            builder.Services,
            BackFillerServiceCollectionExtensions.IsMicrosoftConsoleLoggerOptionsConfiguration);
    }

    private static string FindPlatformHostingSource()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "src",
                "VectorNNTP.BackFiller",
                "Hosting",
                "BackFillerServiceCollectionExtensions.cs");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Could not locate BackFillerServiceCollectionExtensions.cs.");
    }

    [Fact]
    public void Runtime_options_resolve_relative_paths_from_the_host_content_root()
    {
        var contentRoot = Directory.CreateTempSubdirectory("bf-host-root-").FullName;
        try
        {
            using var host = CreateHost(contentRoot);
            var runtime = host.Services.GetRequiredService<BackFillerRuntimeOptions>();
            Assert.Equal(Path.GetFullPath(Path.Combine(contentRoot, "logs")), runtime.LogDirectory);
            Assert.Equal(Path.GetFullPath(Path.Combine(contentRoot, "certs")), runtime.CertificateDirectory);
            Assert.Equal(
                runtime.Shutdown.GracePeriod,
                host.Services.GetRequiredService<IOptions<HostOptions>>().Value.ShutdownTimeout);
        }
        finally
        {
            try
            {
                Directory.Delete(contentRoot, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static IHost CreateHost(string contentRoot)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = [],
            ContentRootPath = contentRoot,
        });
        builder.Configuration.AddInMemoryCollection(BackFillerTestOptions.CreateValidConfigurationPairs());
        builder.Services.AddSingleton<ILocalIpAddressAssignee>(new FakeLocalIpAddressAssignee(assignAll: true));
        builder.Services.AddSingleton<IPhysicalMemoryProvider>(new FakePhysicalMemoryProvider(64L * 1024 * 1024 * 1024));
        builder.Services.AddSingleton<IBackFillerRabbitMqConnectionFactory>(new FakeBackFillerRabbitMqConnectionFactory());
        builder.Services.AddSingleton<ICacheListenerCertificateSource>(new StaticCacheListenerCertificateSource());
        builder.ConfigureBackFillerPlatformHosting();
        builder.AddBackFillerHosting();
        return builder.Build();
    }
}
