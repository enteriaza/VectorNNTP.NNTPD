using Microsoft.Extensions.DependencyInjection.Extensions;
using Serilog;
using Serilog.Events;
using VectorNNTP.NNTPD.Configuration;

namespace VectorNNTP.BackFiller.Logging
{
    /// <summary>
    /// Configures Serilog as the exclusive logging implementation for VectorNNTP.BackFiller.
    /// </summary>
    internal static partial class BackFillerLoggingExtensions
    {
        /// <summary>
        /// Single-line console template suitable for interactive terminals and journald collection.
        /// </summary>
        /// <remarks>
        /// Used by the bootstrap logger. Host Console/File sinks use
        /// <see cref="BackFillerFileLogging.SinkOutputTemplate"/>.
        /// </remarks>
        internal const string ConsoleOutputTemplate =
            "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}";

        /// <summary>
        /// Creates an early bootstrap logger used before the Generic Host is built.
        /// </summary>
        /// <param name="commandLine">
        /// When <see cref="BackFillerLoggingCommandLine.Console"/> is true, the bootstrap logger writes to the console
        /// with <see cref="ConsoleOutputTemplate"/>. When <see cref="BackFillerLoggingCommandLine.EnrichFromLogContext"/>
        /// is true, the logger enriches from the ambient log context. The default value enables neither.
        /// </param>
        /// <returns>A bootstrap logger assigned to <see cref="Log.Logger"/>.</returns>
        internal static Serilog.ILogger CreateBootstrapLogger(BackFillerLoggingCommandLine commandLine = default)
        {
            var configuration = new LoggerConfiguration()
                .MinimumLevel.Override("Microsoft", LogEventLevel.Information)
                .Enrich.WithProperty("Application", ApplicationJsonConfiguration.EntryAssemblyName);
            if (commandLine.EnrichFromLogContext)
            {
                configuration.Enrich.FromLogContext();
            }

            if (commandLine.Console)
            {
                configuration.WriteTo.Console(outputTemplate: ConsoleOutputTemplate);
            }

            return configuration.CreateBootstrapLogger();
        }

        /// <summary>
        /// Makes <see cref="Console.Out"/> flush every write.
        /// </summary>
        /// <remarks>
        /// When stdout is a console, the runtime already flushes often enough that Information
        /// lines appear immediately. When stdout is a pipe (IDE, redirected capture),
        /// <see cref="Console.Out"/> is block-buffered: a startup burst fills the buffer and
        /// becomes visible, then later Information events sit unseen until the buffer fills
        /// again or the process exits. Serilog's Console sink writes to <see cref="Console.Out"/>
        /// and does not Flush. This must run before the first Serilog Console sink is created.
        /// </remarks>
        internal static void UseAutoFlushConsoleOutput()
        {
            var writer = new StreamWriter(Console.OpenStandardOutput(), Console.OutputEncoding)
            {
                AutoFlush = true,
            };
            Console.SetOut(writer);
        }

        /// <summary>
        /// Removes Microsoft default logging providers and registers Serilog as the sole provider.
        /// </summary>
        /// <param name="builder">The host application builder.</param>
        /// <param name="configure">
        /// Optional additional Serilog configuration applied after the explicit host logger graph.
        /// Used by tests to attach in-memory sinks without reintroducing MEL providers.
        /// </param>
        /// <param name="commandLine">
        /// Process switches that enable the console sink and ambient context enrichment.
        /// </param>
        /// <returns>The same <paramref name="builder"/> instance.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="builder"/> is null.</exception>
        /// <remarks>
        /// <para>
        /// Call order: clear MEL providers, remove default <see cref="ILoggerFactory"/> registrations,
        /// then <c>AddSerilog</c> with <c>writeToProviders: false</c> so framework and application
        /// <see cref="ILogger{T}"/> traffic flows only through Serilog sinks.
        /// </para>
        /// <para>
        /// Host sinks are registered explicitly in <see cref="BackFillerFileLogging.ConfigureLogger"/>
        /// from <c>BackFiller:Logging</c>, including <c>BackFiller:Logging:File:LogDir</c>.
        /// <c>ReadFrom.Configuration</c> is not used, and a <c>Serilog</c> configuration section
        /// cannot change the pipeline. The console sink is added only when
        /// <see cref="BackFillerLoggingCommandLine.Console"/> is true.
        /// </para>
        /// <para>
        /// Console formatting is owned by Serilog. journald collects Serilog console stdout under
        /// systemd. Microsoft console formatter options that <c>AddSystemd()</c> may register are
        /// stripped by <c>ConfigureBackFillerPlatformHosting</c> and are not part of this logging pipeline.
        /// </para>
        /// </remarks>
        internal static HostApplicationBuilder ConfigureBackFillerLogging(
            this HostApplicationBuilder builder,
            Action<LoggerConfiguration>? configure = null,
            BackFillerLoggingCommandLine commandLine = default)
        {
            ArgumentNullException.ThrowIfNull(builder);

            builder.Logging.ClearProviders();

            // Host.CreateApplicationBuilder registers a default LoggerFactory; remove it so
            // SerilogLoggerFactory is the only ILoggerFactory in the container.
            builder.Services.RemoveAll<ILoggerFactory>();
            builder.Services.RemoveAll<ILoggerProvider>();

            builder.Services.AddSerilog(
                (services, loggerConfiguration) =>
                {
                    BackFillerFileLogging.ConfigureLogger(
                        loggerConfiguration,
                        builder.Configuration,
                        commandLine: commandLine,
                        services: services);
                    loggerConfiguration.ReadFrom.Services(services);
                    configure?.Invoke(loggerConfiguration);
                },
                preserveStaticLogger: false,
                writeToProviders: false);

            return builder;
        }

        /// <summary>
        /// Emits one Information event through the host <see cref="ILoggerFactory"/> after
        /// Serilog has replaced the bootstrap logger.
        /// </summary>
        /// <param name="services">The built host service provider.</param>
        /// <param name="environmentName">Host environment name (no secrets).</param>
        /// <param name="contentRootPath">Resolved content root used for <c>VectorNNTP.BackFiller.json</c>.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="services"/> is null.</exception>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="environmentName"/> or <paramref name="contentRootPath"/> is null or whitespace.
        /// </exception>
        internal static void WriteLoggingInitialized(
            IServiceProvider services,
            string environmentName,
            string contentRootPath)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentException.ThrowIfNullOrWhiteSpace(environmentName);
            ArgumentException.ThrowIfNullOrWhiteSpace(contentRootPath);

            var factory = services.GetRequiredService<ILoggerFactory>();
            var logger = factory.CreateLogger(BackFillerLogCategories.Hosting);
            LoggingInitialized(
                logger,
                ApplicationJsonConfiguration.EntryAssemblyName,
                factory.GetType().Name,
                BackFillerLogCategories.Hosting,
                environmentName,
                contentRootPath);
        }

        /// <summary>
        /// Written by <see cref="WriteLoggingInitialized"/> after the host <see cref="ILoggerFactory"/> is available.
        /// </summary>
        /// <param name="logger">Host logger created for <see cref="BackFillerLogCategories.Hosting"/>.</param>
        /// <param name="application">Entry assembly name from <see cref="ApplicationJsonConfiguration.EntryAssemblyName"/>.</param>
        /// <param name="provider">Runtime type name of the host <see cref="ILoggerFactory"/>.</param>
        /// <param name="category"><see cref="BackFillerLogCategories.Hosting"/>.</param>
        /// <param name="environment">Host environment name. No secrets.</param>
        /// <param name="contentRoot">Content root path passed to <see cref="WriteLoggingInitialized"/>.</param>
        [LoggerMessage(
            EventId = 1,
            Level = LogLevel.Information,
            Message = "Application logging initialized Application={Application} Provider={Provider} Category={Category} Environment={Environment} ContentRoot={ContentRoot}")]
        private static partial void LoggingInitialized(
            Microsoft.Extensions.Logging.ILogger logger,
            string application,
            string provider,
            string category,
            string environment,
            string contentRoot);
    }
}
