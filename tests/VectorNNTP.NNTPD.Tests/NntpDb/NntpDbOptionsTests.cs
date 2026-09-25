using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using VectorNNTP.NNTPD.Configuration;
using VectorNNTP.NNTPD.NntpDb;
using VectorNNTP.NNTPD.Tests.Fixtures;

namespace VectorNNTP.NNTPD.Tests.NntpDb;

public sealed class NntpDbOptionsTests
{
    [Fact]
    public void Defaults_MatchProductionValues()
    {
        var options = new NntpDbOptions();
        Assert.Equal(TimeSpan.FromSeconds(15), options.StartupTimeout);
        Assert.Equal("NntpDb", NntpDbOptions.SectionName);
        Assert.Equal("NntpDB", NntpDbOptions.ConnectionStringName);
    }

    [Fact]
    public void Validate_Succeeds_ForDefaultsWithConnectionString()
    {
        var options = new NntpDbOptions { ConnectionString = TestHostFactory.TestNntpDbConnectionString };
        Assert.True(new NntpDbOptionsValidator().Validate(null, options).Succeeded);
    }

    [Fact]
    public void Validate_Fails_WhenConnectionStringMissing()
    {
        var result = new NntpDbOptionsValidator().Validate(null, new NntpDbOptions());
        Assert.True(result.Failed);
        Assert.Contains(
            result.Failures!,
            static failure => failure.Contains("ConnectionStrings:NntpDB", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Failures!, static failure => failure.Contains("Password", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_Fails_WhenStartupTimeoutNotPositive()
    {
        var options = new NntpDbOptions
        {
            ConnectionString = TestHostFactory.TestNntpDbConnectionString,
            StartupTimeout = TimeSpan.Zero,
        };
        Assert.True(new NntpDbOptionsValidator().Validate(null, options).Failed);
    }

    [Fact]
    public void Bind_ReadsConnectionStringsNntpDB()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ConnectionStrings:NntpDB"] = TestHostFactory.TestNntpDbConnectionString,
                    ["NntpDb:StartupTimeout"] = "00:00:20",
                })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services
            .AddOptions<NntpDbOptions>()
            .Bind(configuration.GetSection(NntpDbOptions.SectionName))
            .Configure<IConfiguration>((options, config) =>
            {
                options.ConnectionString = config.GetConnectionString(NntpDbOptions.ConnectionStringName) ?? string.Empty;
            });
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<NntpDbOptions>>().Value;
        Assert.Equal(TestHostFactory.TestNntpDbConnectionString, options.ConnectionString);
        Assert.Equal(TimeSpan.FromSeconds(20), options.StartupTimeout);
    }

    [Fact]
    public void ProductionAppsettings_DeclaresPooledNntpDbWithoutGrabberDbChange()
    {
        var path = FindProductionAppsettings();
        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        var connectionStrings = document.RootElement.GetProperty("ConnectionStrings");
        Assert.True(connectionStrings.TryGetProperty("NntpDB", out var nntp));
        var connectionString = nntp.GetString();
        Assert.False(string.IsNullOrWhiteSpace(connectionString));
        Assert.Contains("Pooling=true", connectionString, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MinimumPoolSize=2", connectionString, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MaximumPoolSize=32", connectionString, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ConnectionIdleTimeout=300", connectionString, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Pooling=false", connectionString, StringComparison.OrdinalIgnoreCase);
        Assert.False(connectionStrings.TryGetProperty("GrabberDB", out _));

        var nntpDb = document.RootElement.GetProperty("NntpDb");
        Assert.Equal("00:00:15", nntpDb.GetProperty("StartupTimeout").GetString());
        Assert.False(nntpDb.TryGetProperty("MinimumPoolSize", out _));
        Assert.False(nntpDb.TryGetProperty("MaximumPoolSize", out _));
        Assert.False(nntpDb.TryGetProperty("MaximumIdleTime", out _));
        Assert.False(nntpDb.TryGetProperty("MaintenanceInterval", out _));
        Assert.False(nntpDb.TryGetProperty("AcquisitionTimeout", out _));
    }

    [Fact]
    public void Factory_DoesNotOverrideSuppliedPoolingSetting()
    {
        using var disabled = MySqlNntpDbConnectionFactory.CreateConnection(
            "Server=db;Database=nntpdb;User ID=nntpd;Pooling=false;");
        var disabledPooling = new MySqlConnector.MySqlConnectionStringBuilder(disabled.ConnectionString).Pooling;
        Assert.False(disabledPooling);

        using var enabled = MySqlNntpDbConnectionFactory.CreateConnection(
            "Server=db;Database=nntpdb;User ID=nntpd;Pooling=true;");
        var enabledPooling = new MySqlConnector.MySqlConnectionStringBuilder(enabled.ConnectionString).Pooling;
        Assert.True(enabledPooling);
    }

    private static string FindProductionAppsettings()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "VectorNNTP.NNTPD", "appsettings.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException("Could not locate src/VectorNNTP.NNTPD/appsettings.json.");
    }
}
