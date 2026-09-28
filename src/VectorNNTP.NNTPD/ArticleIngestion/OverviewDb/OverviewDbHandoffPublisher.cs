using Microsoft.Extensions.Options;
using VectorNNTP.NNTPD.Configuration;
using VectorNNTP.NNTPD.Diagnostics;
using VectorNNTP.NNTPD.RabbitMq;

namespace VectorNNTP.NNTPD.ArticleIngestion.OverviewDb;

/// <summary>
/// Publishes OverviewDB protobuf payloads to <c>overviewdb.queue</c> with publisher confirms.
/// </summary>
/// <remarks>
/// RabbitMQ is the handoff boundary. This type does not open an OverviewDB client,
/// RPC channel, HTTP/gRPC call, or database connection, and does not wait for an
/// OverviewDB acknowledgement.
/// </remarks>
internal sealed class OverviewDbHandoffPublisher : IOverviewDbHandoffPublisher, IAsyncDisposable
{
    private readonly IRabbitMqService _rabbitMq;
    private readonly IOptions<NntpdOptions> _nntpdOptions;
    private readonly IOptions<RabbitMqOptions> _rabbitMqOptions;
    private readonly IngestionPipelineMetrics? _pipeline;
    private readonly SemaphoreSlim _publishGate = new(1, 1);
    private IRabbitMqPublishChannel? _channel;
    private int _disposed;

    /// <summary>Initializes a new OverviewDB handoff publisher.</summary>
    public OverviewDbHandoffPublisher(
        IRabbitMqService rabbitMq,
        IOptions<NntpdOptions> nntpdOptions,
        IOptions<RabbitMqOptions> rabbitMqOptions,
        IngestionPipelineMetrics? pipelineMetrics = null)
    {
        ArgumentNullException.ThrowIfNull(rabbitMq);
        ArgumentNullException.ThrowIfNull(nntpdOptions);
        ArgumentNullException.ThrowIfNull(rabbitMqOptions);
        _rabbitMq = rabbitMq;
        _nntpdOptions = nntpdOptions;
        _rabbitMqOptions = rabbitMqOptions;
        _pipeline = pipelineMetrics;
    }

    /// <inheritdoc />
    public async Task PublishConfirmedAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);

        var fqdn = _nntpdOptions.Value.Fqdn;
        if (string.IsNullOrWhiteSpace(fqdn))
        {
            throw new InvalidOperationException("OverviewDB handoff requires the generated application FQDN.");
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var confirmTimeoutSeconds = _rabbitMqOptions.Value.PublishConfirmTimeoutSeconds ?? 10;
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(confirmTimeoutSeconds));

        var handoffStart = System.Diagnostics.Stopwatch.GetTimestamp();
        var gateStart = handoffStart;
        try
        {
            await _publishGate.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            _pipeline?.RecordConfirmTimeout();
            _pipeline?.RecordHandoff(handoffStart);
            throw;
        }

        _pipeline?.RecordGateWait(gateStart);
        try
        {
            var channel = await GetOrCreateChannelAsync(timeoutCts.Token).ConfigureAwait(false);
            var publishStart = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                await channel.PublishConfirmedAsync(
                        OverviewDbTopology.DefaultExchange,
                        OverviewDbTopology.RoutingKey,
                        Guid.NewGuid().ToString(),
                        fqdn,
                        OverviewDbTopology.ExpirationMilliseconds,
                        payload,
                        timeoutCts.Token)
                    .ConfigureAwait(false);
                _pipeline?.RecordPublishAndConfirm(publishStart);
            }
            catch (OperationCanceledException) when (
                timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                _pipeline?.RecordConfirmTimeout();
                throw;
            }
            catch
            {
                _pipeline?.RecordConfirmFailure();
                throw;
            }
        }
        finally
        {
            _pipeline?.RecordHandoff(handoffStart);
            _publishGate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        await _publishGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await DisposeChannelAsync().ConfigureAwait(false);
        }
        finally
        {
            _publishGate.Release();
            _publishGate.Dispose();
        }
    }

    private async Task<IRabbitMqPublishChannel> GetOrCreateChannelAsync(CancellationToken cancellationToken)
    {
        if (!_rabbitMq.TryGetCurrent(out var handle) || !handle.IsCurrent)
        {
            throw new InvalidOperationException("RabbitMQ connection is not ready for OverviewDB handoff.");
        }

        var current = _channel;
        if (current is not null && current.Generation == handle.Generation && current.IsOpen)
        {
            return current;
        }

        await DisposeChannelAsync().ConfigureAwait(false);
        var channel = await handle.Connection
            .CreatePublishChannelAsync(handle.Generation, cancellationToken)
            .ConfigureAwait(false);
        _channel = channel;
        return channel;
    }

    private async Task DisposeChannelAsync()
    {
        var channel = _channel;
        _channel = null;
        if (channel is not null)
        {
            await channel.DisposeAsync().ConfigureAwait(false);
        }
    }
}
