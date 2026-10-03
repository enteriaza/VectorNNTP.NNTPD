using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using VectorNNTP.Common.Configuration;

namespace VectorNNTP.Common.Tests.Configuration
{
    public sealed class ApplicationJsonConfigurationTests
    {
        [Fact]
        public void File_names_are_derived_from_the_entry_assembly_short_name()
        {
            var name = Assembly.GetEntryAssembly()?.GetName()?.Name;
            Assert.False(string.IsNullOrWhiteSpace(name));
            Assert.Equal(name + ".json", ApplicationJsonConfiguration.PrimaryJsonFileName());
            Assert.Equal(name + ".Development.json", ApplicationJsonConfiguration.EnvironmentJsonFileName("Development"));
            Assert.DoesNotContain(".exe", ApplicationJsonConfiguration.PrimaryJsonFileName(), StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Host_loads_entry_assembly_json_and_does_not_fall_back_to_appsettings()
        {
            var root = Directory.CreateTempSubdirectory("vnntp-host-json-");
            try
            {
                var name = Assembly.GetEntryAssembly()!.GetName().Name!;
                Write(root.FullName, "appsettings.json", "legacy");
                Write(root.FullName, name + ".json", "primary", "base");
                Write(root.FullName, name + ".Development.json", shared: "development");
                Write(root.FullName, name + ".settings.json", "settings");
                Write(root.FullName, name + ".settings.Development.json", shared: "settings-development");

                var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
                {
                    ContentRootPath = root.FullName,
                    EnvironmentName = Environments.Development,
                });
                ApplicationJsonConfiguration.UseEntryAssemblyJsonFiles(
                    builder.Configuration,
                    builder.Environment.EnvironmentName);

                Assert.Equal("primary", builder.Configuration["VectorNntpConfigProbe"]);
                Assert.Equal("development", builder.Configuration["VectorNntpConfigShared"]);
                Assert.DoesNotContain(
                    builder.Configuration.Sources.OfType<FileConfigurationSource>(),
                    source => source.Path is "appsettings.json"
                        or "appsettings.Development.json"
                        || (source.Path?.Contains(".settings.", StringComparison.OrdinalIgnoreCase) ?? false));
            }
            finally
            {
                TryDelete(root.FullName);
            }
        }

        [Fact]
        public void Missing_entry_assembly_json_stays_optional_and_ignores_a_leftover_appsettings_file()
        {
            var root = Directory.CreateTempSubdirectory("vnntp-host-json-missing-");
            try
            {
                Write(root.FullName, "appsettings.json", "legacy");
                var builder = new ConfigurationBuilder()
                    .SetBasePath(root.FullName)
                    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
                    .AddJsonFile("appsettings.Development.json", optional: true, reloadOnChange: false);
                ApplicationJsonConfiguration.UseEntryAssemblyJsonFiles(builder, "Development");

                var configuration = builder.Build();
                Assert.Null(configuration["VectorNntpConfigProbe"]);
            }
            finally
            {
                TryDelete(root.FullName);
            }
        }

        private static void Write(string directory, string fileName, string? marker = null, string? shared = null)
        {
            var markerLine = marker is null ? string.Empty : $""" "VectorNntpConfigProbe": "{marker}" """;
            var sharedLine = shared is null ? string.Empty : $""" "VectorNntpConfigShared": "{shared}" """;
            var body = marker is null
                ? $$"""{ {{sharedLine}} }"""
                : shared is null
                    ? $$"""{ {{markerLine}} }"""
                    : $$"""{ {{markerLine}}, {{sharedLine}} }""";
            File.WriteAllText(Path.Combine(directory, fileName), body);
        }

        private static void TryDelete(string path)
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
