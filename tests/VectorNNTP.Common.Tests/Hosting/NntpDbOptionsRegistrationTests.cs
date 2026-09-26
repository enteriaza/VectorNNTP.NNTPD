using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using VectorNNTP.Common.Hosting;
using VectorNNTP.NNTPD.Configuration;

namespace VectorNNTP.Common.Tests.Hosting;

public sealed class NntpDbOptionsRegistrationTests
{
    [Fact]
    public void Shared_options_use_the_existing_nntpdb_environment_variable()
    {
        Assert.Equal("NntpDB", NntpDbOptions.ConnectionStringName);
        Assert.Equal("NntpDb", NntpDbOptions.SectionName);
        Assert.Equal("ConnectionStrings__NntpDB", NntpDbOptions.ConnectionStringEnvironmentVariable);
    }

    [Fact]
    public void AddNntpDbOptions_copies_connection_strings_nntpdb()
    {
        const string connectionString =
            "Server=198.18.0.70;Port=3306;Database=nntpdb;User ID=nntpd;Pooling=true;MinimumPoolSize=2;MaximumPoolSize=32;ConnectionIdleTimeout=300;";
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"ConnectionStrings:{NntpDbOptions.ConnectionStringName}"] = connectionString,
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddNntpDbOptions();
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<NntpDbOptions>>().Value;
        Assert.Equal(connectionString, options.ConnectionString);
        Assert.Equal("nntpdb", new MySqlConnector.MySqlConnectionStringBuilder(options.ConnectionString).Database);
        Assert.True(new NntpDbOptionsValidator().Validate(null, options).Succeeded);
    }

    [Fact]
    public void AddNntpDbOptions_does_not_fall_back_when_the_key_is_missing()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddNntpDbOptions();
        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<OptionsValidationException>(
            () => _ = provider.GetRequiredService<IOptions<NntpDbOptions>>().Value);
        Assert.Contains("ConnectionStrings:NntpDB", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Password", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
