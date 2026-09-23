using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using VectorNNTP.NNTPD.Cloudflare;
using VectorNNTP.NNTPD.Configuration;
using VectorNNTP.NNTPD.Core;
using VectorNNTP.NNTPD.Hosting;
using VectorNNTP.NNTPD.Logging;

namespace VectorNNTP.NNTPD.Tests.Configuration;

public sealed class NntpdOptionsValidatorTests
{
    private static NntpdOptionsValidator CreateValidator(ILocalIpAddressAssignee? assignee = null) =>
        new(assignee ?? new FakeLocalIpAddressAssignee(assignAll: true));

    [Fact]
    public void Validate_Succeeds_ForValidDefaults()
    {
        var result = CreateValidator().Validate(null, TestHostFactory.CreateValidOptions());
        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_Fails_ForEmptyApplicationName()
    {
        var options = TestHostFactory.CreateValidOptions();
        options.ApplicationName = " ";
        var result = CreateValidator().Validate(null, options);
        Assert.True(result.Failed);
    }

    [Fact]
    public void Validate_Fails_ForTooShortShutdownTimeout()
    {
        var options = TestHostFactory.CreateValidOptions();
        options.GracefulShutdownTimeout = TimeSpan.FromMilliseconds(100);
        var result = CreateValidator().Validate(null, options);
        Assert.True(result.Failed);
    }

    [Fact]
    public void Validate_Fails_ForTooShortStartupTimeout()
    {
        var options = TestHostFactory.CreateValidOptions();
        options.StartupTimeout = TimeSpan.FromMilliseconds(10);
        var result = CreateValidator().Validate(null, options);
        Assert.True(result.Failed);
    }

    [Fact]
    public void Validate_Fails_ForInvalidWatchdogIntervalFraction()
    {
        var options = TestHostFactory.CreateValidOptions();
        options.Systemd = new SystemdOptions { WatchdogIntervalFraction = 1.5 };
        var result = CreateValidator().Validate(null, options);
        Assert.True(result.Failed);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    public void Validate_Succeeds_ForValidStreamOutstandingArticleDepth(int depth)
    {
        var options = TestHostFactory.CreateValidOptions();
        options.Transit.StreamOutstandingArticleDepth = depth;
        var result = CreateValidator().Validate(null, options);
        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(17)]
    [InlineData(0)]
    [InlineData(64)]
    public void Validate_Fails_ForInvalidStreamOutstandingArticleDepth(int depth)
    {
        var options = TestHostFactory.CreateValidOptions();
        options.Transit.StreamOutstandingArticleDepth = depth;
        var result = CreateValidator().Validate(null, options);
        Assert.True(result.Failed);
        Assert.Contains(
            result.Failures!,
            f => f.Contains("StreamOutstandingArticleDepth", StringComparison.Ordinal));
    }
}

public sealed class NntpdConfigurationTests
{
    [Fact]
    public void Bind_StandardizedPascalCaseNames_FromConfiguration()
    {
        // Isolated JSON (not the working-tree appsettings) so bind defaults and PascalCase names are asserted.
        var json = """
                   {
                     "Nntpd": {
                       "CloudFlareApiKey": "unit-test-cloudflare-api-key",
                       "CloudFlareZoneId": "5811a29d39a0732afb5f160c9b137c3d",
                       "ServerId": 1
                     }
                   }
                   """;

        using var host = CreateEmptyNntpdHost(json);
        var options = host.Services.GetRequiredService<IOptions<NntpdOptions>>().Value;

        Assert.Equal(["*"], options.BindAddress);
        Assert.Equal(119, options.BindPort);
        Assert.Equal(0, options.BindPortTls);
        Assert.False(options.IsTlsListenerEnabled);
        Assert.Equal("5811a29d39a0732afb5f160c9b137c3d", options.CloudFlareZoneId);
        Assert.Equal(TestHostFactory.TestCloudFlareApiKey, options.CloudFlareApiKey);
        Assert.Equal("usenet.ninja", options.DnsSuffix);
        Assert.Equal(1, options.ServerId);
        Assert.Equal("nntpd01.usenet.ninja", options.Fqdn);
    }

    [Fact]
    public void OldUnderscoreSeparatedNames_AreNotBound()
    {
        // Underscore-separated legacy keys are not aliases. (DnsSuffix vs dnssuffix is the same
        // case-insensitive configuration key and is therefore not covered here.)
        var json = """
                   {
                     "Nntpd": {
                       "bind_address": [ "198.18.0.66" ],
                       "bind_port": 1199,
                       "bind_port_tls": 5633,
                       "cloudflare_api_key": "should-not-bind",
                       "cloudflare_zone_id": "should-not-bind-zone",
                       "server_id": 42,
                       "ServerId": 7,
                       "DnsSuffix": "example.test",
                       "CloudFlareApiKey": "unit-test-cloudflare-api-key",
                       "CloudFlareZoneId": "5811a29d39a0732afb5f160c9b137c3d"
                     }
                   }
                   """;

        var configuration = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json)))
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<ILocalIpAddressAssignee>(new FakeLocalIpAddressAssignee(assignAll: true));
        services.AddOptions<NntpdOptions>()
            .Bind(configuration.GetSection(NntpdOptions.SectionName))
            .PostConfigure(static o =>
            {
                if (o.BindAddress is null || o.BindAddress.Length == 0)
                {
                    o.BindAddress = ["*"];
                }
            })
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<NntpdOptions>, NntpdOptionsValidator>();

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<NntpdOptions>>().Value;

        Assert.Equal(["*"], options.BindAddress);
        Assert.Equal(119, options.BindPort);
        Assert.Equal(0, options.BindPortTls);
        Assert.Equal(7, options.ServerId);
        Assert.Equal("example.test", options.DnsSuffix);
        Assert.Equal("5811a29d39a0732afb5f160c9b137c3d", options.CloudFlareZoneId);
        Assert.Equal(TestHostFactory.TestCloudFlareApiKey, options.CloudFlareApiKey);
        Assert.NotEqual("should-not-bind", options.CloudFlareApiKey);
        Assert.NotEqual("should-not-bind-zone", options.CloudFlareZoneId);
    }

    [Fact]
    public void EnvironmentVariables_UseExactCloudFlareNames()
    {
        const string envKey = "env-override-cloudflare-api-key";
        const string envZone = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

        var previousKey = Environment.GetEnvironmentVariable(NntpdOptions.CloudFlareApiKeyEnvironmentVariable);
        var previousZone = Environment.GetEnvironmentVariable(NntpdOptions.CloudFlareZoneIdEnvironmentVariable);

        try
        {
            Environment.SetEnvironmentVariable(NntpdOptions.CloudFlareApiKeyEnvironmentVariable, envKey);
            Environment.SetEnvironmentVariable(NntpdOptions.CloudFlareZoneIdEnvironmentVariable, envZone);

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        [$"{NntpdOptions.SectionName}:ServerId"] = "1",
                        [$"{NntpdOptions.SectionName}:BindAddress:0"] = "*",
                        [$"{NntpdOptions.SectionName}:CloudFlareApiKey"] = "from-json-should-be-overridden",
                        [$"{NntpdOptions.SectionName}:CloudFlareZoneId"] = "from-json-should-be-overridden",
                    })
                .AddEnvironmentVariables()
                .Build();

            var services = new ServiceCollection();
            services.AddSingleton<IConfiguration>(configuration);
            services.AddSingleton<ILocalIpAddressAssignee>(new FakeLocalIpAddressAssignee(assignAll: true));
            services.AddOptions<NntpdOptions>()
                .Bind(configuration.GetSection(NntpdOptions.SectionName))
                .ValidateOnStart();
            services.AddSingleton<IValidateOptions<NntpdOptions>, NntpdOptionsValidator>();

            using var provider = services.BuildServiceProvider();
            var options = provider.GetRequiredService<IOptions<NntpdOptions>>().Value;

            Assert.Equal(envKey, options.CloudFlareApiKey);
            Assert.Equal(envZone, options.CloudFlareZoneId);
            Assert.Equal(NntpdOptions.CloudFlareApiKeyEnvironmentVariable, "nntpd__cloudflareapikey");
            Assert.Equal(NntpdOptions.CloudFlareZoneIdEnvironmentVariable, "nntpd__CloudFlareZoneId");
        }
        finally
        {
            Environment.SetEnvironmentVariable(NntpdOptions.CloudFlareApiKeyEnvironmentVariable, previousKey);
            Environment.SetEnvironmentVariable(NntpdOptions.CloudFlareZoneIdEnvironmentVariable, previousZone);
        }
    }

    [Fact]
    public void ValidationErrors_DoNotExposeApiKey()
    {
        const string secret = "super-secret-cloudflare-api-key-value";
        var options = TestHostFactory.CreateValidOptions();
        options.CloudFlareApiKey = secret;
        options.BindPort = 0;

        var result = new NntpdOptionsValidator(new FakeLocalIpAddressAssignee(assignAll: true))
            .Validate(null, options);

        Assert.True(result.Failed);
        var joined = NntpdOptionsValidator.JoinFailures(result);
        Assert.DoesNotContain(secret, joined, StringComparison.Ordinal);
        Assert.False(NntpdOptionsValidator.ContainsSecret(joined, secret));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    public void CloudFlareApiKey_BlankOrMissing_FailsValidation(string? apiKey)
    {
        var options = TestHostFactory.CreateValidOptions();
        options.CloudFlareApiKey = apiKey!;
        var result = new NntpdOptionsValidator(new FakeLocalIpAddressAssignee(assignAll: true))
            .Validate(null, options);
        Assert.True(result.Failed);
        var joined = NntpdOptionsValidator.JoinFailures(result);
        Assert.Contains(NntpdOptions.CloudFlareApiKeyConfigurationKey, joined, StringComparison.Ordinal);
        Assert.Contains(NntpdOptions.CloudFlareApiKeyEnvironmentVariable, joined, StringComparison.Ordinal);
        // Only assert non-leak for non-whitespace secrets (whitespace appears naturally in messages).
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            Assert.DoesNotContain(apiKey, joined, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void CloudFlareApiKey_ValidNonblank_Succeeds()
    {
        var options = TestHostFactory.CreateValidOptions();
        options.CloudFlareApiKey = TestHostFactory.TestCloudFlareApiKey;
        var result = new NntpdOptionsValidator(new FakeLocalIpAddressAssignee(assignAll: true))
            .Validate(null, options);
        Assert.True(result.Succeeded, NntpdOptionsValidator.JoinFailures(result));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    public void CloudFlareZoneId_BlankOrMissing_FailsValidation(string? zoneId)
    {
        var options = TestHostFactory.CreateValidOptions();
        options.CloudFlareZoneId = zoneId!;
        var result = new NntpdOptionsValidator(new FakeLocalIpAddressAssignee(assignAll: true))
            .Validate(null, options);
        Assert.True(result.Failed);
        var joined = NntpdOptionsValidator.JoinFailures(result);
        Assert.Contains(NntpdOptions.CloudFlareZoneIdConfigurationKey, joined, StringComparison.Ordinal);
        Assert.DoesNotContain("unit-test-cloudflare-api-key", joined, StringComparison.Ordinal);
    }

    [Fact]
    public void CloudFlareZoneId_ValidNonblank_Succeeds()
    {
        var options = TestHostFactory.CreateValidOptions();
        options.CloudFlareZoneId = "5811a29d39a0732afb5f160c9b137c3d";
        var result = new NntpdOptionsValidator(new FakeLocalIpAddressAssignee(assignAll: true))
            .Validate(null, options);
        Assert.True(result.Succeeded, NntpdOptionsValidator.JoinFailures(result));
    }

    [Fact]
    public void MissingCloudFlareApiKey_FailsValidation()
    {
        var options = TestHostFactory.CreateValidOptions();
        options.CloudFlareApiKey = " ";
        var result = new NntpdOptionsValidator(new FakeLocalIpAddressAssignee(assignAll: true))
            .Validate(null, options);
        Assert.True(result.Failed);
        Assert.Contains(
            NntpdOptions.CloudFlareApiKeyEnvironmentVariable,
            NntpdOptionsValidator.JoinFailures(result),
            StringComparison.Ordinal);
    }

    [Fact]
    public void MissingCloudFlareZoneId_FailsValidation()
    {
        var options = TestHostFactory.CreateValidOptions();
        options.CloudFlareZoneId = string.Empty;
        var result = new NntpdOptionsValidator(new FakeLocalIpAddressAssignee(assignAll: true))
            .Validate(null, options);
        Assert.True(result.Failed);
        Assert.Contains(
            NntpdOptions.CloudFlareZoneIdConfigurationKey,
            NntpdOptionsValidator.JoinFailures(result),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Defaults_ApplyWhenListenerSettingsOmitted()
    {
        var json = """
                   {
                     "Nntpd": {
                       "CloudFlareApiKey": "unit-test-cloudflare-api-key",
                       "CloudFlareZoneId": "5811a29d39a0732afb5f160c9b137c3d",
                       "ServerId": 1
                     }
                   }
                   """;

        var configuration = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json)))
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<ILocalIpAddressAssignee>(new FakeLocalIpAddressAssignee(assignAll: true));
        services.AddOptions<NntpdOptions>()
            .Bind(configuration.GetSection(NntpdOptions.SectionName))
            .PostConfigure(static o =>
            {
                if (o.BindAddress is null || o.BindAddress.Length == 0)
                {
                    o.BindAddress = ["*"];
                }
            })
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<NntpdOptions>, NntpdOptionsValidator>();

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<NntpdOptions>>().Value;

        Assert.Equal(["*"], options.BindAddress);
        Assert.Equal(119, options.BindPort);
        Assert.Equal(0, options.BindPortTls);
        Assert.False(options.IsTlsListenerEnabled);
        Assert.Equal("usenet.ninja", options.DnsSuffix);
    }

    [Fact]
    public void MissingServerId_FailsValidationAndIsDistinctFromZero()
    {
        var missing = TestHostFactory.CreateValidOptions();
        missing.ServerId = null;
        var missingResult = new NntpdOptionsValidator(new FakeLocalIpAddressAssignee(assignAll: true))
            .Validate(null, missing);
        Assert.True(missingResult.Failed);
        Assert.Contains("required", NntpdOptionsValidator.JoinFailures(missingResult), StringComparison.OrdinalIgnoreCase);

        var zero = TestHostFactory.CreateValidOptions();
        zero.ServerId = 0;
        var zeroResult = new NntpdOptionsValidator(new FakeLocalIpAddressAssignee(assignAll: true))
            .Validate(null, zero);
        Assert.True(zeroResult.Failed);
        Assert.Contains("1–99", NntpdOptionsValidator.JoinFailures(zeroResult), StringComparison.Ordinal);

        Assert.Null(missing.ServerId);
        Assert.Equal(0, zero.ServerId);
    }

    [Fact]
    public void MissingServerId_FromConfiguration_FailsStartup()
    {
        var json = """
                   {
                     "Nntpd": {
                       "BindAddress": [ "*" ],
                       "CloudFlareApiKey": "unit-test-cloudflare-api-key",
                       "CloudFlareZoneId": "5811a29d39a0732afb5f160c9b137c3d",
                       "DnsSuffix": "usenet.ninja"
                     }
                   }
                   """;

        var configuration = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json)))
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<ILocalIpAddressAssignee>(new FakeLocalIpAddressAssignee(assignAll: true));
        services.AddOptions<NntpdOptions>()
            .Bind(configuration.GetSection(NntpdOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<NntpdOptions>, NntpdOptionsValidator>();

        using var provider = services.BuildServiceProvider();
        var ex = Assert.Throws<OptionsValidationException>(
            () => _ = provider.GetRequiredService<IOptions<NntpdOptions>>().Value);
        Assert.Contains(nameof(NntpdOptions.ServerId), ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("198.18.0.66")]
    [InlineData("2001:db8::1")]
    public void BindAddress_AssignedAddresses_AreAccepted(string address)
    {
        var ip = IPAddress.Parse(address);
        var options = TestHostFactory.CreateValidOptions();
        options.BindAddress = [address];
        var result = new NntpdOptionsValidator(new FakeLocalIpAddressAssignee(ip))
            .Validate(null, options);
        Assert.True(result.Succeeded, NntpdOptionsValidator.JoinFailures(result));
    }

    [Fact]
    public void BindAddress_MultipleConfiguredAddresses_AreAllValidated()
    {
        var options = TestHostFactory.CreateValidOptions();
        options.BindAddress = ["198.18.0.66", "2001:db8::1"];
        var assignee = new FakeLocalIpAddressAssignee(
            IPAddress.Parse("198.18.0.66"),
            IPAddress.Parse("2001:db8::1"));
        var result = new NntpdOptionsValidator(assignee).Validate(null, options);
        Assert.True(result.Succeeded);
    }

    [Fact]
    public void BindAddress_AssignedPrivateAndPublic_AreAccepted()
    {
        var options = TestHostFactory.CreateValidOptions();
        options.BindAddress = ["10.0.0.5", "203.0.113.10"];
        var assignee = new FakeLocalIpAddressAssignee(
            IPAddress.Parse("10.0.0.5"),
            IPAddress.Parse("203.0.113.10"));
        var result = new NntpdOptionsValidator(assignee).Validate(null, options);
        Assert.True(result.Succeeded);
    }

    [Fact]
    public void BindAddress_UnassignedAddress_IsRejected()
    {
        var options = TestHostFactory.CreateValidOptions();
        options.BindAddress = ["198.18.0.66"];
        var result = new NntpdOptionsValidator(new FakeLocalIpAddressAssignee(false))
            .Validate(null, options);
        Assert.True(result.Failed);
        Assert.Contains("not assigned", NntpdOptionsValidator.JoinFailures(result), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BindAddress_InvalidSyntax_IsRejected()
    {
        var options = TestHostFactory.CreateValidOptions();
        options.BindAddress = ["not-an-ip"];
        var result = new NntpdOptionsValidator(new FakeLocalIpAddressAssignee(assignAll: true))
            .Validate(null, options);
        Assert.True(result.Failed);
    }

    [Theory]
    [InlineData("*")]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    public void BindAddress_Wildcards_AreAcceptedWithoutNicAssignment(string wildcard)
    {
        var options = TestHostFactory.CreateValidOptions();
        options.BindAddress = [wildcard];
        var result = new NntpdOptionsValidator(new FakeLocalIpAddressAssignee(false))
            .Validate(null, options);
        Assert.True(result.Succeeded, NntpdOptionsValidator.JoinFailures(result));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(119)]
    [InlineData(65535)]
    public void BindPort_ValidRange_Succeeds(int port)
    {
        var options = TestHostFactory.CreateValidOptions();
        options.BindPort = port;
        var result = new NntpdOptionsValidator(new FakeLocalIpAddressAssignee(assignAll: true))
            .Validate(null, options);
        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public void BindPort_InvalidValues_Fail(int port)
    {
        var options = TestHostFactory.CreateValidOptions();
        options.BindPort = port;
        var result = new NntpdOptionsValidator(new FakeLocalIpAddressAssignee(assignAll: true))
            .Validate(null, options);
        Assert.True(result.Failed);
    }

    [Fact]
    public void BindPortTls_UnsetAndZero_DisableTls()
    {
        var unset = TestHostFactory.CreateValidOptions();
        unset.BindPortTls = default;
        Assert.Equal(0, unset.BindPortTls);
        Assert.False(unset.IsTlsListenerEnabled);
        Assert.True(
            new NntpdOptionsValidator(new FakeLocalIpAddressAssignee(assignAll: true))
                .Validate(null, unset)
                .Succeeded);

        var explicitZero = TestHostFactory.CreateValidOptions();
        explicitZero.BindPortTls = 0;
        Assert.False(explicitZero.IsTlsListenerEnabled);
        Assert.True(
            new NntpdOptionsValidator(new FakeLocalIpAddressAssignee(assignAll: true))
                .Validate(null, explicitZero)
                .Succeeded);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(563)]
    [InlineData(65535)]
    public void BindPortTls_NonZero_EnablesTlsConfiguration(int port)
    {
        var options = TestHostFactory.CreateValidOptions();
        options.BindPortTls = port;
        options.AcmeEmail = "ops@example.org";
        options.AcmeCertificatePassword = "unit-test-pfx-password";
        Assert.True(options.IsTlsListenerEnabled);
        var result = new NntpdOptionsValidator(new FakeLocalIpAddressAssignee(assignAll: true))
            .Validate(null, options);
        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(65536)]
    public void BindPortTls_OutOfRange_Fails(int port)
    {
        var options = TestHostFactory.CreateValidOptions();
        options.BindPortTls = port;
        var result = new NntpdOptionsValidator(new FakeLocalIpAddressAssignee(assignAll: true))
            .Validate(null, options);
        Assert.True(result.Failed);
    }

    [Theory]
    [InlineData(1, "nntpd01.usenet.ninja")]
    [InlineData(8, "nntpd08.usenet.ninja")]
    [InlineData(9, "nntpd09.usenet.ninja")]
    [InlineData(10, "nntpd10.usenet.ninja")]
    [InlineData(99, "nntpd99.usenet.ninja")]
    public void Fqdn_UsesExactFormat_WithoutDotBeforeId(int serverId, string expected)
    {
        var options = TestHostFactory.CreateValidOptions();
        options.ServerId = serverId;
        options.DnsSuffix = "usenet.ninja";

        Assert.Equal(expected, options.Fqdn);
        Assert.DoesNotContain("nntpd.", options.Fqdn, StringComparison.Ordinal);
        Assert.StartsWith($"nntpd{serverId:00}.", options.Fqdn, StringComparison.Ordinal);

        var result = new NntpdOptionsValidator(new FakeLocalIpAddressAssignee(assignAll: true))
            .Validate(null, options);
        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(99)]
    public void ServerId_ValidBoundaries_Succeed(int serverId)
    {
        var options = TestHostFactory.CreateValidOptions();
        options.ServerId = serverId;
        var result = new NntpdOptionsValidator(new FakeLocalIpAddressAssignee(assignAll: true))
            .Validate(null, options);
        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(100)]
    public void ServerId_OutOfRange_Fails(int serverId)
    {
        var options = TestHostFactory.CreateValidOptions();
        options.ServerId = serverId;
        var result = new NntpdOptionsValidator(new FakeLocalIpAddressAssignee(assignAll: true))
            .Validate(null, options);
        Assert.True(result.Failed);
        Assert.Contains("1–99", NntpdOptionsValidator.JoinFailures(result), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("-bad.example")]
    [InlineData("bad..example")]
    [InlineData("bad_label.example")]
    public void DnsSuffix_Invalid_Fails(string suffix)
    {
        var options = TestHostFactory.CreateValidOptions();
        options.DnsSuffix = suffix;
        var result = new NntpdOptionsValidator(new FakeLocalIpAddressAssignee(assignAll: true))
            .Validate(null, options);
        Assert.True(result.Failed);
    }

    [Fact]
    public void Fqdn_CannotBeIndependentlyConfiguredOrOverridden()
    {
        var json = """
                   {
                     "Nntpd": {
                       "BindAddress": [ "*" ],
                       "BindPort": 119,
                       "BindPortTls": 0,
                       "CloudFlareApiKey": "unit-test-cloudflare-api-key",
                       "CloudFlareZoneId": "5811a29d39a0732afb5f160c9b137c3d",
                       "DnsSuffix": "usenet.ninja",
                       "ServerId": 8,
                       "Fqdn": "evil.example.com"
                     }
                   }
                   """;

        using var host = CreateEmptyNntpdHost(json);
        var options = host.Services.GetRequiredService<IOptions<NntpdOptions>>().Value;

        Assert.Equal(8, options.ServerId);
        Assert.Equal("nntpd08.usenet.ninja", options.Fqdn);
        Assert.DoesNotContain("evil", options.Fqdn, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HostStart_MissingCloudFlareApiKey_FailsBeforeRunning()
    {
        var json = """
                   {
                     "Nntpd": {
                       "BindAddress": [ "*" ],
                       "CloudFlareApiKey": "",
                       "CloudFlareZoneId": "5811a29d39a0732afb5f160c9b137c3d",
                       "ServerId": 1
                     }
                   }
                   """;

        using var host = CreateEmptyNntpdHost(json);
        var ex = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
        Assert.Contains(NntpdOptions.CloudFlareApiKeyConfigurationKey, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(TestHostFactory.TestCloudFlareApiKey, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HostStart_MissingCloudFlareZoneId_FailsBeforeRunning()
    {
        var json = """
                   {
                     "Nntpd": {
                       "BindAddress": [ "*" ],
                       "CloudFlareApiKey": "unit-test-cloudflare-api-key",
                       "CloudFlareZoneId": " ",
                       "ServerId": 1
                     }
                   }
                   """;

        using var host = CreateEmptyNntpdHost(json);
        var ex = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
        Assert.Contains(NntpdOptions.CloudFlareZoneIdConfigurationKey, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(TestHostFactory.TestCloudFlareApiKey, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HostStart_MissingServerId_FailsBeforeRunning()
    {
        var json = """
                   {
                     "Nntpd": {
                       "BindAddress": [ "*" ],
                       "CloudFlareApiKey": "unit-test-cloudflare-api-key",
                       "CloudFlareZoneId": "5811a29d39a0732afb5f160c9b137c3d"
                     }
                   }
                   """;

        using var host = CreateEmptyNntpdHost(json);
        var ex = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
        Assert.Contains(nameof(NntpdOptions.ServerId), ex.Message, StringComparison.Ordinal);
        Assert.Contains(NntpdOptions.ServerIdEnvironmentVariable, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(TestHostFactory.TestCloudFlareApiKey, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HostStart_ValidMandatorySettings_ReachesRunning()
    {
        var json = """
                   {
                     "Nntpd": {
                       "BindAddress": [ "*" ],
                       "CloudFlareApiKey": "unit-test-cloudflare-api-key",
                       "CloudFlareZoneId": "5811a29d39a0732afb5f160c9b137c3d",
                       "ServerId": 1
                     }
                   }
                   """;

        using var host = CreateEmptyNntpdHost(json);
        await host.StartAsync();
        var lifecycle = host.Services.GetRequiredService<ApplicationLifecycle>();
        Assert.Equal(ApplicationState.Running, lifecycle.State);
        await host.StopAsync();
    }

    [Fact]
    public void CloudFlareApiKey_NullFromConfiguration_FailsStartup()
    {
        var json = """
                   {
                     "Nntpd": {
                       "BindAddress": [ "*" ],
                       "CloudFlareApiKey": null,
                       "CloudFlareZoneId": "5811a29d39a0732afb5f160c9b137c3d",
                       "ServerId": 1
                     }
                   }
                   """;

        using var host = CreateEmptyNntpdHost(json);
        var ex = Assert.Throws<OptionsValidationException>(
            () => _ = host.Services.GetRequiredService<IOptions<NntpdOptions>>().Value);
        Assert.Contains(NntpdOptions.CloudFlareApiKeyConfigurationKey, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CloudFlareZoneId_NullFromConfiguration_FailsStartup()
    {
        var json = """
                   {
                     "Nntpd": {
                       "BindAddress": [ "*" ],
                       "CloudFlareApiKey": "unit-test-cloudflare-api-key",
                       "CloudFlareZoneId": null,
                       "ServerId": 1
                     }
                   }
                   """;

        using var host = CreateEmptyNntpdHost(json);
        var ex = Assert.Throws<OptionsValidationException>(
            () => _ = host.Services.GetRequiredService<IOptions<NntpdOptions>>().Value);
        Assert.Contains(NntpdOptions.CloudFlareZoneIdConfigurationKey, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(TestHostFactory.TestCloudFlareApiKey, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ServerId_NullFromConfiguration_FailsStartup()
    {
        var json = """
                   {
                     "Nntpd": {
                       "BindAddress": [ "*" ],
                       "CloudFlareApiKey": "unit-test-cloudflare-api-key",
                       "CloudFlareZoneId": "5811a29d39a0732afb5f160c9b137c3d",
                       "ServerId": null
                     }
                   }
                   """;

        using var host = CreateEmptyNntpdHost(json);
        var ex = Assert.Throws<OptionsValidationException>(
            () => _ = host.Services.GetRequiredService<IOptions<NntpdOptions>>().Value);
        Assert.Contains(nameof(NntpdOptions.ServerId), ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EnvironmentVariables_DocumentedNames_AreExact()
    {
        Assert.Equal("nntpd__cloudflareapikey", NntpdOptions.CloudFlareApiKeyEnvironmentVariable);
        Assert.Equal("nntpd__CloudFlareZoneId", NntpdOptions.CloudFlareZoneIdEnvironmentVariable);
        Assert.Equal("nntpd__ServerId", NntpdOptions.ServerIdEnvironmentVariable);
    }

    private static IHost CreateEmptyNntpdHost(string json)
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
        {
            ApplicationName = "VectorNNTP.NNTPD.Tests",
            EnvironmentName = Environments.Development,
        });

        builder.Configuration.AddJsonStream(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json)));
        builder.Services.AddSingleton<ILocalIpAddressAssignee>(new FakeLocalIpAddressAssignee(assignAll: true));
        builder.Services.AddSingleton<ICloudflareDnsClient>(new FakeCloudflareDnsClient());
        builder.Services.PostConfigure<NntpdOptions>(static options =>
        {
            options.BindPortTls = 0;
            options.AcmeEmail = string.Empty;
        });
        builder.ConfigureNntpdLogging(static lc => lc.MinimumLevel.Fatal());
        builder.Services.AddNntpdHosting(includePlaceholderService: false);
        return builder.Build();
    }

    private static IHost CreateConfiguredHost(
        IReadOnlyDictionary<string, string?>? configuration = null,
        ILocalIpAddressAssignee? assignee = null)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                [$"{NntpdOptions.SectionName}:{NntpdOptions.CloudFlareApiKeyConfigurationKey}"] =
                    TestHostFactory.TestCloudFlareApiKey,
                [$"{NntpdOptions.SectionName}:BindAddress:0"] = "*",
                [$"{NntpdOptions.SectionName}:BindAddress:1"] = null,
            });

        if (configuration is not null)
        {
            builder.Configuration.AddInMemoryCollection(configuration);
        }

        builder.Services.AddSingleton<ILocalIpAddressAssignee>(
            assignee ?? new FakeLocalIpAddressAssignee(assignAll: true));
        builder.Services.AddSingleton<ICloudflareDnsClient>(new FakeCloudflareDnsClient());
        builder.Services.PostConfigure<NntpdOptions>(static options =>
        {
            options.BindAddress = ["*"];
            options.BindPortTls = 0;
            options.AcmeEmail = string.Empty;
        });
        TestHostFactory.IsolateTransit(builder.Services);

        builder.ConfigureNntpdLogging(static lc => lc.MinimumLevel.Fatal());
        builder.Services.AddNntpdHosting(includePlaceholderService: false);
        return builder.Build();
    }
}
