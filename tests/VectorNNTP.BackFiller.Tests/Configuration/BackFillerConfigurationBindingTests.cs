using Microsoft.Extensions.Configuration;
using VectorNNTP.BackFiller.Configuration;
using VectorNNTP.BackFiller.Tests.Fixtures;

namespace VectorNNTP.BackFiller.Tests.Configuration;

public sealed class BackFillerConfigurationBindingTests
{
    [Fact]
    public void Canonical_environment_variable_names_map_to_nested_configuration_paths()
    {
        Assert.Equal("backfiller__", BackFillerOptions.EnvironmentVariablePrefix);
        Assert.Equal("BackFiller", BackFillerOptions.SectionName);
        Assert.Equal("ConnectionStrings", BackFillerConnectionStringsOptions.SectionName);

        Assert.Equal("BackFiller:Name", ToConfigurationPath(BackFillerOptions.NameEnvironmentVariable));
        Assert.Equal("BackFiller:ServerId", ToConfigurationPath(BackFillerOptions.ServerIdEnvironmentVariable));
        Assert.Equal("BackFiller:RabbitMQ:Username", ToConfigurationPath(BackFillerOptions.RabbitMqUsernameEnvironmentVariable));
        Assert.Equal("BackFiller:RabbitMQ:Password", ToConfigurationPath(BackFillerOptions.RabbitMqPasswordEnvironmentVariable));
        Assert.Equal("BackFiller:LetsEncrypt:CloudFlareApiToken", ToConfigurationPath(BackFillerOptions.CloudFlareApiTokenEnvironmentVariable));
        Assert.Equal("BackFiller:LetsEncrypt:PfxExportPassword", ToConfigurationPath(BackFillerOptions.PfxExportPasswordEnvironmentVariable));
        Assert.Equal("ConnectionStrings:GrabberDB", ToConfigurationPath(BackFillerOptions.GrabberDbEnvironmentVariable));
    }

    [Fact]
    public void Bind_populates_options_from_canonical_prefixed_paths()
    {
        var configuration = ConfigurationFromEnvironmentVariables(
            (BackFillerOptions.NameEnvironmentVariable, "canonical-name"),
            (BackFillerOptions.ServerIdEnvironmentVariable, "8"),
            (BackFillerOptions.RabbitMqUsernameEnvironmentVariable, "canonical-user"),
            (BackFillerOptions.RabbitMqPasswordEnvironmentVariable, BackFillerTestOptions.SecretPassword),
            (BackFillerOptions.CloudFlareApiTokenEnvironmentVariable, BackFillerTestOptions.SecretToken),
            (BackFillerOptions.PfxExportPasswordEnvironmentVariable, BackFillerTestOptions.SecretPfx),
            (BackFillerOptions.GrabberDbEnvironmentVariable, "Server=127.0.0.1;Database=nntp;User ID=nntparticles;Password=db-secret-xyz"));

        var options = new BackFillerOptions();
        configuration.GetSection(BackFillerOptions.SectionName).Bind(options);
        var connectionStrings = new BackFillerConnectionStringsOptions();
        configuration.GetSection(BackFillerConnectionStringsOptions.SectionName).Bind(connectionStrings);

        Assert.Equal("canonical-name", options.Name);
        Assert.Equal(8, options.ServerId);
        Assert.Equal("canonical-user", options.RabbitMQ.Username);
        Assert.Equal(BackFillerTestOptions.SecretPassword, options.RabbitMQ.Password);
        Assert.Equal(BackFillerTestOptions.SecretToken, options.LetsEncrypt.CloudFlareApiToken);
        Assert.Equal(BackFillerTestOptions.SecretPfx, options.LetsEncrypt.PfxExportPassword);
        Assert.Equal("Server=127.0.0.1;Database=nntp;User ID=nntparticles;Password=db-secret-xyz", connectionStrings.GrabberDB);
    }

    [Theory]
    [InlineData("backfiller__RabbitMQ__Username", "backfiller__RabbitMQ__Password")]
    [InlineData("backfiller__LetsEncrypt__CloudFlareApiToken", "backfiller__LetsEncrypt__PfxExportPassword")]
    public void Bind_does_not_accept_short_form_secret_aliases(string usernameOrToken, string passwordOrPfx)
    {
        var configuration = ConfigurationFromEnvironmentVariables(
            (usernameOrToken, "short-form-user-or-token"),
            (passwordOrPfx, BackFillerTestOptions.SecretPassword));

        var options = new BackFillerOptions();
        configuration.GetSection(BackFillerOptions.SectionName).Bind(options);

        Assert.True(string.IsNullOrWhiteSpace(options.RabbitMQ.Username));
        Assert.True(string.IsNullOrWhiteSpace(options.RabbitMQ.Password));
        Assert.True(string.IsNullOrWhiteSpace(options.LetsEncrypt.CloudFlareApiToken));
        Assert.True(string.IsNullOrWhiteSpace(options.LetsEncrypt.PfxExportPassword));
    }

    [Fact]
    public void Bind_does_not_map_legacy_id_key_to_server_id()
    {
        var configuration = ConfigurationFromEnvironmentVariables(
            ("backfiller__BackFiller__Id", "7"));

        var options = new BackFillerOptions();
        configuration.GetSection(BackFillerOptions.SectionName).Bind(options);
        Assert.Null(options.ServerId);
    }

    [Fact]
    public void Grabber_db_validation_failures_do_not_include_the_connection_string()
    {
        var options = new BackFillerConnectionStringsOptions
        {
            GrabberDB = "Server=127.0.0.1;User ID=nntparticles;Password=db-secret-xyz",
        };

        var result = new BackFillerConnectionStringsOptionsValidator().Validate(null, options);
        Assert.True(result.Failed);
        var raw = options.GrabberDB!;
        Assert.All(
            result.Failures!,
            failure =>
            {
                Assert.DoesNotContain("db-secret-xyz", failure, StringComparison.Ordinal);
                Assert.DoesNotContain(raw, failure, StringComparison.Ordinal);
            });
    }

    [Fact]
    public void Prefixed_environment_variables_reach_runtime_options()
    {
        using var environment = new IsolatedEnvironment(
            (BackFillerOptions.NameEnvironmentVariable, "env-name"),
            (BackFillerOptions.ServerIdEnvironmentVariable, "8"),
            (BackFillerOptions.RabbitMqUsernameEnvironmentVariable, "env-user"),
            (BackFillerOptions.RabbitMqPasswordEnvironmentVariable, BackFillerTestOptions.SecretPassword),
            (BackFillerOptions.CloudFlareApiTokenEnvironmentVariable, BackFillerTestOptions.SecretToken),
            ("backfiller__BackFiller__LetsEncrypt__CloudFlareZoneId", "0123456789abcdef0123456789abcdef"),
            (BackFillerOptions.PfxExportPasswordEnvironmentVariable, BackFillerTestOptions.SecretPfx),
            (BackFillerOptions.GrabberDbEnvironmentVariable, "Server=127.0.0.1;Database=nntp;User ID=nntparticles;Password=db-secret-xyz"));

        var configuration = environment.BuildPrefixedConfiguration();
        var options = new BackFillerOptions();
        configuration.GetSection(BackFillerOptions.SectionName).Bind(options);
        var connectionStrings = new BackFillerConnectionStringsOptions();
        configuration.GetSection(BackFillerConnectionStringsOptions.SectionName).Bind(connectionStrings);

        Assert.Equal("env-name", options.Name);
        Assert.Equal(8, options.ServerId);
        Assert.Equal("env-user", options.RabbitMQ.Username);
        Assert.Equal(BackFillerTestOptions.SecretPassword, options.RabbitMQ.Password);
        Assert.Equal(BackFillerTestOptions.SecretToken, options.LetsEncrypt.CloudFlareApiToken);
        Assert.Equal(BackFillerTestOptions.SecretPfx, options.LetsEncrypt.PfxExportPassword);
        Assert.Equal("Server=127.0.0.1;Database=nntp;User ID=nntparticles;Password=db-secret-xyz", connectionStrings.GrabberDB);
    }

    [Fact]
    public void Whitespace_name_from_the_prefixed_environment_fails_validation()
    {
        using var environment = new IsolatedEnvironment(
            (BackFillerOptions.NameEnvironmentVariable, "   "));

        var configuration = environment.BuildPrefixedConfiguration();
        var options = BackFillerTestOptions.CreateValid();
        configuration.GetSection(BackFillerOptions.SectionName).Bind(options);

        Assert.True(string.IsNullOrWhiteSpace(options.Name));
        var result = BackFillerTestOptions.CreateValidator().Validate(null, options);
        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, static failure => failure.Contains("Name", StringComparison.Ordinal));
        Assert.All(
            result.Failures!,
            failure =>
            {
                Assert.DoesNotContain(BackFillerTestOptions.SecretPassword, failure, StringComparison.Ordinal);
                Assert.DoesNotContain(BackFillerTestOptions.SecretPfx, failure, StringComparison.Ordinal);
            });
    }

    [Fact]
    public void Malformed_server_id_from_the_prefixed_environment_fails_at_bind()
    {
        using var environment = new IsolatedEnvironment(
            (BackFillerOptions.ServerIdEnvironmentVariable, "not-an-integer"));

        var configuration = environment.BuildPrefixedConfiguration();
        var options = BackFillerTestOptions.CreateValid();
        var ex = Assert.Throws<InvalidOperationException>(
            () => configuration.GetSection(BackFillerOptions.SectionName).Bind(options));
        Assert.Contains("ServerId", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(BackFillerTestOptions.SecretPassword, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Short_form_environment_names_do_not_bind_through_the_real_prefix_provider()
    {
        using var environment = new IsolatedEnvironment(
            ("backfiller__RabbitMQ__Username", "short-form-user"),
            ("backfiller__RabbitMQ__Password", BackFillerTestOptions.SecretPassword));

        var configuration = environment.BuildPrefixedConfiguration();
        var options = new BackFillerOptions();
        configuration.GetSection(BackFillerOptions.SectionName).Bind(options);
        Assert.True(string.IsNullOrWhiteSpace(options.RabbitMQ.Username));
        Assert.True(string.IsNullOrWhiteSpace(options.RabbitMQ.Password));
    }

    private static IConfiguration ConfigurationFromEnvironmentVariables(params (string Name, string Value)[] variables)
    {
        var pairs = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in variables)
        {
            pairs[ToConfigurationPath(name)] = value;
        }

        return new ConfigurationBuilder()
            .AddInMemoryCollection(pairs)
            .Build();
    }

    private static string ToConfigurationPath(string environmentVariable)
    {
        Assert.StartsWith(
            BackFillerOptions.EnvironmentVariablePrefix,
            environmentVariable,
            StringComparison.OrdinalIgnoreCase);
        return environmentVariable[BackFillerOptions.EnvironmentVariablePrefix.Length..]
            .Replace("__", ":", StringComparison.Ordinal);
    }

    private sealed class IsolatedEnvironment : IDisposable
    {
        private readonly (string Name, string? Previous)[] _previous;

        public IsolatedEnvironment(params (string Name, string Value)[] variables)
        {
            _previous = variables
                .Select(static pair => (pair.Name, Environment.GetEnvironmentVariable(pair.Name)))
                .ToArray();
            foreach (var (name, value) in variables)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }

        public IConfiguration BuildPrefixedConfiguration() =>
            new ConfigurationBuilder()
                .AddEnvironmentVariables(prefix: BackFillerOptions.EnvironmentVariablePrefix)
                .Build();

        public void Dispose()
        {
            foreach (var (name, previous) in _previous)
            {
                Environment.SetEnvironmentVariable(name, previous);
            }
        }
    }
}
