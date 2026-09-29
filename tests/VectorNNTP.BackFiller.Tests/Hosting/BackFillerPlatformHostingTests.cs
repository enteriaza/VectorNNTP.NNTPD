using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Options;
using VectorNNTP.BackFiller.Configuration;
using VectorNNTP.BackFiller.Hosting;
using VectorNNTP.BackFiller.RabbitMq;
using VectorNNTP.BackFiller.Tests.Fixtures;
using VectorNNTP.BackFiller.Tests.TestDoubles;
using VectorNNTP.NNTPD.Configuration;

using VectorNNTP.Common.Messaging.RabbitMq;

namespace VectorNNTP.BackFiller.Tests.Hosting;

public sealed class BackFillerPlatformHostingTests
{
    [Fact]
    public void Program_pins_content_root_to_the_application_base()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = [],
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.Environment.ContentRootPath = AppContext.BaseDirectory;
        Assert.Equal(
            Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(builder.Environment.ContentRootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        Assert.Contains(
            "ContentRootPath = AppContext.BaseDirectory",
            File.ReadAllText(FindSource("Program.cs")),
            StringComparison.Ordinal);
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

    private static string FindPlatformHostingSource() =>
        FindSource(Path.Combine("Hosting", "BackFillerServiceCollectionExtensions.cs"));

    private static string FindSource(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "VectorNNTP.BackFiller", relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not locate {relativePath}.");
    }

    [Fact]
    public void Runtime_options_resolve_relative_paths_from_the_application_base_not_content_root()
    {
        var decoyContentRoot = Directory.CreateTempSubdirectory("bf-host-decoy-").FullName;
        try
        {
            using var host = CreateHost(decoyContentRoot);
            var runtime = host.Services.GetRequiredService<BackFillerRuntimeOptions>();
            var expectedLogs = ApplicationLocalPath.ResolveApplicationLocalPath("logs", AppContext.BaseDirectory);
            var expectedCerts = AcmeCloudflareOptionsValidator.ResolveAcmeStateDir("certs/", AppContext.BaseDirectory);
            Assert.Equal(expectedLogs, runtime.LogDirectory);
            Assert.Equal(expectedCerts, runtime.CertificateDirectory);
            Assert.DoesNotContain(
                Path.GetFullPath(decoyContentRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                runtime.LogDirectory,
                StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                Path.GetFullPath(decoyContentRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                runtime.CertificateDirectory,
                StringComparison.OrdinalIgnoreCase);
            Assert.Equal(
                runtime.Shutdown.GracePeriod,
                host.Services.GetRequiredService<IOptions<HostOptions>>().Value.ShutdownTimeout);
        }
        finally
        {
            try
            {
                Directory.Delete(decoyContentRoot, recursive: true);
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
        builder.Services.AddSingleton<IRabbitMqConnectionFactory>(new FakeBackFillerRabbitMqConnectionFactory());
        builder.ConfigureBackFillerPlatformHosting();
        builder.AddBackFillerHosting();
        return builder.Build();
    }
}
