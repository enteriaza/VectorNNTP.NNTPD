using System.Threading.Channels;

namespace VectorNNTP.StorageServer.Ingress;

/// <summary>
/// Unbounded multi-writer, multi-reader queue of accepted articles.
/// </summary>
/// <remarks>
/// <para>
/// The channel is unbounded. Unread articles remain in process memory until dequeued
/// or the process exits. That growth is deliberate: a full queue must not drop an
/// article, and this server no longer has a filesystem capacity budget. Shutdown
/// calls <see cref="Complete"/> so producers stop and consumers waiting on an empty
/// queue return without an article.
/// </para>
/// <para>
/// <see cref="Channel{T}"/> is created with multiple writers and multiple readers enabled.
/// Enqueue does not copy <see cref="AcceptedArticle.ArtData"/>.
/// </para>
/// </remarks>
public sealed class AcceptedArticleQueue : IAcceptedArticleQueue
{
    private readonly Channel<AcceptedArticle> _channel = Channel.CreateUnbounded<AcceptedArticle>(
        new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });

    private long _enqueued;
    private long _dequeued;
    private long _queuedBytes;

    /// <summary>Articles successfully written to the channel.</summary>
    public long EnqueuedCount => Volatile.Read(ref _enqueued);

    /// <summary>Articles removed by <see cref="DequeueAsync"/>.</summary>
    public long DequeuedCount => Volatile.Read(ref _dequeued);

    /// <summary>
    /// Bytes currently held by the channel. A snapshot can tear relative to
    /// <see cref="EnqueuedCount"/> and <see cref="DequeuedCount"/>.
    /// </summary>
    public long QueuedBytes => Volatile.Read(ref _queuedBytes);

    /// <inheritdoc />
    public bool TryEnqueue(AcceptedArticle article)
    {
        if (article.ArtSize < 1 || article.ArtData.Length != article.ArtSize)
        {
            throw new ArgumentException("ArtSize must match ArtData.Length and be at least 1.", nameof(article));
        }

        if (!_channel.Writer.TryWrite(article))
        {
            return false;
        }

        Interlocked.Increment(ref _enqueued);
        Interlocked.Add(ref _queuedBytes, article.ArtSize);
        return true;
    }

    /// <inheritdoc />
    public async ValueTask<AcceptedArticleDelivery> DequeueAsync(CancellationToken cancellationToken)
    {
        try
        {
            var article = await _channel.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            Interlocked.Increment(ref _dequeued);
            Interlocked.Add(ref _queuedBytes, -article.ArtSize);
            return new AcceptedArticleDelivery(true, article);
        }
        catch (ChannelClosedException)
        {
            return default;
        }
    }

    /// <inheritdoc />
    public void Complete() => _channel.Writer.TryComplete();
}
