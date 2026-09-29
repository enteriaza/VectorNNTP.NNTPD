using VectorNNTP.NNTPD.Core;

namespace VectorNNTP.NNTPD.RabbitMq;

/// <summary>
/// Declares NNTPD-owned RabbitMQ topology: internal storage article-retrieval and OverviewDB.
/// </summary>
/// <remarks>
/// <para>
/// This service owns NNTPD-specific topology only: the internal <c>backfiller.storage</c>
/// endpoint and the one-way OverviewDB ingest queue <c>overviewdb.queue</c>. Per-backbone
/// <c>backfiller.*</c> provider exchanges and queues are declared by VectorNNTP.BackFiller
/// when a backbone becomes usable. Article-work RPC reply queues are owned by
/// <c>ArticleWorkRpcService</c> (exclusive, auto-delete, non-durable classic).
/// <see cref="RabbitMqService"/> remains the sole connection lifecycle owner. The
/// topology service obtains the current generation through
/// <see cref="IRabbitMqService.TryGetCurrent"/> and opens one declare-only channel for the
/// startup pass.
/// </para>
/// <para>
/// Declaration uses RabbitMQ's normal idempotent declare/bind operations. Existing
/// entities are never deleted, purged, or mutated. An incompatible existing entity
/// fails startup. Current VectorNNTP-owned durable application work queues are quorum
/// (<c>x-queue-type=quorum</c>). Exclusive auto-delete ArticleWork RPC reply queues
/// remain non-durable classic.
/// </para>
/// <para>
/// Topology is established during <see cref="StartAsync"/> and is required before
/// NNTPD reports <c>RUNNING</c>. After a later connection-generation replacement,
/// broker-side durable topology is assumed to remain. This phase does not subscribe
/// to <see cref="IRabbitMqService.ConnectionReplaced"/> and does not run a second
/// recovery loop.
/// </para>
/// </remarks>
public sealed class RabbitMqTopologyService : IApplicationService
{
    private readonly IRabbitMqService _rabbitMq;
    private readonly ILogger<RabbitMqTopologyService> _logger;
    private int _started;

    /// <summary>Initializes a new instance of the <see cref="RabbitMqTopologyService"/> class.</summary>
    public RabbitMqTopologyService(
        IRabbitMqService rabbitMq,
        ILogger<RabbitMqTopologyService> logger)
    {
        ArgumentNullException.ThrowIfNull(rabbitMq);
        ArgumentNullException.ThrowIfNull(logger);
        _rabbitMq = rabbitMq;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "RabbitMQTopology";

    /// <inheritdoc />
    public Task? Execution => null;

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            return;
        }

        try
        {
            await DeclareRequiredTopologyAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Interlocked.Exchange(ref _started, 0);
            throw;
        }
        catch (OperationCanceledException)
        {
            Interlocked.Exchange(ref _started, 0);
            throw;
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Declares the internal <c>backfiller.storage</c> endpoint and the OverviewDB
    /// <c>overviewdb.queue</c> durable quorum queue.
    /// </summary>
    /// <param name="cancellationToken">Token used to cancel declaration.</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when no current RabbitMQ connection is available.
    /// </exception>
    internal async Task DeclareRequiredTopologyAsync(CancellationToken cancellationToken)
    {
        var definitions = ArticleRetrievalTopology.Required;
        RabbitMqTopologyLogMessages.Establishing(_logger, definitions.Count);

        if (!_rabbitMq.TryGetCurrent(out var handle))
        {
            RabbitMqTopologyLogMessages.ConnectionNotReady(_logger);
            throw new InvalidOperationException("RabbitMQ connection is not ready for topology declaration.");
        }

        IRabbitMqTopologyChannel? channel = null;
        RabbitMqArticleRetrievalEndpoint? current = null;
        try
        {
            channel = await handle.Connection
                .CreateTopologyChannelAsync(cancellationToken)
                .ConfigureAwait(false);

            for (var i = 0; i < definitions.Count; i++)
            {
                current = definitions[i];
                await DeclareEndpointAsync(channel, current, cancellationToken).ConfigureAwait(false);
            }

            current = null;
            await DeclareOverviewDbQueueAsync(channel, cancellationToken).ConfigureAwait(false);

            RabbitMqTopologyLogMessages.Established(_logger, definitions.Count, handle.Generation);
            RabbitMqTopologyLogMessages.OverviewQueueDeclared(_logger, OverviewDbTopology.QueueName, handle.Generation);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RabbitMqTopologyLogMessages.DeclarationFailed(
                _logger,
                ex,
                current?.ExchangeName ?? OverviewDbTopology.QueueName,
                current?.ExchangeName ?? OverviewDbTopology.DefaultExchange,
                current?.QueueName ?? OverviewDbTopology.QueueName);
            throw;
        }
        finally
        {
            if (channel is not null)
            {
                await channel.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static async Task DeclareEndpointAsync(
        IRabbitMqTopologyChannel channel,
        RabbitMqArticleRetrievalEndpoint definition,
        CancellationToken cancellationToken)
    {
        await channel.ExchangeDeclareAsync(
            definition.ExchangeName,
            definition.ExchangeType,
            definition.ExchangeDurable,
            definition.ExchangeAutoDelete,
            arguments: null,
            cancellationToken).ConfigureAwait(false);

        await channel.QueueDeclareAsync(
            definition.QueueName,
            definition.QueueDurable,
            definition.QueueExclusive,
            definition.QueueAutoDelete,
            definition.QueueArguments,
            cancellationToken).ConfigureAwait(false);

        await channel.QueueBindAsync(
            definition.QueueName,
            definition.ExchangeName,
            definition.RoutingKey,
            arguments: null,
            cancellationToken).ConfigureAwait(false);
    }

    private static Task DeclareOverviewDbQueueAsync(
        IRabbitMqTopologyChannel channel,
        CancellationToken cancellationToken) =>
        channel.QueueDeclareAsync(
            OverviewDbTopology.QueueName,
            durable: true,
            exclusive: false,
            autoDelete: false,
            OverviewDbTopology.QueueArguments,
            cancellationToken);
}
