using Serilog;
using VectorNNTP.NNTPD.Configuration;
using VectorNNTP.NNTPD.Hosting;
using VectorNNTP.NNTPD.Logging;
using VectorNNTP.NNTPD.Session.CommandProcessor;

NntpdLoggingExtensions.UseAutoFlushConsoleOutput();
Log.Logger = NntpdLoggingExtensions.CreateBootstrapLogger();

try
{
    Log.Information("VectorNNTP.NNTPD host starting");

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

    Log.Information("VectorNNTP.NNTPD host stopped cleanly");
    return 0;
}
catch (Exception ex)
{
    Log.Fatal(ex, "VectorNNTP.NNTPD host terminated unexpectedly");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync().ConfigureAwait(false);
}
