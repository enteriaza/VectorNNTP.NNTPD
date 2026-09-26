using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using VectorNNTP.NNTPD.Configuration;

namespace VectorNNTP.Common.Hosting;

/// <summary>
/// Registers the shared NntpDB options binding used by NNTPD and BackFiller.
/// </summary>
public static class NntpDbServiceCollectionExtensions
{
    /// <summary>
    /// Binds <see cref="NntpDbOptions"/> from the <c>NntpDb</c> section and
    /// copies <c>ConnectionStrings:NntpDB</c> when the options connection string is empty.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same <paramref name="services"/> instance.</returns>
    /// <remarks>
    /// This is configuration only. It does not open MySQL, create schema, or
    /// register <c>NntpDbService</c>.
    /// </remarks>
    public static IServiceCollection AddNntpDbOptions(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services
            .AddOptions<NntpDbOptions>()
            .BindConfiguration(NntpDbOptions.SectionName)
            .Configure<IConfiguration>(static (options, configuration) =>
            {
                if (string.IsNullOrWhiteSpace(options.ConnectionString))
                {
                    options.ConnectionString =
                        configuration.GetConnectionString(NntpDbOptions.ConnectionStringName) ?? string.Empty;
                }
            })
            .ValidateOnStart();
        services.TryAddSingleton<IValidateOptions<NntpDbOptions>, NntpDbOptionsValidator>();
        return services;
    }
}
