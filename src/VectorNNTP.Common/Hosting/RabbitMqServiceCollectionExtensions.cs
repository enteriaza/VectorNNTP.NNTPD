using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using VectorNNTP.Common.Messaging.RabbitMq;

namespace VectorNNTP.Common.Hosting
{
    /// <summary>
    /// Registers shared RabbitMQ connection infrastructure.
    /// </summary>
    public static class RabbitMqServiceCollectionExtensions
    {
        /// <summary>
        /// Adds the RabbitMQ connection factory and <see cref="RabbitMqService"/> as singletons.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <returns>The same <paramref name="services"/> instance.</returns>
        /// <remarks>
        /// <para>
        /// Does not bind configuration, validate options, connect to a broker, or register
        /// <c>IApplicationService</c> / <c>IHostedService</c>. Applications bind the
        /// <c>RabbitMQ</c> section, register <see cref="IRabbitMqConnectionNameProvider"/>,
        /// and add <see cref="RabbitMqService"/> as an application service when desired.
        /// </para>
        /// <para>
        /// Topology, publishers, consumers, and Management HTTP clients remain application-owned.
        /// </para>
        /// </remarks>
        public static IServiceCollection AddRabbitMqInfrastructure(this IServiceCollection services)
        {
            ArgumentNullException.ThrowIfNull(services);

            services.TryAddSingleton<IRabbitMqConnectionFactory, RabbitMqClientConnectionFactory>();
            services.TryAddSingleton<RabbitMqService>();
            services.TryAddSingleton<IRabbitMqService>(static sp => sp.GetRequiredService<RabbitMqService>());

            return services;
        }
    }
}
