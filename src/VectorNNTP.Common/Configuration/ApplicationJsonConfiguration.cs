using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;

namespace VectorNNTP.Common.Configuration
{
    /// <summary>
    /// Names the primary JSON configuration file from the entry assembly short name.
    /// </summary>
    /// <remarks>
    /// The file is <c>{assembly name}.json</c>. The environment overlay is
    /// <c>{assembly name}.{environment}.json</c>. Both use
    /// <see cref="Assembly.GetEntryAssembly"/>, not the process executable path.
    /// Generic Host defaults (<c>appsettings.json</c> and <c>{application}.settings.json</c>)
    /// are not left in place as a fallback.
    /// </remarks>
    public static class ApplicationJsonConfiguration
    {
        /// <summary>Gets the entry assembly's short name.</summary>
        /// <returns>The <see cref="AssemblyName.Name"/> of the entry assembly.</returns>
        /// <exception cref="InvalidOperationException">The entry assembly or its name is missing.</exception>
        internal static string EntryAssemblyName
        {
            get
            {
                var name = Assembly.GetEntryAssembly()?.GetName()?.Name;
                if (string.IsNullOrWhiteSpace(name))
                {
                    throw new InvalidOperationException(
                        "The entry assembly name is required to locate application configuration.");
                }

                return name;
            }
        }

        /// <summary>Gets <c>{entry assembly name}.json</c>.</summary>
        /// <returns>The primary application JSON configuration filename.</returns>
        public static string PrimaryJsonFileName() => EntryAssemblyName + ".json";

        /// <summary>Gets <c>{entry assembly name}.{environment}.json</c>.</summary>
        /// <param name="environmentName">Host environment name, for example <c>Development</c>.</param>
        /// <returns>The environment-specific JSON configuration filename.</returns>
        /// <exception cref="ArgumentException"><paramref name="environmentName"/> is empty.</exception>
        internal static string EnvironmentJsonFileName(string environmentName)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(environmentName);
            return EntryAssemblyName + "." + environmentName + ".json";
        }

        /// <summary>
        /// Replaces Generic Host JSON sources with the entry-assembly filenames.
        /// </summary>
        /// <param name="configuration">Host configuration builder, before it is read.</param>
        /// <param name="environmentName">Current host environment name.</param>
        /// <exception cref="ArgumentNullException"><paramref name="configuration"/> is null.</exception>
        /// <exception cref="ArgumentException"><paramref name="environmentName"/> is empty.</exception>
        /// <exception cref="InvalidOperationException">The host did not register the default JSON sources.</exception>
        /// <remarks>
        /// Optional and reload-on-change flags are copied from the sources they replace.
        /// <c>appsettings.json</c> and <c>{application}.settings.json</c> are not loaded.
        /// </remarks>
        internal static void UseEntryAssemblyJsonFiles(IConfigurationBuilder configuration, string environmentName)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            ArgumentException.ThrowIfNullOrWhiteSpace(environmentName);

            var primary = PrimaryJsonFileName();
            var environmentFile = EnvironmentJsonFileName(environmentName);
            var legacyEnvironment = "appsettings." + environmentName + ".json";
            var replacedPrimary = false;
            var replacedEnvironment = false;

            for (var index = configuration.Sources.Count - 1; index >= 0; index--)
            {
                if (configuration.Sources[index] is not FileConfigurationSource file || string.IsNullOrEmpty(file.Path))
                {
                    continue;
                }

                if (IsHostNamedSettingsFile(file.Path))
                {
                    configuration.Sources.RemoveAt(index);
                    continue;
                }

                string? replacement = null;
                if (file.Path.Equals("appsettings.json", StringComparison.OrdinalIgnoreCase))
                {
                    replacement = primary;
                    replacedPrimary = true;
                }
                else if (file.Path.Equals(legacyEnvironment, StringComparison.OrdinalIgnoreCase))
                {
                    replacement = environmentFile;
                    replacedEnvironment = true;
                }

                if (replacement is null)
                {
                    continue;
                }

                var source = new JsonConfigurationSource
                {
                    Path = replacement,
                    Optional = file.Optional,
                    ReloadOnChange = file.ReloadOnChange,
                    FileProvider = file.FileProvider,
                    OnLoadException = file.OnLoadException,
                };
                source.ResolveFileProvider();
                configuration.Sources[index] = source;
            }

            if (!replacedPrimary || !replacedEnvironment)
            {
                throw new InvalidOperationException(
                    "Generic Host JSON configuration sources were not found.");
            }
        }

        /// <summary>Shared RabbitMQ configuration filename, loaded from the application binary directory.</summary>
        private const string SharedRabbitMqFileName = "RabbitMq.json";

        /// <summary>
        /// Inserts <see cref="SharedRabbitMqFileName"/> after the entry-assembly JSON sources
        /// and before environment variables and command-line arguments.
        /// </summary>
        /// <param name="configuration">Host configuration builder after <see cref="UseEntryAssemblyJsonFiles"/>.</param>
        /// <param name="environmentName">Current host environment name.</param>
        /// <exception cref="ArgumentNullException"><paramref name="configuration"/> is null.</exception>
        /// <exception cref="ArgumentException"><paramref name="environmentName"/> is empty.</exception>
        /// <exception cref="InvalidOperationException">The entry-assembly JSON sources were not found.</exception>
        /// <remarks>
        /// Optional and reload-on-change match the primary JSON source. There is no
        /// environment-specific RabbitMQ file. The section name inside the file remains <c>RabbitMQ</c>.
        /// </remarks>
        internal static void AddSharedRabbitMqJsonFile(IConfigurationBuilder configuration, string environmentName)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            ArgumentException.ThrowIfNullOrWhiteSpace(environmentName);

            var primary = PrimaryJsonFileName();
            var environmentFile = EnvironmentJsonFileName(environmentName);
            FileConfigurationSource? template = null;
            var insertAt = -1;
            for (var index = 0; index < configuration.Sources.Count; index++)
            {
                if (configuration.Sources[index] is not FileConfigurationSource file || string.IsNullOrEmpty(file.Path))
                {
                    continue;
                }

                if (file.Path.Equals(SharedRabbitMqFileName, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                if (file.Path.Equals(primary, StringComparison.OrdinalIgnoreCase)
                    || file.Path.Equals(environmentFile, StringComparison.OrdinalIgnoreCase))
                {
                    template = file;
                    insertAt = index + 1;
                }
            }

            if (template is null)
            {
                throw new InvalidOperationException(
                    "Entry-assembly JSON configuration sources were not found.");
            }

            var source = new JsonConfigurationSource
            {
                Path = SharedRabbitMqFileName,
                Optional = template.Optional,
                ReloadOnChange = template.ReloadOnChange,
                FileProvider = template.FileProvider,
                OnLoadException = template.OnLoadException,
            };
            source.ResolveFileProvider();
            configuration.Sources.Insert(insertAt, source);
        }

        private static bool IsHostNamedSettingsFile(string path) =>
            path.EndsWith(".settings.json", StringComparison.OrdinalIgnoreCase)
            || (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                && path.Contains(".settings.", StringComparison.OrdinalIgnoreCase));
    }
}
