using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using VectorNNTP.NNTPD.Configuration;
using VectorNNTP.NNTPD.Transit;

namespace VectorNNTP.NNTPD.Tests.Transit;

public sealed class TransitPeersOptionsTests
{
    [Fact]
    public void Bind_ExactTopLevelDictionaryShape_MultiplePeersAndDefaults()
    {
        const string json = """
            {
              "Transit": {
                "news-example": {
                  "MaxIncomingConnections": 10,
                  "MaxOutgoingConnections": 2,
                  "AllowFrom": [ "news.example.net", "192.0.2.0/24", "2001:db8:1234::/48" ],
                  "ConnectTo": [ "news.example.net:563" ],
                  "Username": "",
                  "Password": "",
                  "Ssl": "TLS",
                  "Patterns": "*",
                  "DeferOnDuplicate": true,
                  "PathToken": "peer.example",
                  "MaxSize": 10485760,
                  "MessageTypes": [ "default" ]
                },
                "second": {
                  "MaxIncomingConnections": 1,
                  "MaxOutgoingConnections": 0,
                  "AllowFrom": [ "198.51.100.10" ]
                }
              }
            }
            """;

        var options = Bind(json);
        Assert.Equal(2, options.Count);
        Assert.True(options.ContainsKey("news-example"));
        Assert.True(options.ContainsKey("second"));
        var first = options["news-example"];
        Assert.Equal(10, first.MaxIncomingConnections);
        Assert.Equal(2, first.MaxOutgoingConnections);
        Assert.Equal(["news.example.net", "192.0.2.0/24", "2001:db8:1234::/48"], first.AllowFrom);
        Assert.Equal(["news.example.net:563"], first.ConnectTo);
        Assert.Equal("TLS", first.Ssl);
        Assert.Equal("*", first.Patterns);
        Assert.True(first.DeferOnDuplicate);
        Assert.Equal(string.Empty, first.Username);
        Assert.Equal(string.Empty, first.Password);
        Assert.Equal("peer.example", first.PathToken);
        Assert.Equal(10_485_760, first.MaxSize);
        Assert.Equal(["default"], first.MessageTypes);
        Assert.Equal("*", options["second"].Patterns);
        Assert.True(options["second"].DeferOnDuplicate);
        Assert.Equal([], options["second"].ConnectTo);
        Assert.Equal(string.Empty, options["second"].PathToken);
        Assert.Equal(TransitPeerOptions.DefaultMaxSize, options["second"].MaxSize);
        Assert.Empty(options["second"].MessageTypes);
        Assert.True(TransitMessageTypesParser.TryParse(options["second"].MessageTypes, out var secondTypes, out _));
        Assert.Equal(TransitMessageTypes.Default, secondTypes);
    }

    [Fact]
    public void Bind_EmptyTransit_IsValid()
    {
        var options = Bind("""{ "Transit": { } }""");
        Assert.Empty(options);
        var result = new TransitPeersOptionsValidator().Validate(null, options);
        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData("Giganews, Inc.")]
    [InlineData("Blueworld Hosting")]
    [InlineData("news-example")]
    [InlineData("peer/with slash")]
    [InlineData("-leading")]
    [InlineData("Café — ニュース通信社")]
    public void Validate_AcceptsHumanReadablePeerNames(string name)
    {
        var options = new TransitPeersOptions { [name] = TransitTestPeers.Peer() };
        var result = new TransitPeersOptionsValidator().Validate(null, options);
        Assert.True(result.Succeeded);
        Assert.True(options.ContainsKey(name));
    }

    [Fact]
    public void Validate_RejectsWhitespaceOnlyPeerName()
    {
        var options = new TransitPeersOptions { ["   "] = TransitTestPeers.Peer() };
        var result = new TransitPeersOptionsValidator().Validate(null, options);
        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, static f => f.Contains("peer name", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_RejectsControlCharacterPeerName()
    {
        var options = new TransitPeersOptions { ["peer\nname"] = TransitTestPeers.Peer() };
        var result = new TransitPeersOptionsValidator().Validate(null, options);
        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, static f => f.Contains("control character", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_RejectsOverlongPeerName()
    {
        var name = new string('a', TransitPeersOptionsValidator.MaxPeerNameLength + 1);
        var options = new TransitPeersOptions { [name] = TransitTestPeers.Peer() };
        var result = new TransitPeersOptionsValidator().Validate(null, options);
        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, static f => f.Contains("maximum length", StringComparison.Ordinal));
    }

    [Fact]
    public void Bind_PreservesExactHumanReadablePeerNames()
    {
        const string json = """
            {
              "Transit": {
                "Giganews, Inc.": {
                  "MaxIncomingConnections": 50,
                  "MaxOutgoingConnections": 50,
                  "PathToken": "giganews.example"
                },
                "Blueworld Hosting": {
                  "MaxIncomingConnections": 10,
                  "MaxOutgoingConnections": 10,
                  "PathToken": "blueworld.example"
                }
              }
            }
            """;

        var options = Bind(json);
        Assert.True(new TransitPeersOptionsValidator().Validate(null, options).Succeeded);
        Assert.True(options.ContainsKey("Giganews, Inc."));
        Assert.True(options.ContainsKey("Blueworld Hosting"));
        Assert.Equal("giganews.example", options["Giganews, Inc."].PathToken);
        Assert.Equal("blueworld.example", options["Blueworld Hosting"].PathToken);
    }

    [Fact]
    public void Validate_PathToken_PreservesExactString_AndRejectsControlChars()
    {
        var preserved = TransitTestPeers.Dictionary("alpha", TransitTestPeers.Peer(pathToken: "Peer.Example"));
        Assert.True(new TransitPeersOptionsValidator().Validate(null, preserved).Succeeded);
        var snapshot = TransitConfigurationSnapshot.Create(preserved);
        Assert.Equal("Peer.Example", snapshot.Peers["alpha"].PathToken);

        var empty = TransitTestPeers.Dictionary("alpha", TransitTestPeers.Peer(pathToken: ""));
        Assert.True(new TransitPeersOptionsValidator().Validate(null, empty).Succeeded);

        var control = TransitTestPeers.Dictionary("alpha", TransitTestPeers.Peer(pathToken: "peer\tname"));
        var result = new TransitPeersOptionsValidator().Validate(null, control);
        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, static f => f.Contains("PathToken", StringComparison.Ordinal));
        Assert.Contains(result.Failures!, static f => f.Contains("alpha", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData((long)int.MaxValue + 1)]
    public void Validate_RejectsInvalidMaxSize(long maxSize)
    {
        var options = TransitTestPeers.Dictionary("alpha", TransitTestPeers.Peer(maxSize: maxSize));
        var result = new TransitPeersOptionsValidator().Validate(null, options);
        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, static f => f.Contains("MaxSize", StringComparison.Ordinal));
        Assert.Contains(result.Failures!, static f => f.Contains("alpha", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_AcceptsMaxSizeBounds()
    {
        var min = TransitTestPeers.Dictionary("alpha", TransitTestPeers.Peer(maxSize: 1));
        var max = TransitTestPeers.Dictionary("alpha", TransitTestPeers.Peer(maxSize: TransitPeerOptions.MaxMaxSize));
        Assert.True(new TransitPeersOptionsValidator().Validate(null, min).Succeeded);
        Assert.True(new TransitPeersOptionsValidator().Validate(null, max).Succeeded);
    }

    [Fact]
    public void Validate_RejectsUnknownMessageTypes()
    {
        var options = TransitTestPeers.Dictionary("alpha", TransitTestPeers.Peer(messageTypes: ["default", "not-a-type"]));
        var result = new TransitPeersOptionsValidator().Validate(null, options);
        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, static f => f.Contains("MessageTypes", StringComparison.Ordinal));
        Assert.Contains(result.Failures!, static f => f.Contains("alpha", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_RejectsMissingConnectionLimits()
    {
        var options = TransitTestPeers.Dictionary("alpha", new TransitPeerOptions { AllowFrom = ["192.0.2.1"] });
        var result = new TransitPeersOptionsValidator().Validate(null, options);
        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, static f => f.Contains("MaxIncomingConnections", StringComparison.Ordinal));
        Assert.Contains(result.Failures!, static f => f.Contains("MaxOutgoingConnections", StringComparison.Ordinal));
        Assert.Contains(result.Failures!, static f => f.Contains("alpha", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4097)]
    public void Validate_RejectsOutOfRangeLimits(int value)
    {
        var options = TransitTestPeers.Dictionary("alpha", TransitTestPeers.Peer(maxIncoming: value, maxOutgoing: value));
        var result = new TransitPeersOptionsValidator().Validate(null, options);
        Assert.True(result.Failed);
    }

    [Theory]
    [InlineData("ssl")]
    [InlineData("plain")]
    [InlineData("tls-start")]
    public void Validate_RejectsInvalidSsl(string ssl)
    {
        var options = TransitTestPeers.Dictionary("alpha", TransitTestPeers.Peer(ssl: ssl));
        var result = new TransitPeersOptionsValidator().Validate(null, options);
        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, static f => f.Contains("Ssl", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("")]
    [InlineData("TLS")]
    [InlineData("tls")]
    [InlineData("STARTTLS")]
    [InlineData("starttls")]
    public void Validate_AcceptsCanonicalSslValues(string ssl)
    {
        var options = TransitTestPeers.Dictionary("alpha", TransitTestPeers.Peer(ssl: ssl));
        Assert.True(new TransitPeersOptionsValidator().Validate(null, options).Succeeded);
        Assert.True(TransitSslParser.TryParse(ssl, out _, out _));
    }

    [Theory]
    [InlineData("news.example.net")]
    [InlineData("192.0.2.10")]
    [InlineData("2001:db8::10")]
    [InlineData("[2001:db8::10]")]
    [InlineData("2001:db8::10:119")]
    public void Validate_RejectsInvalidConnectTo(string endpoint)
    {
        var options = TransitTestPeers.Dictionary("alpha", TransitTestPeers.Peer(connectTo: [endpoint]));
        var result = new TransitPeersOptionsValidator().Validate(null, options);
        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, static f => f.Contains("ConnectTo", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("news.example.net:119")]
    [InlineData("news.example.net:563")]
    [InlineData("192.0.2.10:119")]
    [InlineData("[2001:db8::10]:119")]
    [InlineData("[2001:db8::10]:563")]
    public void Validate_AcceptsExplicitConnectToPorts(string endpoint)
    {
        var options = TransitTestPeers.Dictionary("alpha", TransitTestPeers.Peer(connectTo: [endpoint]));
        Assert.True(new TransitPeersOptionsValidator().Validate(null, options).Succeeded);
        Assert.True(TransitConnectEndpoint.TryParse(endpoint, out var parsed, out var error), error);
        Assert.InRange(parsed!.Port, 1, 65535);
    }

    [Fact]
    public void Validate_RejectsInvalidAllowFromAndPatterns_WithoutLeakingPassword()
    {
        var options = TransitTestPeers.Dictionary(
            "alpha",
            TransitTestPeers.Peer(
                allowFrom: ["not a host!"],
                username: "peer",
                password: "peer-secret",
                patterns: "comp.[abc"));
        var result = new TransitPeersOptionsValidator().Validate(null, options);
        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, static f => f.Contains("AllowFrom", StringComparison.Ordinal));
        Assert.Contains(result.Failures!, static f => f.Contains("Patterns", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Failures!, static f => f.Contains("peer-secret", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_RejectsPartialCredentials_AllowsBothBlankOrBothSet()
    {
        var validator = new TransitPeersOptionsValidator();

        var userOnly = TransitTestPeers.Dictionary("alpha", TransitTestPeers.Peer(username: "peer", password: ""));
        var userOnlyResult = validator.Validate(null, userOnly);
        Assert.True(userOnlyResult.Failed);
        Assert.Contains(userOnlyResult.Failures!, static f => f.Contains("Password", StringComparison.Ordinal));
        Assert.Contains(userOnlyResult.Failures!, static f => f.Contains("both be set or both be blank", StringComparison.Ordinal));

        var passwordOnly = TransitTestPeers.Dictionary("alpha", TransitTestPeers.Peer(username: "", password: "peer-secret"));
        var passwordOnlyResult = validator.Validate(null, passwordOnly);
        Assert.True(passwordOnlyResult.Failed);
        Assert.Contains(passwordOnlyResult.Failures!, static f => f.Contains("Username", StringComparison.Ordinal));
        Assert.DoesNotContain(passwordOnlyResult.Failures!, static f => f.Contains("peer-secret", StringComparison.Ordinal));

        var bothBlank = TransitTestPeers.Dictionary("alpha", TransitTestPeers.Peer(username: "", password: ""));
        Assert.True(validator.Validate(null, bothBlank).Succeeded);

        var bothSet = TransitTestPeers.Dictionary("alpha", TransitTestPeers.Peer(username: "peer", password: "peer-secret"));
        Assert.True(validator.Validate(null, bothSet).Succeeded);
        Assert.True(TransitConfigurationSnapshot.Create(bothSet).Peers["alpha"].HasPeerCredentials);
    }

    [Fact]
    public async Task ProductionAppsettings_BindsAndValidatesNamedTransitPeers()
    {
        var path = FindProductionAppsettings();
        var config = new ConfigurationBuilder().AddJsonFile(path, optional: false).Build();
        var options = new TransitPeersOptions();
        config.GetSection("Transit").Bind(options);

        Assert.True(options.ContainsKey("Giganews, Inc."));
        Assert.True(options.ContainsKey("Blueworld Hosting"));
        Assert.False(options.ContainsKey("giganews-inc"));
        Assert.False(options.ContainsKey("blueworld-hosting"));

        var result = new TransitPeersOptionsValidator().Validate(null, options);
        Assert.True(result.Succeeded, string.Join("; ", result.Failures ?? []));

        var snapshot = TransitConfigurationSnapshot.Create(options);
        Assert.Equal("nntp.giganews.com", snapshot.Peers["Giganews, Inc."].PathToken);
        Assert.Equal("usenet.blueworldhosting.com", snapshot.Peers["Blueworld Hosting"].PathToken);
        Assert.Equal(5_242_880, snapshot.Peers["Giganews, Inc."].MaxSize);
        Assert.Equal(1_048_576, snapshot.Peers["Blueworld Hosting"].MaxSize);
        Assert.Equal(TransitMessageTypes.All, snapshot.Peers["Giganews, Inc."].MessageTypes);
        Assert.Equal(TransitMessageTypes.All, snapshot.Peers["Blueworld Hosting"].MessageTypes);
        Assert.False(snapshot.Peers["Giganews, Inc."].HasPeerCredentials);
        Assert.False(snapshot.Peers["Blueworld Hosting"].HasPeerCredentials);

        var builder = Host.CreateEmptyApplicationBuilder(
            new HostApplicationBuilderSettings
            {
                ContentRootPath = Path.GetDirectoryName(path),
            });
        builder.Configuration.AddJsonFile(path, optional: false);
        builder.Services.AddOptions<TransitPeersOptions>()
            .Bind(builder.Configuration.GetSection("Transit"))
            .ValidateOnStart();
        builder.Services.AddSingleton<IValidateOptions<TransitPeersOptions>, TransitPeersOptionsValidator>();
        using var host = builder.Build();
        await host.StartAsync();
        try
        {
            var bound = host.Services.GetRequiredService<IOptions<TransitPeersOptions>>().Value;
            Assert.True(bound.ContainsKey("Giganews, Inc."));
            Assert.True(bound.ContainsKey("Blueworld Hosting"));
        }
        finally
        {
            await host.StopAsync();
        }
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

    [Fact]
    public void Validate_RejectsOverlappingPrefixes()
    {
        var options = new TransitPeersOptions
        {
            ["one"] = TransitTestPeers.Peer(allowFrom: ["192.0.2.0/24"]),
            ["two"] = TransitTestPeers.Peer(allowFrom: ["192.0.2.10"]),
        };
        var result = new TransitPeersOptionsValidator().Validate(null, options);
        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, static f => f.Contains("overlapping", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_RejectsSharedHostname()
    {
        var options = new TransitPeersOptions
        {
            ["one"] = TransitTestPeers.Peer(allowFrom: ["news.example.net"]),
            ["two"] = TransitTestPeers.Peer(allowFrom: ["news.example.net"]),
        };
        var result = new TransitPeersOptionsValidator().Validate(null, options);
        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, static f => f.Contains("news.example.net", StringComparison.Ordinal));
    }

    [Fact]
    public void ServiceBind_DoesNotRequireNestedPeersSection()
    {
        var json = """{ "Transit": { "alpha": { "MaxIncomingConnections": 1, "MaxOutgoingConnections": 0, "AllowFrom": [ "192.0.2.1" ] } } }""";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var config = new ConfigurationBuilder().AddJsonStream(stream).Build();
        var services = new ServiceCollection();
        services.AddOptions<TransitPeersOptions>().Bind(config.GetSection("Transit"));
        using var provider = services.BuildServiceProvider();
        var bound = provider.GetRequiredService<IOptions<TransitPeersOptions>>().Value;
        Assert.True(bound.ContainsKey("alpha"));
        Assert.False(bound.ContainsKey("Peers"));
    }

    private static TransitPeersOptions Bind(string json)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var config = new ConfigurationBuilder().AddJsonStream(stream).Build();
        var options = new TransitPeersOptions();
        config.GetSection("Transit").Bind(options);
        return options;
    }
}
