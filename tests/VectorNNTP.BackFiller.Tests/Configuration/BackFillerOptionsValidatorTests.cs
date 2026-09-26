using Microsoft.Extensions.Configuration;
using VectorNNTP.BackFiller.Configuration;
using VectorNNTP.BackFiller.Tests.Fixtures;
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
    [InlineData(100)]
    public void Validate_fails_for_server_id_outside_0_to_99(int serverId)
    {
        var options = BackFillerTestOptions.CreateValid();
        options.ServerId = serverId;
        var result = BackFillerTestOptions.CreateValidator().Validate(null, options);
        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, static f => f.Contains("ServerId", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_allows_server_id_zero()
    {
        var options = BackFillerTestOptions.CreateValid();
        options.ServerId = 0;
        var result = BackFillerTestOptions.CreateValidator().Validate(null, options);
        Assert.True(result.Succeeded);
        Assert.Equal("backfiller00.usenet.ninja", options.Fqdn);
    }

    [Fact]
    public void Validate_fails_when_name_is_missing()
    {
        var options = BackFillerTestOptions.CreateValid();
        options.Name = " ";
        var result = BackFillerTestOptions.CreateValidator().Validate(null, options);
        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, static f => f.Contains("Name", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_fails_when_shared_bind_port_is_invalid()
    {
        var acme = BackFillerTestOptions.CreateValidAcme();
        acme.BindPort = 0;
        acme.BindPortTls = 0;
        var result = new AcmeCloudflareOptionsValidator(new FakeLocalIpAddressAssignee(assignAll: true))
            .Validate(null, acme);
        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, static f => f.Contains("BindPort", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_fails_when_drain_timeout_exceeds_grace_period()
    {
        var options = BackFillerTestOptions.CreateValid();
        options.Shutdown.GracePeriodSeconds = 30;
        options.RabbitMQ.MaximumShutdownDrainTimeoutSeconds = 31;
        var result = BackFillerTestOptions.CreateValidator().Validate(null, options);
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
    public void Validate_fails_when_accounts_refresh_interval_is_out_of_range(int seconds)
    {
        var options = BackFillerTestOptions.CreateValid();
        options.Accounts.RefreshIntervalSeconds = seconds;
        var result = BackFillerTestOptions.CreateValidator().Validate(null, options);
        Assert.True(result.Failed);
        Assert.Contains(
            result.Failures!,
            static f => f.Contains("Accounts:RefreshIntervalSeconds", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(121)]
    public void Validate_fails_when_accounts_command_timeout_is_out_of_range(int seconds)
    {
        var options = BackFillerTestOptions.CreateValid();
        options.Accounts.CommandTimeoutSeconds = seconds;
        var result = BackFillerTestOptions.CreateValidator().Validate(null, options);
        Assert.True(result.Failed);
        Assert.Contains(
            result.Failures!,
            static f => f.Contains("Accounts:CommandTimeoutSeconds", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_fails_when_grabber_db_is_missing()
    {
        var result = new BackFillerConnectionStringsOptionsValidator()
            .Validate(null, new BackFillerConnectionStringsOptions());
        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, static f => f.Contains("GrabberDB", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_failures_do_not_include_secret_values()
    {
        var options = BackFillerTestOptions.CreateValid();
        options.ServerId = null;
        options.RabbitMQ.Password = BackFillerTestOptions.SecretPassword;
        var result = BackFillerTestOptions.CreateValidator().Validate(null, options);
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
    public void Bind_uses_canonical_nested_rabbitmq_path()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BackFiller:RabbitMQ:Username"] = "canonical-user",
                ["BackFiller:RabbitMQ:Password"] = BackFillerTestOptions.SecretPassword,
                ["RabbitMQ:Username"] = "short-form-user",
                ["RabbitMQ:Password"] = "short-form-password",
            })
            .Build();
        var options = new BackFillerOptions();
        configuration.GetSection(BackFillerOptions.SectionName).Bind(options);
        Assert.Equal("canonical-user", options.RabbitMQ.Username);
        Assert.Equal(BackFillerTestOptions.SecretPassword, options.RabbitMQ.Password);
    }

    [Fact]
    public void Bind_does_not_treat_root_rabbitmq_as_a_supported_alias()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RabbitMQ:Username"] = "short-form-user",
                ["RabbitMQ:Password"] = BackFillerTestOptions.SecretPassword,
            })
            .Build();
        var options = new BackFillerOptions();
        configuration.GetSection(BackFillerOptions.SectionName).Bind(options);
        Assert.True(string.IsNullOrWhiteSpace(options.RabbitMQ.Username));
        Assert.True(string.IsNullOrWhiteSpace(options.RabbitMQ.Password));
    }

    [Fact]
    public void Bind_uses_canonical_connection_strings_path()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:GrabberDB"] = "Server=127.0.0.1;Database=nntp;User ID=nntparticles;Password=db-secret-xyz",
            })
            .Build();
        var options = new BackFillerConnectionStringsOptions();
        configuration.GetSection(BackFillerConnectionStringsOptions.SectionName).Bind(options);
        Assert.Equal("Server=127.0.0.1;Database=nntp;User ID=nntparticles;Password=db-secret-xyz", options.GrabberDB);
        Assert.True(new BackFillerConnectionStringsOptionsValidator().Validate(null, options).Succeeded);
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
