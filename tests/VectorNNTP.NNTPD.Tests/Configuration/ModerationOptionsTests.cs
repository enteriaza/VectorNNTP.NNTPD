using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using VectorNNTP.NNTPD.Configuration;
using VectorNNTP.NNTPD.Moderation;
using VectorNNTP.NNTPD.Newsgroups;

namespace VectorNNTP.NNTPD.Tests.Configuration;

public sealed class ModerationOptionsTests
{
    [Fact]
    public void Bind_EmptySection_IsValid()
    {
        var options = Bind(
            """
            {
              "Moderation": {
                "Moderators": []
              }
            }
            """);

        Assert.Empty(options.Moderators);
        Assert.True(new ModerationOptionsValidator().Validate(null, options).Succeeded);
    }

    [Fact]
    public void Bind_OmittedSection_IsValid()
    {
        var options = Bind("""{ "Nntpd": { "ServerId": 1 } }""");
        Assert.Empty(options.Moderators);
        Assert.True(new ModerationOptionsValidator().Validate(null, options).Succeeded);
    }

    [Fact]
    public void Bind_ModeratorMapping_Succeeds()
    {
        var options = Bind(
            """
            {
              "Moderation": {
                "Moderators": [
                  {
                    "Pattern": "comp.example.*",
                    "Address": "moderator@example.com",
                    "Username": "moderator-example"
                  }
                ]
              }
            }
            """);

        var mapping = Assert.Single(options.Moderators);
        Assert.Equal("comp.example.*", mapping.Pattern);
        Assert.Equal("moderator@example.com", mapping.Address);
        Assert.Equal("moderator-example", mapping.Username);
        Assert.True(new ModerationOptionsValidator().Validate(null, options).Succeeded);
    }

    [Fact]
    public void Bind_ProductionAppsettings_ImportsInnRoutingCatalogue()
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(FindProductionAppsettings(), optional: false)
            .Build();
        var options = new ModerationOptions();
        configuration.GetSection(ModerationOptions.SectionName).Bind(options);
        Assert.True(new ModerationOptionsValidator().Validate(null, options).Succeeded);
        Assert.Equal("https://raw.githubusercontent.com/InterNetNews/inn/main/samples/moderators", options.Source.InnUrl);
        Assert.Equal("InnUrl", options.Source.RetrievedFrom);
        Assert.Equal("https://github.com/InterNetNews/inn/blob/main/samples/moderators", options.Source.Url);
        Assert.Equal(8, options.Moderators.Length);
        Assert.Equal(
            [
                ("fido7.*", "%s@fido7.org"),
                ("fj.*", "%s@moderators.fj-news.org"),
                ("medlux.*", "%s@news.medlux.ru"),
                ("nl.*", "%s@nl.news-admin.org"),
                ("perl.*", "news-moderator-%s@perl.org"),
                ("relcom.*", "%s@moderators.relcom.ru"),
                ("si.*", "%s@arnes.si"),
                ("*", "%s@moderators.isc.org"),
            ],
            options.Moderators.Select(static m => (m.Pattern, m.Address)));
        Assert.Equal("*", options.Moderators[^1].Pattern);
        Assert.All(options.Moderators, static mapping =>
        {
            Assert.True(string.IsNullOrEmpty(mapping.Username));
            Assert.True(NntpWildmat.TryValidate(Encoding.ASCII.GetBytes(mapping.Pattern)));
            Assert.True(ModeratorAddressTemplate.IsTemplate(mapping.Address));
            Assert.True(ModeratorAddressTemplate.TryValidate(mapping.Address));
        });

        var control = new ControlOptions();
        configuration.GetSection(ControlOptions.SectionName).Bind(control);
        Assert.NotEmpty(control.PgpAuthorities.Authorities);
        Assert.Equal("AIOE", control.PgpAuthorities.Authorities[0].Name);
    }

    [Fact]
    public void Bind_RoutingOnlyEntry_UsernameMayBeOmitted()
    {
        var options = Bind(
            """
            {
              "Moderation": {
                "Moderators": [
                  {
                    "Pattern": "fido7.*",
                    "Address": "%s@fido7.org"
                  }
                ]
              }
            }
            """);

        var mapping = Assert.Single(options.Moderators);
        Assert.Equal("fido7.*", mapping.Pattern);
        Assert.Equal("%s@fido7.org", mapping.Address);
        Assert.Equal(string.Empty, mapping.Username);
        Assert.True(new ModerationOptionsValidator().Validate(null, options).Succeeded);
    }

    [Theory]
    [InlineData("", "moderator@example.com", "moderator-example")]
    [InlineData("comp.example.*", "", "moderator-example")]
    [InlineData("   ", "moderator@example.com", "moderator-example")]
    public void Validate_RejectsEmptyPatternOrAddress(string pattern, string address, string username)
    {
        var result = Validate(new ModeratorMappingOptions
        {
            Pattern = pattern,
            Address = address,
            Username = username,
        });
        Assert.True(result.Failed);
    }

    [Theory]
    [InlineData("%q@fido7.org")]
    [InlineData("%s%s@fido7.org")]
    [InlineData("not-a-mailbox")]
    public void Validate_RejectsMalformedAddressTemplate(string address)
    {
        var result = Validate(new ModeratorMappingOptions
        {
            Pattern = "fido7.*",
            Address = address,
        });
        Assert.True(result.Failed);
    }

    [Theory]
    [InlineData("comp[example]")]
    [InlineData("comp\\example")]
    [InlineData("")]
    public void Validate_RejectsMalformedWildmat(string pattern)
    {
        var result = Validate(new ModeratorMappingOptions
        {
            Pattern = pattern,
            Address = "moderator@example.com",
            Username = "moderator-example",
        });
        Assert.True(result.Failed);
    }

    [Fact]
    public void Validate_RejectsNonMailboxAddress()
    {
        var result = Validate(new ModeratorMappingOptions
        {
            Pattern = "comp.example.*",
            Address = "not-a-mailbox",
            Username = "moderator-example",
        });
        Assert.True(result.Failed);
    }

    [Fact]
    public void Validate_RejectsDuplicatePatterns()
    {
        var options = new ModerationOptions
        {
            Moderators =
            [
                new() { Pattern = "comp.example.*", Address = "a@example.com", Username = "mod-a" },
                new() { Pattern = "COMP.EXAMPLE.*", Address = "b@example.com", Username = "mod-b" },
            ],
        };

        var result = new ModerationOptionsValidator().Validate(null, options);
        Assert.True(result.Failed);
        Assert.Contains("duplicates", result.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_AllowsSameUsernameOnDistinctPatterns()
    {
        var options = new ModerationOptions
        {
            Moderators =
            [
                new() { Pattern = "group.a", Address = "a@example.com", Username = "mod-shared" },
                new() { Pattern = "group.b", Address = "b@example.com", Username = "mod-shared" },
            ],
        };

        Assert.True(new ModerationOptionsValidator().Validate(null, options).Succeeded);
    }

    [Fact]
    public void Options_InvalidMapping_FailsOnStart()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(
                """
                {
                  "Moderation": {
                    "Moderators": [
                      { "Pattern": "comp[bad]", "Address": "mod@example.com", "Username": "mod" }
                    ]
                  }
                }
                """)))
            .Build();
        services.AddSingleton<IConfiguration>(configuration);
        services
            .AddOptions<ModerationOptions>()
            .BindConfiguration(ModerationOptions.SectionName)
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<ModerationOptions>, ModerationOptionsValidator>();

        using var provider = services.BuildServiceProvider();
        var resolve = () => provider.GetRequiredService<IOptions<ModerationOptions>>().Value;
        Assert.Throws<OptionsValidationException>(resolve);
    }

    private static ValidateOptionsResult Validate(ModeratorMappingOptions mapping) =>
        new ModerationOptionsValidator().Validate(
            null,
            new ModerationOptions { Moderators = [mapping] });

    private static ModerationOptions Bind(string json)
    {
        var options = new ModerationOptions();
        new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)))
            .Build()
            .GetSection(ModerationOptions.SectionName)
            .Bind(options);
        options.Moderators ??= [];
        return options;
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
}
