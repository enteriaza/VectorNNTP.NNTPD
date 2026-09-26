using RabbitMQ.Client;

namespace VectorNNTP.BackFiller.RabbitMq;

/// <summary>Owns one RabbitMQ.Client channel. Does not own the parent connection.</summary>
internal sealed class BackFillerRabbitMqClientChannel : IBackFillerRabbitMqChannel
{
    private readonly IChannel _channel;
    private int _disposed;

    /// <summary>
    /// Initializes a new wrapper around an opened channel.
    /// </summary>
    /// <param name="channel">Opened RabbitMQ.Client channel.</param>
    /// <param name="generation">Connection generation the channel belongs to.</param>
    internal BackFillerRabbitMqClientChannel(IChannel channel, long generation)
    {
        ArgumentNullException.ThrowIfNull(channel);
        _channel = channel;
        Generation = generation;
    }

    /// <inheritdoc />
    public long Generation { get; }

    /// <inheritdoc />
    public bool IsOpen => _channel.IsOpen;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        try
        {
            if (_channel.IsOpen)
            {
                await _channel.CloseAsync().ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
        }

        await _channel.DisposeAsync().ConfigureAwait(false);
    }
}
