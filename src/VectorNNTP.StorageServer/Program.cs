using Serilog;
using VectorNNTP.StorageServer.Hosting;
using VectorNNTP.StorageServer.Logging;
using VectorNNTP.Common.Configuration;

StorageServerLoggingExtensions.UseAutoFlushConsoleOutput();
Log.Logger = StorageServerLoggingExtensions.CreateBootstrapLogger();

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

    builder.ConfigureStorageServerLogging();
    builder.ConfigureStorageServerPlatformHosting();
    builder.AddStorageServerHosting();

    var host = builder.Build();
    StorageServerLoggingExtensions.WriteLoggingInitialized(
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
