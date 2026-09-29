using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using VectorNNTP.BackFiller.Configuration;
using VectorNNTP.BackFiller.Tests.Fixtures;
using VectorNNTP.Common.Hosting;
using VectorNNTP.NNTPD.Configuration;

namespace VectorNNTP.BackFiller.Tests.Configuration;

public sealed class BackFillerOptionsValidatorTests
{
    [Fact]
    public void Validate_succeeds_for_complete_valid_options()
    {
        var options = BackFillerTestOptions.CreateValid();
        var result = BackFillerTestOptions.CreateValidator().Validate(null, options);
        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_fails_when_server_id_is_missing()
    {
        var options = BackFillerTestOptions.CreateValid();
        options.ServerId = null;
        var result = BackFillerTestOptions.CreateValidator().Validate(null, options);
        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, static f => f.Contains("ServerId", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(256)]
    public void Validate_fails_for_server_id_outside_1_to_255(int serverId)
    {
        var options = BackFillerTestOptions.CreateValid();
        options.ServerId = serverId;
        var result = BackFillerTestOptions.CreateValidator().Validate(null, options);
        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, static f => f.Contains("1–255", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_does_not_require_a_configurable_name()
    {
        var options = BackFillerTestOptions.CreateValid();
        Assert.Null(typeof(BackFillerOptions).GetProperty("Name"));
        var result = BackFillerTestOptions.CreateValidator().Validate(null, options);
        Assert.True(result.Succeeded);
        Assert.Equal("backfiller01.usenet.ninja", options.Fqdn);
    }

    [Fact]
    public void Validate_does_not_require_common_cleartext_bind_port()
    {
        var acme = BackFillerTestOptions.CreateValidAcme();
        acme.BindPort = 0;
        var result = new AcmeCloudflareOptionsValidator(new FakeLocalIpAddressAssignee(assignAll: true))
            .Validate(null, acme);
        Assert.True(result.Succeeded);
        Assert.DoesNotContain(
            result.Failures ?? [],
            static f => f.Contains("BindPort", StringComparison.Ordinal)
                        && !f.Contains("BindPortTls", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_fails_when_nested_bind_port_tls_is_missing()
    {
        var options = BackFillerTestOptions.CreateValid();
        options.BindPortTls = null;
        var result = BackFillerTestOptions.CreateValidator().Validate(null, options);
        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, static f => f.Contains("BindPortTls", StringComparison.Ordinal));
        Assert.Contains(result.Failures!, static f => f.Contains("TLS-only", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_does_not_require_application_section_acme_email()
    {
        var options = BackFillerTestOptions.CreateValid();
        var result = BackFillerTestOptions.CreateValidator().Validate(null, options);
        Assert.True(result.Succeeded);
        Assert.DoesNotContain(
            result.Failures ?? [],
            static f => f.Contains("AcmeEmail", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_fails_when_drain_timeout_exceeds_grace_period()
    {
        var options = BackFillerTestOptions.CreateValid();
        options.Shutdown.GracePeriodSeconds = 30;
        var rabbit = BackFillerTestOptions.CreateValidRabbitMq();
        rabbit.MaximumShutdownDrainTimeoutSeconds = 31;
        var result = BackFillerTestOptions.CreateValidator(rabbitMq: rabbit).Validate(null, options);
        Assert.True(result.Failed);
        Assert.Contains(
            result.Failures!,
            static f => f.Contains("MaximumShutdownDrainTimeoutSeconds", StringComparison.Ordinal)
                        && f.Contains("GracePeriodSeconds", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void Validate_accepts_independent_drain_and_finish_shutdown_flags(bool drainQueuedWork, bool finishActiveArticles)
    {
        var options = BackFillerTestOptions.CreateValid();
        options.Shutdown.DrainQueuedWork = drainQueuedWork;
        options.Shutdown.FinishActiveArticles = finishActiveArticles;
        var result = BackFillerTestOptions.CreateValidator().Validate(null, options);
        Assert.False(result.Failed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(257)]
    public void Validate_fails_for_max_openable_request_ids_outside_1_to_256(int value)
    {
        var options = BackFillerTestOptions.CreateValid();
        options.ArticleRetention.MaxOpenableRequestIdsPerArticle = value;
        var result = BackFillerTestOptions.CreateValidator().Validate(null, options);
        Assert.True(result.Failed);
        Assert.Contains(
            result.Failures!,
            static f => f.Contains("MaxOpenableRequestIdsPerArticle", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_fails_when_retention_exceeds_physical_memory_ceiling()
    {
        var options = BackFillerTestOptions.CreateValid();
        options.ArticleRetention.MaximumRetainedPayloadGigabytes = 8;
        var memory = new FakePhysicalMemoryProvider(4L * 1024 * 1024 * 1024);
        var result = BackFillerTestOptions.CreateValidator(memory: memory).Validate(null, options);
        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, static f => f.Contains("physical-memory", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_accepts_configured_four_gib_below_the_physical_memory_ceiling()
    {
        var options = BackFillerTestOptions.CreateValid();
        options.ArticleRetention.MaximumRetainedPayloadGigabytes = 4;
        var result = BackFillerTestOptions
            .CreateValidator(memory: new FakePhysicalMemoryProvider(16L * 1024 * 1024 * 1024))
            .Validate(null, options);
        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_fails_when_physical_memory_cannot_be_determined()
    {
        var options = BackFillerTestOptions.CreateValid();
        var result = BackFillerTestOptions
            .CreateValidator(memory: new FailingPhysicalMemoryProvider())
            .Validate(null, options);
        Assert.True(result.Failed);
        Assert.Contains(
            result.Failures!,
            static f => f.Contains("total physical memory could not be determined", StringComparison.Ordinal));
        Assert.DoesNotContain(
            result.Failures!,
            static f => f.Contains("80% physical-memory ceiling", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_fails_when_explicit_bind_address_is_not_local()
    {
        var acme = BackFillerTestOptions.CreateValidAcme();
        acme.BindAddress = ["198.51.100.10"];
        var result = new AcmeCloudflareOptionsValidator(new FakeLocalIpAddressAssignee(assignAll: false))
            .Validate(null, acme);
        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, static f => f.Contains("BindAddress", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_allows_omitted_bind_address()
    {
        var options = BackFillerTestOptions.CreateValid();
        options.BindAddress = null;
        var result = BackFillerTestOptions.CreateValidator().Validate(null, options);
        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(3601)]
    public void Validate_fails_when_account_refresh_interval_is_out_of_range(int seconds)
    {
        var options = BackFillerTestOptions.CreateValid();
        options.BackFillerAccountRefreshIntervalSeconds = seconds;
        var result = BackFillerTestOptions.CreateValidator().Validate(null, options);
        Assert.True(result.Failed);
        Assert.Contains(
            result.Failures!,
            static f => f.Contains("BackFillerAccountRefreshIntervalSeconds", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_fails_when_nntp_db_is_missing()
    {
        var result = new NntpDbOptionsValidator()
            .Validate(null, new NntpDbOptions());
        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, static f => f.Contains("ConnectionStrings:NntpDB", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_failures_do_not_include_secret_values()
    {
        var options = BackFillerTestOptions.CreateValid();
        options.ServerId = null;
        var rabbit = BackFillerTestOptions.CreateValidRabbitMq();
        rabbit.Password = BackFillerTestOptions.SecretPassword;
        var result = BackFillerTestOptions.CreateValidator(rabbitMq: rabbit).Validate(null, options);
        Assert.True(result.Failed);
        Assert.All(
            result.Failures!,
            static failure =>
            {
                Assert.DoesNotContain(BackFillerTestOptions.SecretPassword, failure, StringComparison.Ordinal);
                Assert.DoesNotContain(BackFillerTestOptions.SecretToken, failure, StringComparison.Ordinal);
                Assert.DoesNotContain(BackFillerTestOptions.SecretPfx, failure, StringComparison.Ordinal);
                Assert.DoesNotContain("db-secret-xyz", failure, StringComparison.Ordinal);
            });
    }

    [Fact]
    public void Bind_uses_top_level_rabbitmq_path()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BackFiller:RabbitMQ:Username"] = "nested-user",
                ["BackFiller:RabbitMQ:Password"] = "nested-password",
                ["RabbitMQ:Username"] = "canonical-user",
                ["RabbitMQ:Password"] = BackFillerTestOptions.SecretPassword,
            })
            .Build();
        var options = new BackFillerOptions();
        configuration.GetSection(BackFillerOptions.SectionName).Bind(options);
        var rabbit = new VectorNNTP.Common.Messaging.RabbitMq.RabbitMqOptions();
        configuration.GetSection("RabbitMQ").Bind(rabbit);
        Assert.Null(typeof(BackFillerOptions).GetProperty("RabbitMQ"));
        Assert.Equal("canonical-user", rabbit.Username);
        Assert.Equal(BackFillerTestOptions.SecretPassword, rabbit.Password);
    }

    [Fact]
    public void Bind_does_not_map_nested_backfiller_rabbitmq_onto_backfiller_options()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BackFiller:RabbitMQ:Username"] = "nested-user",
                ["BackFiller:RabbitMQ:Password"] = BackFillerTestOptions.SecretPassword,
            })
            .Build();
        var options = new BackFillerOptions();
        configuration.GetSection(BackFillerOptions.SectionName).Bind(options);
        Assert.Null(typeof(BackFillerOptions).GetProperty("RabbitMQ"));
    }

    [Fact]
    public void Bind_uses_canonical_nntp_db_connection_string_path()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:NntpDB"] = "Server=127.0.0.1;Database=nntp;User ID=nntparticles;Password=db-secret-xyz",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddNntpDbOptions();
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<NntpDbOptions>>().Value;
        Assert.Equal("Server=127.0.0.1;Database=nntp;User ID=nntparticles;Password=db-secret-xyz", options.ConnectionString);
        Assert.True(new NntpDbOptionsValidator().Validate(null, options).Succeeded);
    }

    [Fact]
    public void Bind_uses_server_id_not_legacy_id_key()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BackFiller:Id"] = "7",
                ["BackFiller:ServerId"] = "3",
            })
            .Build();
        var options = new BackFillerOptions();
        configuration.GetSection(BackFillerOptions.SectionName).Bind(options);
        Assert.Equal(3, options.ServerId);
    }
}
