using Serilog;
using VectorNNTP.BackFiller.Hosting;
using VectorNNTP.BackFiller.Logging;

BackFillerLoggingExtensions.UseAutoFlushConsoleOutput();
Log.Logger = BackFillerLoggingExtensions.CreateBootstrapLogger();

try
{
    Log.Information("VectorNNTP.BackFiller host starting");

    var builder = Host.CreateApplicationBuilder(args);

    builder.ConfigureBackFillerLogging();
    builder.ConfigureBackFillerPlatformHosting();
    builder.Services.AddBackFillerHosting();

    var host = builder.Build();
    BackFillerLoggingExtensions.WriteLoggingInitialized(
        host.Services,
        builder.Environment.EnvironmentName,
        builder.Environment.ContentRootPath);
    await host.RunAsync().ConfigureAwait(false);

    Log.Information("VectorNNTP.BackFiller host stopped cleanly");
    return 0;
}
catch (Exception ex)
{
    Log.Fatal(ex, "VectorNNTP.BackFiller host terminated unexpectedly");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync().ConfigureAwait(false);
}
