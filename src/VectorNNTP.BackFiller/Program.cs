using Serilog;
using VectorNNTP.BackFiller.Hosting;
using VectorNNTP.BackFiller.Logging;
using VectorNNTP.Common.Configuration;

var loggingCommandLine = BackFillerLoggingCommandLine.FromArguments(args);
if (loggingCommandLine.Console)
{
    BackFillerLoggingExtensions.UseAutoFlushConsoleOutput();
}

Log.Logger = BackFillerLoggingExtensions.CreateBootstrapLogger(loggingCommandLine);

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

    builder.ConfigureBackFillerLogging(commandLine: loggingCommandLine);
    builder.ConfigureBackFillerPlatformHosting();
    builder.AddBackFillerHosting();

    var host = builder.Build();
    BackFillerLoggingExtensions.WriteLoggingInitialized(
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
