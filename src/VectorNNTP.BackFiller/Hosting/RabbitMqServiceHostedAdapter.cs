using VectorNNTP.Common.Messaging.RabbitMq;

namespace VectorNNTP.BackFiller.Hosting;

/// <summary>
/// Forwards Generic Host start/stop to <see cref="RabbitMqService"/> without registering it
/// into BackFiller <c>ApplicationServiceManager</c>.
/// </summary>
/// <remarks>
/// BackFiller keeps RabbitMQ as an early <see cref="IHostedService"/> (before accounts).
/// Application services remain Cloudflare → ACME → Cache Listener.
/// </remarks>
internal sealed class RabbitMqServiceHostedAdapter : IHostedService
{
    private readonly RabbitMqService _service;

    /// <summary>Initializes a new adapter around the shared RabbitMQ connection service.</summary>
    internal RabbitMqServiceHostedAdapter(RabbitMqService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        _service = service;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken) =>
        _service.StartAsync(cancellationToken);

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) =>
        _service.StopAsync(cancellationToken);
}
