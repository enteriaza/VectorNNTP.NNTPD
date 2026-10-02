using Serilog;
using VectorNNTP.NNTPD.Configuration;
using VectorNNTP.NNTPD.Hosting;
using VectorNNTP.NNTPD.Logging;
using VectorNNTP.NNTPD.Session.CommandProcessor;

NntpdLoggingExtensions.UseAutoFlushConsoleOutput();
Log.Logger = NntpdLoggingExtensions.CreateBootstrapLogger();

try
{
    Log.Information("{Application} host starting", ApplicationJsonConfiguration.EntryAssemblyName);

    var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
    {
        Args = args,
        ContentRootPath = AppContext.BaseDirectory,
    });
    builder.Environment.ContentRootPath = AppContext.BaseDirectory;
    ApplicationJsonConfiguration.UseEntryAssemblyJsonFiles(
        builder.Configuration,
        builder.Environment.EnvironmentName);
    ApplicationJsonConfiguration.AddSharedRabbitMqJsonFile(
        builder.Configuration,
        builder.Environment.EnvironmentName);

    builder.ConfigureNntpdLogging();
    builder.ConfigureNntpdPlatformHosting();
    builder.Services.AddNntpdHosting();

    var host = builder.Build();
    NntpCommandLoggers.Configure(host.Services.GetRequiredService<ILoggerFactory>());
    NntpdLoggingExtensions.WriteLoggingInitialized(
        host.Services,
        builder.Environment.EnvironmentName,
        builder.Environment.ContentRootPath);
    await host.RunAsync().ConfigureAwait(false);

    Log.Information("{Application} host stopped cleanly", ApplicationJsonConfiguration.EntryAssemblyName);
    return 0;
}
catch (Exception ex)
{
    Log.Fatal(ex, "{Application} host terminated unexpectedly", ApplicationJsonConfiguration.EntryAssemblyName);
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync().ConfigureAwait(false);
}
