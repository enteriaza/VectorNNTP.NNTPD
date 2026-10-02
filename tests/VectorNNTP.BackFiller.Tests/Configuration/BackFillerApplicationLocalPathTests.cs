using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using VectorNNTP.BackFiller.Configuration;
using VectorNNTP.BackFiller.Hosting;
using VectorNNTP.BackFiller.Logging;
using VectorNNTP.BackFiller.RabbitMq;
using VectorNNTP.BackFiller.Tests.Fixtures;
using VectorNNTP.BackFiller.Tests.TestDoubles;
using VectorNNTP.NNTPD.Configuration;

using VectorNNTP.Common.Messaging.RabbitMq;

namespace VectorNNTP.BackFiller.Tests.Configuration;

/// <summary>
/// Proves BackFiller application-local paths resolve against
/// <see cref="AppContext.BaseDirectory"/>, not the process working directory
/// or an IDE/project content root.
/// </summary>
[Collection("WorkingDirectory")]
public sealed class BackFillerApplicationLocalPathTests
{
    [Fact]
    public void Relative_certs_resolves_beneath_application_base()
    {
        using var host = CreateHost(decoyContentRoot: CreateTempDir("bf-decoy-content-"));
        var runtime = host.Services.GetRequiredService<BackFillerRuntimeOptions>();
        var acme = host.Services.GetRequiredService<IOptions<AcmeCloudflareOptions>>().Value;
        Assert.Equal(Expected("certs/"), runtime.CertificateDirectory);
        Assert.Equal(Expected("certs/"), acme.AcmeStateDir);
        Assert.StartsWith(
            Normalized(AppContext.BaseDirectory),
            runtime.CertificateDirectory,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Resolved_paths_are_not_based_on_process_working_directory()
    {
        var previous = Environment.CurrentDirectory;
        var cwd = CreateTempDir("bf-cwd-certs-");
        try
        {
            Environment.CurrentDirectory = cwd;
            using var host = CreateHost();
            var runtime = host.Services.GetRequiredService<BackFillerRuntimeOptions>();
            Assert.Equal(Expected("certs/"), runtime.CertificateDirectory);
            Assert.Equal(Expected("logs"), runtime.LogDirectory);
            Assert.NotEqual(Path.GetFullPath("certs/"), runtime.CertificateDirectory);
            Assert.NotEqual(Path.GetFullPath("logs"), runtime.LogDirectory);
            Assert.DoesNotContain(Normalized(cwd), runtime.CertificateDirectory, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(Normalized(cwd), runtime.LogDirectory, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.CurrentDirectory = previous;
            TryDelete(cwd);
        }
    }

    [Fact]
    public void Changing_working_directory_does_not_change_resolved_paths()
    {
        var previous = Environment.CurrentDirectory;
        var first = CreateTempDir("bf-cwd-a-");
        var second = CreateTempDir("bf-cwd-b-");
        try
        {
            Environment.CurrentDirectory = first;
            using var hostA = CreateHost();
            var runtimeA = hostA.Services.GetRequiredService<BackFillerRuntimeOptions>();
            Environment.CurrentDirectory = second;
            using var hostB = CreateHost();
            var runtimeB = hostB.Services.GetRequiredService<BackFillerRuntimeOptions>();
            Assert.Equal(runtimeA.CertificateDirectory, runtimeB.CertificateDirectory);
            Assert.Equal(runtimeA.LogDirectory, runtimeB.LogDirectory);
            Assert.Equal(Expected("certs/"), runtimeB.CertificateDirectory);
            Assert.Equal(Expected("logs"), runtimeB.LogDirectory);
        }
        finally
        {
            Environment.CurrentDirectory = previous;
            TryDelete(first);
            TryDelete(second);
        }
    }

    [Fact]
    public void Explicit_absolute_paths_remain_absolute()
    {
        var absoluteCerts = CreateTempDir("bf-abs-certs-");
        var absoluteLogs = CreateTempDir("bf-abs-logs-");
        try
        {
            var pairs = BackFillerTestOptions.CreateValidConfigurationPairs();
            pairs["BackFiller:AcmeStateDir"] = absoluteCerts;
            pairs["BackFiller:Logging:File:LogDir"] = absoluteLogs;
            using var host = CreateHost(pairs: pairs);
            var runtime = host.Services.GetRequiredService<BackFillerRuntimeOptions>();
            Assert.Equal(Expected(absoluteCerts), runtime.CertificateDirectory);
            Assert.Equal(Expected(absoluteLogs), runtime.LogDirectory);
        }
        finally
        {
            TryDelete(absoluteCerts);
            TryDelete(absoluteLogs);
        }
    }

    [Fact]
    public void Explicit_relative_paths_resolve_beneath_application_base()
    {
        var pairs = BackFillerTestOptions.CreateValidConfigurationPairs();
        pairs["BackFiller:AcmeStateDir"] = "nested-certs/";
        pairs["BackFiller:Logging:File:LogDir"] = "nested-logs";
        using var host = CreateHost(pairs: pairs);
        var runtime = host.Services.GetRequiredService<BackFillerRuntimeOptions>();
        Assert.Equal(Expected("nested-certs/"), runtime.CertificateDirectory);
        Assert.Equal(Expected("nested-logs"), runtime.LogDirectory);
    }

    [Fact]
    public void Backfiller_uses_the_same_common_path_resolution_as_nntpd()
    {
        using var host = CreateHost();
        var runtime = host.Services.GetRequiredService<BackFillerRuntimeOptions>();
        Assert.Equal(
            AcmeCloudflareOptionsValidator.ResolveAcmeStateDir("certs/", AppContext.BaseDirectory),
            runtime.CertificateDirectory);
        Assert.Equal(
            ApplicationLocalPath.ResolveApplicationLocalPath("certs/", AppContext.BaseDirectory),
            runtime.CertificateDirectory);
        Assert.Equal(
            ApplicationLocalPath.ResolveApplicationLocalPath("logs", AppContext.BaseDirectory),
            runtime.LogDirectory);
        Assert.Contains(
            "ApplicationLocalPath.ResolveApplicationLocalPath",
            File.ReadAllText(FindRepoFile(Path.Combine("src", "VectorNNTP.BackFiller", "Hosting", "BackFillerServiceCollectionExtensions.cs"))),
            StringComparison.Ordinal);
        Assert.Contains(
            "ApplicationLocalPath.ResolveApplicationLocalPath",
            File.ReadAllText(FindRepoFile(Path.Combine("src", "VectorNNTP.BackFiller", "Configuration", "BackFillerRuntimeOptionsFactory.cs"))),
            StringComparison.Ordinal);
        Assert.Contains(
            "ApplicationLocalPath.ResolveApplicationLocalPath",
            File.ReadAllText(FindRepoFile(Path.Combine("src", "VectorNNTP.BackFiller", "Logging", "BackFillerFileLogging.cs"))),
            StringComparison.Ordinal);
        Assert.Equal(
            ApplicationLocalPath.ResolveApplicationLocalPath("logs", AppContext.BaseDirectory),
            BackFillerFileLogging.ResolveDirectory("logs"));
    }

    [Fact]
    public void Hosting_does_not_create_certs_or_logs_in_the_working_directory()
    {
        var previous = Environment.CurrentDirectory;
        var cwd = CreateTempDir("bf-create-cwd-");
        try
        {
            Environment.CurrentDirectory = cwd;
            using var host = CreateHost();
            _ = host.Services.GetRequiredService<BackFillerRuntimeOptions>();
            Assert.False(Directory.Exists(Path.Combine(cwd, "certs")));
            Assert.False(Directory.Exists(Path.Combine(cwd, "logs")));
        }
        finally
        {
            Environment.CurrentDirectory = previous;
            TryDelete(cwd);
        }
    }

    private static string Expected(string configured) =>
        ApplicationLocalPath.ResolveApplicationLocalPath(configured, AppContext.BaseDirectory);

    private static string Normalized(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static IHost CreateHost(
        string? decoyContentRoot = null,
        Dictionary<string, string?>? pairs = null)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = [],
            ContentRootPath = decoyContentRoot ?? AppContext.BaseDirectory,
        });
        builder.Configuration.AddInMemoryCollection(pairs ?? BackFillerTestOptions.CreateValidConfigurationPairs());
        builder.Services.AddSingleton<ILocalIpAddressAssignee>(new FakeLocalIpAddressAssignee(assignAll: true));
        builder.Services.AddSingleton<IPhysicalMemoryProvider>(new FakePhysicalMemoryProvider(64L * 1024 * 1024 * 1024));
        builder.Services.AddSingleton<IRabbitMqConnectionFactory>(new FakeBackFillerRabbitMqConnectionFactory());
        builder.ConfigureBackFillerPlatformHosting();
        builder.AddBackFillerHosting();
        return builder.Build();
    }

    private static string CreateTempDir(string prefix) =>
        Directory.CreateTempSubdirectory(prefix).FullName;

    private static void TryDelete(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static string FindRepoFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not locate {relativePath}.");
    }
}
