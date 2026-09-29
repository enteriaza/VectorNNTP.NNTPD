using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VectorNNTP.Common.Hosting;
using VectorNNTP.Common.Messaging.RabbitMq;

namespace VectorNNTP.Common.Tests.Messaging.RabbitMq;

public sealed class RabbitMqOptionsValidatorTests
{
    [Fact]
    public void Defaults_MatchSharedContract()
    {
        var options = new RabbitMqOptions();

        Assert.Equal(1024, options.WorkRequestMaxPayloadBytes);
        Assert.Equal(60, options.ChannelLeaseTimeoutSeconds);
        Assert.Equal(30, options.RpcTimeoutSeconds);
        Assert.Empty(options.Hosts!);
        Assert.Equal("/", options.VirtualHost);
        Assert.True(options.EnableSsl);
        Assert.Equal(5672, options.Port);
        Assert.NotNull(options.Management);
        Assert.Null(options.Management.BaseUrl);
    }

    [Fact]
    public void Validate_Succeeds_WithoutManagement()
    {
        var result = new RabbitMqOptionsValidator().Validate(null, CreateValidConnectivityOnly());
        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_Fails_WhenHostsMissing()
    {
        var result = new RabbitMqOptionsValidator().Validate(null, new RabbitMqOptions());
        Assert.True(result.Failed);
        Assert.Contains(result.Failures, static failure => failure.Contains("Hosts", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_Fails_WhenPortOutOfRange()
    {
        var options = CreateValidConnectivityOnly();
        options.Port = 0;
        var result = new RabbitMqOptionsValidator().Validate(null, options);
        Assert.True(result.Failed);
    }

    [Fact]
    public void Validate_Fails_WhenManagementBaseUrlHasPath()
    {
        var options = CreateValidConnectivityOnly();
        options.Management = new RabbitMqManagementOptions
        {
            BaseUrl = "http://127.0.0.1:15672/api",
            RequestTimeoutSeconds = 5,
        };
        var result = new RabbitMqOptionsValidator().Validate(null, options);
        Assert.True(result.Failed);
        Assert.Contains(result.Failures, static failure => failure.Contains("must not include a path", StringComparison.Ordinal));
    }

    [Fact]
    public void ToRuntimeOptions_ProjectsValidatedSnapshot()
    {
        var runtime = CreateValidConnectivityOnly().ToRuntimeOptions();
        Assert.Equal(["127.0.0.1"], runtime.Hosts);
        Assert.Equal(5672, runtime.Port);
        Assert.Equal("/", runtime.VirtualHost);
        Assert.False(runtime.EnableSsl);
    }

    [Fact]
    public void GetDefaultConnectionName_UsesApplicationPrefix()
    {
        Assert.Equal(
            "VectorNNTP.StorageServer:cache01.usenet.ninja",
            RabbitMqRuntimeOptions.GetDefaultConnectionName("VectorNNTP.StorageServer", "cache01.usenet.ninja"));
    }

    internal static RabbitMqOptions CreateValidConnectivityOnly() =>
        new()
        {
            Hosts = ["127.0.0.1"],
            EnableSsl = false,
        };
}

public sealed class RabbitMqInfrastructureRegistrationTests
{
    [Fact]
    public async Task AddRabbitMqInfrastructure_RegistersSingletons_WithoutConnecting()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Options.Create(RabbitMqOptionsValidatorTests.CreateValidConnectivityOnly()));
        services.AddSingleton<IRabbitMqConnectionNameProvider>(
            new DelegateRabbitMqConnectionNameProvider(() => "VectorNNTP.Common.Tests:unit"));
        var factory = new CountingFakeRabbitMqConnectionFactory();
        services.AddSingleton<IRabbitMqConnectionFactory>(factory);
        services.AddRabbitMqInfrastructure();

        await using var provider = services.BuildServiceProvider();
        var service = provider.GetRequiredService<IRabbitMqService>();
        Assert.Same(provider.GetRequiredService<RabbitMqService>(), service);
        Assert.Equal(0, factory.ConnectCount);
    }

    private sealed class CountingFakeRabbitMqConnectionFactory : IRabbitMqConnectionFactory
    {
        public int ConnectCount { get; private set; }

        public Task<IRabbitMqConnection> ConnectAsync(
            RabbitMqOptions options,
            string connectionName,
            CancellationToken cancellationToken)
        {
            ConnectCount++;
            throw new InvalidOperationException("Tests must not connect during registration.");
        }
    }
}
