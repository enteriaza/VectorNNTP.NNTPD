using Microsoft.Extensions.Configuration;
using VectorNNTP.BackFiller.Configuration;
using VectorNNTP.BackFiller.Tests.Fixtures;
using VectorNNTP.NNTPD.Configuration;

namespace VectorNNTP.BackFiller.Tests.Configuration;

public sealed class BackFillerConfigurationBindingTests
{
    [Fact]
    public void Shared_acme_cloudflare_environment_variables_are_vector_names()
    {
        Assert.Equal("CLOUDFLAREAPIKEY", StripVectorPrefix("VECTOR__CLOUDFLAREAPIKEY"));
        Assert.Equal("ACMECERTIFICATEPASSWORD", StripVectorPrefix("VECTOR__ACMECERTIFICATEPASSWORD"));
        Assert.Equal("CLOUDFLAREZONEID", StripVectorPrefix("VECTOR__CLOUDFLAREZONEID"));
        Assert.Equal("BINDPORT", StripVectorPrefix("VECTOR__BINDPORT"));

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CloudFlareApiKey"] = BackFillerTestOptions.SecretToken,
                ["AcmeCertificatePassword"] = BackFillerTestOptions.SecretPfx,
                ["CloudFlareZoneId"] = "0123456789abcdef0123456789abcdef",
            })
            .Build();

        var options = new AcmeCloudflareOptions();
        configuration.Bind(options);
        Assert.Equal(BackFillerTestOptions.SecretToken, options.CloudFlareApiKey);
        Assert.Equal(BackFillerTestOptions.SecretPfx, options.AcmeCertificatePassword);
        Assert.Equal("0123456789abcdef0123456789abcdef", options.CloudFlareZoneId);
    }

    [Fact]
    public void Canonical_environment_variable_names_map_to_root_configuration_paths()
    {
        Assert.Equal(VectorEnvironment.Prefix, BackFillerOptions.EnvironmentVariablePrefix);
        Assert.Equal("BackFiller", BackFillerOptions.SectionName);
        Assert.Equal("ConnectionStrings", BackFillerConnectionStringsOptions.SectionName);

        Assert.Equal("BackFiller:Name", ToApplicationConfigurationPath(BackFillerOptions.NameEnvironmentVariable));
        Assert.Equal("BackFiller:ServerId", ToApplicationConfigurationPath(BackFillerOptions.ServerIdEnvironmentVariable));
        Assert.Equal("RABBITMQ:USERNAME", ToConfigurationPath(BackFillerOptions.RabbitMqUsernameEnvironmentVariable));
        Assert.Equal("RABBITMQ:PASSWORD", ToConfigurationPath(BackFillerOptions.RabbitMqPasswordEnvironmentVariable));
        Assert.Equal("CONNECTIONSTRINGS:GRABBERDB", ToConfigurationPath(BackFillerOptions.GrabberDbEnvironmentVariable));
        Assert.False(VectorEnvironment.IsCanonicalName(BackFillerOptions.NameEnvironmentVariable));
        Assert.False(VectorEnvironment.IsCanonicalName(BackFillerOptions.ServerIdEnvironmentVariable));
        Assert.True(VectorEnvironment.IsCanonicalName(BackFillerOptions.RabbitMqUsernameEnvironmentVariable));
    }

    [Fact]
    public void Bind_populates_options_from_canonical_prefixed_paths()
    {
        var configuration = ConfigurationFromEnvironmentVariables(
            (BackFillerOptions.NameEnvironmentVariable, "canonical-name"),
            (BackFillerOptions.ServerIdEnvironmentVariable, "8"),
            (BackFillerOptions.RabbitMqUsernameEnvironmentVariable, "canonical-user"),
            (BackFillerOptions.RabbitMqPasswordEnvironmentVariable, BackFillerTestOptions.SecretPassword),
            (AcmeCloudflareOptions.CloudFlareApiKeyEnvironmentVariable, BackFillerTestOptions.SecretToken),
            (AcmeCloudflareOptions.AcmeCertificatePasswordEnvironmentVariable, BackFillerTestOptions.SecretPfx),
            (BackFillerOptions.GrabberDbEnvironmentVariable, "Server=127.0.0.1;Database=nntp;User ID=nntparticles;Password=db-secret-xyz"));

        var options = new BackFillerOptions();
        configuration.GetSection(BackFillerOptions.SectionName).Bind(options);
        var rabbit = new BackFillerRabbitMqOptions();
        configuration.GetSection("RabbitMQ").Bind(rabbit);
        options.RabbitMQ = rabbit;
        var acme = new AcmeCloudflareOptions();
        configuration.Bind(acme);
        var connectionStrings = new BackFillerConnectionStringsOptions();
        configuration.GetSection(BackFillerConnectionStringsOptions.SectionName).Bind(connectionStrings);

        Assert.Equal("canonical-name", options.Name);
        Assert.Equal(8, options.ServerId);
        Assert.Equal("canonical-user", options.RabbitMQ.Username);
        Assert.Equal(BackFillerTestOptions.SecretPassword, options.RabbitMQ.Password);
        Assert.Equal(BackFillerTestOptions.SecretToken, acme.CloudFlareApiKey);
        Assert.Equal(BackFillerTestOptions.SecretPfx, acme.AcmeCertificatePassword);
        Assert.Equal("Server=127.0.0.1;Database=nntp;User ID=nntparticles;Password=db-secret-xyz", connectionStrings.GrabberDB);
    }

    [Theory]
    [InlineData("nntpd__RabbitMQ__Username", "nntpd__RabbitMQ__Password")]
    [InlineData("backfiller__RabbitMQ__Username", "backfiller__RabbitMQ__Password")]
    [InlineData("backfiller__LetsEncrypt__CloudFlareApiToken", "backfiller__LetsEncrypt__PfxExportPassword")]
    public void Bind_does_not_accept_obsolete_application_prefixes(string usernameOrToken, string passwordOrPfx)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [usernameOrToken.Replace("__", ":", StringComparison.Ordinal)] = "short-form-user-or-token",
                [passwordOrPfx.Replace("__", ":", StringComparison.Ordinal)] = BackFillerTestOptions.SecretPassword,
            })
            .Build();

        var options = new BackFillerOptions();
        configuration.Bind(options);
        var acme = new AcmeCloudflareOptions();
        configuration.Bind(acme);

        Assert.True(string.IsNullOrWhiteSpace(options.RabbitMQ.Username));
        Assert.True(string.IsNullOrWhiteSpace(options.RabbitMQ.Password));
        Assert.True(string.IsNullOrWhiteSpace(acme.CloudFlareApiKey));
        Assert.True(string.IsNullOrWhiteSpace(acme.AcmeCertificatePassword));
    }

    [Fact]
    public void Bind_does_not_map_legacy_id_key_to_server_id()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BackFiller:Id"] = "7",
            })
            .Build();

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
            (AcmeCloudflareOptions.CloudFlareApiKeyEnvironmentVariable, BackFillerTestOptions.SecretToken),
            (AcmeCloudflareOptions.CloudFlareZoneIdEnvironmentVariable, "0123456789abcdef0123456789abcdef"),
            (AcmeCloudflareOptions.AcmeCertificatePasswordEnvironmentVariable, BackFillerTestOptions.SecretPfx),
            (BackFillerOptions.GrabberDbEnvironmentVariable, "Server=127.0.0.1;Database=nntp;User ID=nntparticles;Password=db-secret-xyz"));

        var configuration = environment.BuildHostConfiguration();
        var options = new BackFillerOptions();
        configuration.GetSection(BackFillerOptions.SectionName).Bind(options);
        var rabbit = new BackFillerRabbitMqOptions();
        configuration.GetSection("RabbitMQ").Bind(rabbit);
        options.RabbitMQ = rabbit;
        var acme = new AcmeCloudflareOptions();
        configuration.Bind(acme);
        var connectionStrings = new BackFillerConnectionStringsOptions();
        configuration.GetSection(BackFillerConnectionStringsOptions.SectionName).Bind(connectionStrings);

        Assert.Equal("env-name", options.Name);
        Assert.Equal(8, options.ServerId);
        Assert.Equal("env-user", options.RabbitMQ.Username);
        Assert.Equal(BackFillerTestOptions.SecretPassword, options.RabbitMQ.Password);
        Assert.Equal(BackFillerTestOptions.SecretToken, acme.CloudFlareApiKey);
        Assert.Equal(BackFillerTestOptions.SecretPfx, acme.AcmeCertificatePassword);
        Assert.Equal("Server=127.0.0.1;Database=nntp;User ID=nntparticles;Password=db-secret-xyz", connectionStrings.GrabberDB);
    }

    [Fact]
    public void Whitespace_name_from_the_prefixed_environment_fails_validation()
    {
        using var environment = new IsolatedEnvironment(
            (BackFillerOptions.NameEnvironmentVariable, "   "));

        var configuration = environment.BuildHostConfiguration();
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

        var configuration = environment.BuildHostConfiguration();
        var options = BackFillerTestOptions.CreateValid();
        var ex = Assert.Throws<InvalidOperationException>(
            () => configuration.GetSection(BackFillerOptions.SectionName).Bind(options));
        Assert.Contains("ServerId", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(BackFillerTestOptions.SecretPassword, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Obsolete_prefixes_do_not_bind_through_the_vector_provider()
    {
        using var environment = new IsolatedEnvironment(
            ("nntpd__RabbitMQ__Username", "obsolete-user"),
            ("backfiller__RabbitMQ__Password", BackFillerTestOptions.SecretPassword));

        var configuration = environment.BuildPrefixedConfiguration();
        var options = new BackFillerOptions();
        configuration.Bind(options);
        Assert.True(string.IsNullOrWhiteSpace(options.RabbitMQ.Username));
        Assert.True(string.IsNullOrWhiteSpace(options.RabbitMQ.Password));
    }

    [Fact]
    public void Vector_prefix_does_not_bind_application_identity()
    {
        using var environment = new IsolatedEnvironment(
            ("VECTOR__NAME", "vector-must-not-bind"),
            ("VECTOR__SERVERID", "77"));

        var configuration = environment.BuildHostConfiguration();
        var options = new BackFillerOptions();
        configuration.GetSection(BackFillerOptions.SectionName).Bind(options);
        Assert.True(string.IsNullOrWhiteSpace(options.Name));
        Assert.Null(options.ServerId);
    }

    private static IConfiguration ConfigurationFromEnvironmentVariables(params (string Name, string Value)[] variables)
    {
        var pairs = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in variables)
        {
            pairs[ToAnyConfigurationPath(name)] = value;
        }

        return new ConfigurationBuilder()
            .AddInMemoryCollection(pairs)
            .Build();
    }

    private static string ToConfigurationPath(string environmentVariable)
    {
        Assert.True(VectorEnvironment.IsCanonicalName(environmentVariable));
        return environmentVariable[VectorEnvironment.Prefix.Length..]
            .Replace("__", ":", StringComparison.Ordinal);
    }

    private static string ToApplicationConfigurationPath(string environmentVariable)
    {
        Assert.StartsWith("BACKFILLER__", environmentVariable, StringComparison.Ordinal);
        var remainder = environmentVariable["BACKFILLER__".Length..];
        return remainder switch
        {
            "NAME" => "BackFiller:Name",
            "SERVERID" => "BackFiller:ServerId",
            _ => "BackFiller:" + remainder,
        };
    }

    private static string ToAnyConfigurationPath(string environmentVariable) =>
        VectorEnvironment.IsCanonicalName(environmentVariable)
            ? ToConfigurationPath(environmentVariable)
            : ToApplicationConfigurationPath(environmentVariable);

    private static string StripVectorPrefix(string environmentVariable) =>
        ToConfigurationPath(environmentVariable);

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
                .AddVectorEnvironmentVariables()
                .Build();

        public IConfiguration BuildHostConfiguration() =>
            new ConfigurationBuilder()
                .AddEnvironmentVariables()
                .AddVectorEnvironmentVariables()
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
