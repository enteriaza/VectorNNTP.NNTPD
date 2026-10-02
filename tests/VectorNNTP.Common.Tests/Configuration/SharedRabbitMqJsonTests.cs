using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using VectorNNTP.Common.Messaging.RabbitMq;
using VectorNNTP.NNTPD.Configuration;

namespace VectorNNTP.Common.Tests.Configuration;

public sealed class SharedRabbitMqJsonTests
{
    [Theory]
    [InlineData("VectorNNTP.NNTPD")]
    [InlineData("VectorNNTP.StorageServer")]
    [InlineData("VectorNNTP.BackFiller")]
    public void Application_output_loads_shared_RabbitMq_json_and_primary_file_has_no_section(string applicationName)
    {
        var output = FindApplicationOutput(applicationName);
        var primaryPath = Path.Combine(output, applicationName + ".json");
        var sharedPath = Path.Combine(output, ApplicationJsonConfiguration.SharedRabbitMqFileName);
        Assert.True(File.Exists(primaryPath), primaryPath);
        Assert.True(File.Exists(sharedPath), sharedPath);

        using (var primary = JsonDocument.Parse(File.ReadAllText(primaryPath)))
        {
            Assert.False(primary.RootElement.TryGetProperty(RabbitMqOptions.SectionName, out _));
        }

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = [],
            ContentRootPath = output,
            EnvironmentName = Environments.Production,
        });
        ApplicationJsonConfiguration.UseEntryAssemblyJsonFiles(
            builder.Configuration,
            builder.Environment.EnvironmentName);
        ApplicationJsonConfiguration.AddSharedRabbitMqJsonFile(
            builder.Configuration,
            builder.Environment.EnvironmentName);

        var rabbitIndex = IndexOf(builder.Configuration, ApplicationJsonConfiguration.SharedRabbitMqFileName);
        var rabbit = (FileConfigurationSource)builder.Configuration.Sources[rabbitIndex];
        Assert.True(rabbit.Optional);
        Assert.True(rabbit.ReloadOnChange);
        var environmentIndex = IndexOf(builder.Configuration, ApplicationJsonConfiguration.EnvironmentJsonFileName("Production"));
        Assert.True(rabbitIndex > environmentIndex);
        Assert.True(rabbitIndex < IndexOfEnvironmentVariablesAfter(builder.Configuration, rabbitIndex));

        for (var index = builder.Configuration.Sources.Count - 1; index >= 0; index--)
        {
            var name = builder.Configuration.Sources[index].GetType().Name;
            if (name is "EnvironmentVariablesConfigurationSource" or "CommandLineConfigurationSource")
            {
                builder.Configuration.Sources.RemoveAt(index);
            }
        }

        var options = new RabbitMqOptions();
        builder.Configuration.GetSection(RabbitMqOptions.SectionName).Bind(options);
        AssertSharedValues(options);

        var validation = new RabbitMqOptionsValidator().Validate(null, options);
        Assert.True(validation.Succeeded, string.Join("; ", validation.Failures ?? []));
    }

    [Fact]
    public void Environment_variable_and_command_line_override_RabbitMq_json()
    {
        var root = Directory.CreateTempSubdirectory("vnntp-rabbit-json-");
        const string overriddenPort = "5999";
        var previous = Environment.GetEnvironmentVariable("RabbitMQ__Port");
        try
        {
            File.WriteAllText(
                Path.Combine(root.FullName, ApplicationJsonConfiguration.SharedRabbitMqFileName),
                """{"RabbitMQ":{"Port":5672,"Hosts":["198.18.0.3"]}}""");
            File.WriteAllText(
                Path.Combine(root.FullName, ApplicationJsonConfiguration.PrimaryJsonFileName()),
                """{"RabbitMQ":{"Port":1}}""");

            Environment.SetEnvironmentVariable("RabbitMQ__Port", overriddenPort);
            var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
            {
                Args = ["--RabbitMQ:VirtualHost=/from-args"],
                ContentRootPath = root.FullName,
                EnvironmentName = Environments.Production,
            });
            ApplicationJsonConfiguration.UseEntryAssemblyJsonFiles(
                builder.Configuration,
                builder.Environment.EnvironmentName);
            ApplicationJsonConfiguration.AddSharedRabbitMqJsonFile(
                builder.Configuration,
                builder.Environment.EnvironmentName);

            var rabbitIndex = IndexOf(builder.Configuration, ApplicationJsonConfiguration.SharedRabbitMqFileName);
            Assert.True(rabbitIndex < IndexOfCommandLineAfter(builder.Configuration, rabbitIndex));
            Assert.Equal(overriddenPort, builder.Configuration["RabbitMQ:Port"]);
            Assert.Equal("/from-args", builder.Configuration["RabbitMQ:VirtualHost"]);
            Assert.Equal("198.18.0.3", builder.Configuration["RabbitMQ:Hosts:0"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("RabbitMQ__Port", previous);
            try
            {
                root.Delete(recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static void AssertSharedValues(RabbitMqOptions options)
    {
        Assert.NotNull(options.Hosts);
        Assert.Equal(["198.18.0.3"], options.Hosts);
        Assert.Equal(5672, options.Port);
        Assert.Equal(512, options.ChannelPoolSize);
        Assert.Equal(1024, options.WorkRequestMaxPayloadBytes);
        Assert.Equal("http://198.18.0.3:15672", options.Management?.BaseUrl);
        Assert.Equal(5, options.Management?.RequestTimeoutSeconds);
        Assert.Equal("/", options.VirtualHost);
        Assert.False(options.EnableSsl);
    }

    private static int IndexOf(IConfigurationBuilder configuration, string path)
    {
        for (var index = 0; index < configuration.Sources.Count; index++)
        {
            if (configuration.Sources[index] is FileConfigurationSource file
                && string.Equals(file.Path, path, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        throw new InvalidOperationException("Configuration source was not found: " + path);
    }

    private static int IndexOfCommandLineAfter(IConfigurationBuilder configuration, int start)
    {
        for (var index = start + 1; index < configuration.Sources.Count; index++)
        {
            if (configuration.Sources[index].GetType().Name == "CommandLineConfigurationSource")
            {
                return index;
            }
        }

        throw new InvalidOperationException(
            "Command-line configuration source was not found. Sources: "
            + string.Join(", ", configuration.Sources.Select(static source => source.GetType().Name)));
    }

    private static int IndexOfEnvironmentVariablesAfter(IConfigurationBuilder configuration, int start)
    {
        for (var index = start + 1; index < configuration.Sources.Count; index++)
        {
            if (configuration.Sources[index].GetType().Name == "EnvironmentVariablesConfigurationSource")
            {
                return index;
            }
        }

        throw new InvalidOperationException("Environment variable configuration source was not found.");
    }

    private static string FindApplicationOutput(string applicationName)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            foreach (var configuration in new[] { "Release", "Debug" })
            {
                var candidate = Path.Combine(
                    dir.FullName,
                    "src",
                    applicationName,
                    "bin",
                    configuration,
                    "net10.0");
                if (File.Exists(Path.Combine(candidate, ApplicationJsonConfiguration.SharedRabbitMqFileName)))
                {
                    return candidate;
                }
            }
        }

        throw new InvalidOperationException("Could not locate " + applicationName + " output RabbitMq.json.");
    }
}
