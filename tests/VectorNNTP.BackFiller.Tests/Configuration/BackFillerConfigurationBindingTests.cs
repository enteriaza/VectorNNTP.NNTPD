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
}
