using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using VectorNNTP.NNTPD.Acme;
using VectorNNTP.NNTPD.Cloudflare;
using VectorNNTP.NNTPD.Configuration;
using VectorNNTP.NNTPD.Networking;

namespace VectorNNTP.Common.Hosting;

/// <summary>
/// Registers shared ACME, Cloudflare, and bind-address infrastructure.
/// </summary>
public static class AcmeCloudflareServiceCollectionExtensions
{
    /// <summary>
    /// Adds bind-address resolution, Cloudflare DNS, and ACME component factories.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same <paramref name="services"/> instance.</returns>
    /// <remarks>
    /// Requires <see cref="IOptions{TOptions}"/> of <see cref="AcmeCloudflareOptions"/> to be registered
    /// by the application. Does not register application lifecycle/hosted wrappers.
    /// </remarks>
    public static IServiceCollection AddAcmeCloudflareInfrastructure(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<ILocalIpAddressAssignee, NetworkInterfaceLocalIpAddressAssignee>();
        services.TryAddSingleton<IBindAddressResolver, BindAddressResolver>();
        services.TryAddSingleton<IAcmeCertificateReadiness, AcmeCertificateReadiness>();
        services.TryAddSingleton<ICloudflareDnsReconciler, CloudflareDnsReconciler>();

        services.AddHttpClient(CloudflareDnsClient.HttpClientName, static client =>
        {
            client.BaseAddress = new Uri("https://api.cloudflare.com/client/v4/");
            client.Timeout = Timeout.InfiniteTimeSpan;
            client.DefaultRequestHeaders.ExpectContinue = false;
        });

        services.AddHttpClient(CertesAcmeIssuer.HttpClientName, static client =>
        {
            client.Timeout = Timeout.InfiniteTimeSpan;
            client.DefaultRequestHeaders.ExpectContinue = false;
        });

        services.TryAddSingleton<ICloudflareDnsClient>(static sp =>
        {
            var httpClient = sp.GetRequiredService<IHttpClientFactory>()
                .CreateClient(CloudflareDnsClient.HttpClientName);
            return new CloudflareDnsClient(
                httpClient,
                sp.GetRequiredService<IOptions<AcmeCloudflareOptions>>(),
                sp.GetRequiredService<ILogger<CloudflareDnsClient>>());
        });

        services.TryAddSingleton<AcmeComponentFactory>();
        services.TryAddSingleton<IServerCertificateProvider>(static sp =>
            sp.GetRequiredService<AcmeComponentFactory>().GetCertificateProvider());
        services.TryAddSingleton<CloudflareDnsReconciliationService>();
        services.TryAddSingleton<AcmeCertificateService>();

        return services;
    }
}
