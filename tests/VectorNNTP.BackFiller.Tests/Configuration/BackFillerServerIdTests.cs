using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using VectorNNTP.BackFiller.Configuration;
using VectorNNTP.BackFiller.Hosting;
using VectorNNTP.BackFiller.Tests.Fixtures;
using VectorNNTP.BackFiller.Tests.TestDoubles;
using VectorNNTP.NNTPD.Configuration;

using VectorNNTP.Common.Messaging.RabbitMq;

namespace VectorNNTP.BackFiller.Tests.Configuration;

/// <summary>
/// BackFiller <c>ServerId</c> cases mirrored from NNTPD
/// <c>NntpdOptionsValidatorTests</c>.
/// </summary>
public sealed class BackFillerServerIdTests
{
    [Fact]
    public void MissingServerId_FailsValidationAndIsDistinctFromZero()
    {
        var missing = BackFillerTestOptions.CreateValid();
        missing.ServerId = null;
        var missingResult = BackFillerTestOptions.CreateValidator().Validate(null, missing);
        Assert.True(missingResult.Failed);
        Assert.Contains(missingResult.Failures!, static f => f.Contains("required", StringComparison.OrdinalIgnoreCase));

        var zero = BackFillerTestOptions.CreateValid();
        zero.ServerId = 0;
        var zeroResult = BackFillerTestOptions.CreateValidator().Validate(null, zero);
        Assert.True(zeroResult.Failed);
        Assert.Contains(zeroResult.Failures!, static f => f.Contains("1–255", StringComparison.Ordinal));

        Assert.Null(missing.ServerId);
        Assert.Equal(0, zero.ServerId);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(99)]
    [InlineData(100)]
    [InlineData(255)]
    public void ServerId_ValidBoundaries_Succeed(int serverId)
    {
        var options = BackFillerTestOptions.CreateValid();
        options.ServerId = serverId;
        var result = BackFillerTestOptions.CreateValidator().Validate(null, options);
        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(256)]
    public void ServerId_OutOfRange_Fails(int serverId)
    {
        var options = BackFillerTestOptions.CreateValid();
        options.ServerId = serverId;
        var result = BackFillerTestOptions.CreateValidator().Validate(null, options);
        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, static f => f.Contains("1–255", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(1, "backfiller01.usenet.ninja")]
    [InlineData(8, "backfiller08.usenet.ninja")]
    [InlineData(9, "backfiller09.usenet.ninja")]
    [InlineData(10, "backfiller10.usenet.ninja")]
    [InlineData(99, "backfiller99.usenet.ninja")]
    public void Fqdn_UsesExactFormat_WithoutDotBeforeId(int serverId, string expected)
    {
        var options = BackFillerTestOptions.CreateValid();
        options.ServerId = serverId;
        options.DnsSuffix = "usenet.ninja";

        Assert.Equal(expected, options.Fqdn);
        Assert.Equal(
            ApplicationFqdn.Build(BackFillerOptions.ApplicationPrefix, serverId, "usenet.ninja"),
            options.Fqdn);
        Assert.NotEqual(
            ApplicationFqdn.Build("nntpd", serverId, "usenet.ninja"),
            options.Fqdn);
        Assert.DoesNotContain("backfiller.", options.Fqdn, StringComparison.Ordinal);
        Assert.StartsWith($"backfiller{serverId:00}.", options.Fqdn, StringComparison.Ordinal);

        var result = BackFillerTestOptions.CreateValidator().Validate(null, options);
        Assert.True(result.Succeeded);

        var runtime = BackFillerRuntimeOptionsFactory.Create(
            options,
            BackFillerTestOptions.CreateValidNntpDb(),
            BackFillerTestOptions.CreateValidAcme(options));
        Assert.Equal(serverId, runtime.ServerId);
        Assert.Equal(expected, runtime.Fqdn);
    }

    [Fact]
    public void Bind_ConsumesBackFillerServerId_NotRootOrEnvironmentAlias()
    {
        var pairs = BackFillerTestOptions.CreateValidConfigurationPairs();
        pairs["ServerId"] = "99";
        pairs["VECTOR__SERVERID"] = "77";
        pairs["BackFiller:ServerId"] = "8";

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(pairs)
            .Build();
        var options = new BackFillerOptions();
        configuration.GetSection(BackFillerOptions.SectionName).Bind(options);

        Assert.Equal(8, options.ServerId);
        Assert.Equal("backfiller08.usenet.ninja", options.Fqdn);
    }

    [Fact]
    public void MissingServerId_FromConfiguration_FailsStartup()
    {
        var pairs = BackFillerTestOptions.CreateValidConfigurationPairs();
        pairs.Remove("BackFiller:ServerId");

        var ex = Assert.Throws<OptionsValidationException>(() =>
        {
            using var host = CreateIsolatedHost(pairs);
            _ = host.Services.GetRequiredService<IOptions<BackFillerOptions>>().Value;
        });
        Assert.Contains("ServerId", ex.Message, StringComparison.Ordinal);
        Assert.Contains("required", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("BACKFILLER__SERVERID", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("VECTOR__SERVERID", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ServerId_NullFromConfiguration_FailsStartup()
    {
        var json = """
                   {
                     "BackFiller": {
                       "ServerId": null,
                       "DnsSuffix": "usenet.ninja",
                       "CloudFlareZoneId": "0123456789abcdef0123456789abcdef",
                       "BindAddress": [ "127.0.0.1" ],
                       "BindPortTls": 1190,
                       "LogDirectory": "logs"
                     },
                     "RabbitMQ": {
                       "Hosts": [ "127.0.0.1" ],
                       "Username": "nntparticles",
                       "Password": "unit-test-password",
                       "EnableSsl": false
                     },
                     "ConnectionStrings": {
                       "NntpDB": "Server=127.0.0.1;Database=nntp;User ID=nntparticles;Password=db-secret-xyz"
                     }
                   }
                   """;

        var builder = CreateIsolatedBuilder();
        builder.Configuration.AddJsonStream(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json)));
        builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["CloudFlareApiKey"] = BackFillerTestOptions.SecretToken,
                ["AcmeCertificatePassword"] = BackFillerTestOptions.SecretPfx,
                [AcmeCloudflareOptions.AcmeAccountConfigurationKey] = "security@usenet.ninja",
            });
        RegisterHostFakes(builder);
        builder.AddBackFillerHosting();

        var ex = Assert.Throws<OptionsValidationException>(() =>
        {
            using var host = builder.Build();
        });
        Assert.Contains("ServerId", ex.Message, StringComparison.Ordinal);
        Assert.Contains("required", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Host_runtime_uses_section_server_id_for_fqdn()
    {
        var pairs = BackFillerTestOptions.CreateValidConfigurationPairs();
        pairs["BackFiller:ServerId"] = "12";
        pairs["ServerId"] = "99";

        using var host = CreateHost(pairs);
        var identity = host.Services.GetRequiredService<IOptions<BackFillerOptions>>().Value;
        var acme = host.Services.GetRequiredService<IOptions<AcmeCloudflareOptions>>().Value;
        var runtime = host.Services.GetRequiredService<BackFillerRuntimeOptions>();

        Assert.Equal(12, identity.ServerId);
        Assert.Equal("backfiller12.usenet.ninja", identity.Fqdn);
        Assert.Equal("backfiller12.usenet.ninja", acme.Fqdn);
        Assert.Equal(12, runtime.ServerId);
        Assert.Equal("backfiller12.usenet.ninja", runtime.Fqdn);
    }

    [Fact]
    public void Production_appsettings_declares_server_id_under_backfiller()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        string? path = null;
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "VectorNNTP.BackFiller", "appsettings.json");
            if (File.Exists(candidate))
            {
                path = candidate;
                break;
            }

            directory = directory.Parent;
        }

        Assert.NotNull(path);
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        var section = root.GetProperty("BackFiller");

        Assert.False(root.TryGetProperty("ServerId", out _));
        Assert.Equal(1, section.GetProperty("ServerId").GetInt32());
        Assert.False(section.TryGetProperty("Name", out _));
    }

    [Fact]
    public void Shared_rules_match_nntpd_bounds()
    {
        Assert.Equal(1, ServerIdRules.MinimumInclusive);
        Assert.Equal(255, ServerIdRules.MaximumInclusive);
        Assert.Equal(ServerIdRules.MinimumInclusive, BackFillerIdentity.MinimumServerId);
        Assert.Equal(ServerIdRules.MaximumInclusive, BackFillerIdentity.MaximumServerId);
        Assert.Equal(ServerIdValidationStatus.Missing, ServerIdRules.Classify(null));
        Assert.Equal(ServerIdValidationStatus.OutOfRange, ServerIdRules.Classify(0));
        Assert.Equal(ServerIdValidationStatus.Valid, ServerIdRules.Classify(1));
        Assert.Equal(ServerIdValidationStatus.Valid, ServerIdRules.Classify(99));
        Assert.Equal(ServerIdValidationStatus.Valid, ServerIdRules.Classify(100));
        Assert.Equal(ServerIdValidationStatus.Valid, ServerIdRules.Classify(255));
        Assert.Equal(ServerIdValidationStatus.OutOfRange, ServerIdRules.Classify(256));
    }

    private static IHost CreateHost(Dictionary<string, string?> pairs) =>
        CreateIsolatedHost(pairs);

    private static IHost CreateIsolatedHost(Dictionary<string, string?> pairs)
    {
        var builder = CreateIsolatedBuilder();
        builder.Configuration.AddInMemoryCollection(pairs);
        RegisterHostFakes(builder);
        builder.AddBackFillerHosting();
        return builder.Build();
    }

    private static HostApplicationBuilder CreateIsolatedBuilder() =>
        Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
        {
            ApplicationName = "VectorNNTP.BackFiller.Tests",
            EnvironmentName = Environments.Development,
        });

    private static void RegisterHostFakes(HostApplicationBuilder builder)
    {
        builder.Services.AddSingleton<ILocalIpAddressAssignee>(new FakeLocalIpAddressAssignee(assignAll: true));
        builder.Services.AddSingleton<VectorNNTP.NNTPD.Cloudflare.ICloudflareDnsReconciler>(
            new NoOpCloudflareDnsReconciler());
        builder.Services.AddSingleton<IPhysicalMemoryProvider>(
            new FakePhysicalMemoryProvider(64L * 1024 * 1024 * 1024));
        builder.Services.AddSingleton<VectorNNTP.Common.Messaging.RabbitMq.IRabbitMqConnectionFactory>(
            new FakeBackFillerRabbitMqConnectionFactory());
        builder.Services.AddSingleton<VectorNNTP.BackFiller.Accounts.IProviderAccountSource>(
            new FakeProviderAccountSource());
    }
}
