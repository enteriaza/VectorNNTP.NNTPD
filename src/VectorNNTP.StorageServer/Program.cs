using Serilog;
using VectorNNTP.StorageServer.Hosting;
using VectorNNTP.StorageServer.Logging;

StorageServerLoggingExtensions.UseAutoFlushConsoleOutput();
Log.Logger = StorageServerLoggingExtensions.CreateBootstrapLogger();

try
{
    Log.Information("VectorNNTP.StorageServer host starting");

    var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
    {
        Args = args,
        ContentRootPath = AppContext.BaseDirectory,
    });
    builder.Environment.ContentRootPath = AppContext.BaseDirectory;

    builder.ConfigureStorageServerLogging();
    builder.ConfigureStorageServerPlatformHosting();
    builder.AddStorageServerHosting();

    var host = builder.Build();
    StorageServerLoggingExtensions.WriteLoggingInitialized(
        host.Services,
        builder.Environment.EnvironmentName,
        builder.Environment.ContentRootPath);
    await host.RunAsync().ConfigureAwait(false);

    Log.Information("VectorNNTP.StorageServer host stopped cleanly");
    return 0;
}
catch (Exception ex)
{
    Log.Fatal(ex, "VectorNNTP.StorageServer host terminated unexpectedly");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync().ConfigureAwait(false);
}
