using Serilog;
using VectorNNTP.NNTPD.Hosting;
using VectorNNTP.NNTPD.Logging;

Log.Logger = NntpdLoggingExtensions.CreateBootstrapLogger();

try
{
    Log.Information("VectorNNTP.NNTPD host starting");

    var builder = Host.CreateApplicationBuilder(args);

    builder.ConfigureNntpdLogging();
    builder.ConfigureNntpdPlatformHosting();
    builder.Services.AddNntpdHosting();

    var host = builder.Build();
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
