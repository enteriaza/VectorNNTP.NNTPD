using System.Threading.Channels;
using VectorNNTP.StorageServer.Configuration;

namespace VectorNNTP.StorageServer.Ingress;

/// <summary>
/// Multi-writer, multi-reader queue of accepted articles, bounded by queued bytes.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="TryEnqueue"/> returns false when the byte budget would be exceeded or the
/// queue has been completed. The channel itself stays unbounded so a reserved article is
/// not dropped after the budget check. Enqueue does not copy <see cref="AcceptedArticle.ArtData"/>.
/// </para>
/// <para>
/// The default budget is one gibibyte. At 768,000-byte articles that is about 1,398 articles.
/// VATP RESULT is sent only after a true enqueue. A false enqueue is the overload signal
/// and is not a durable-storage acknowledgement.
/// </para>
/// </remarks>
public sealed class AcceptedArticleQueue : IAcceptedArticleQueue
{
    private readonly long _maxQueuedBytes;
    private readonly TimeProvider _time;
    private readonly Channel<AcceptedArticle> _channel = Channel.CreateUnbounded<AcceptedArticle>(
        new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });

    private readonly object _gate = new();
    private readonly Queue<long> _stamps = new();
    private long _enqueued;
    private long _dequeued;
    private long _queuedBytes;
    private bool _completed;

    /// <summary>Initializes the queue with the default one-gibibyte budget.</summary>
    public AcceptedArticleQueue()
        : this(SpoolPersistenceOptions.DefaultMaxQueuedBytes, TimeProvider.System)
    {
    }

    /// <summary>Initializes the queue with an explicit byte budget and clock.</summary>
    /// <param name="maxQueuedBytes">Maximum queued article bytes. Must be at least 1.</param>
    /// <param name="time">Clock used for oldest-item age. The queue stores timestamps, not the articles' clocks.</param>
    public AcceptedArticleQueue(long maxQueuedBytes, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        if (maxQueuedBytes < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxQueuedBytes));
        }

        _maxQueuedBytes = maxQueuedBytes;
        _time = time;
    }

    /// <summary>Maximum bytes <see cref="TryEnqueue"/> will hold.</summary>
    public long MaxQueuedBytes => _maxQueuedBytes;

    /// <summary>Articles successfully written to the channel.</summary>
    public long EnqueuedCount
    {
        get
        {
            lock (_gate)
            {
                return _enqueued;
            }
        }
    }

    /// <summary>Articles removed by <see cref="TryDequeue"/> or <see cref="DequeueAsync"/>.</summary>
    public long DequeuedCount
    {
        get
        {
            lock (_gate)
            {
                return _dequeued;
            }
        }
    }

    /// <summary>Bytes currently held by the channel.</summary>
    public long QueuedBytes
    {
        get
        {
            lock (_gate)
            {
                return _queuedBytes;
            }
        }
    }

    /// <inheritdoc />
    public int QueuedCount
    {
        get
        {
            lock (_gate)
            {
                return _stamps.Count;
            }
        }
    }

    /// <inheritdoc />
    public bool IsCompleted
    {
        get
        {
            lock (_gate)
            {
                return _completed;
            }
        }
    }

    /// <inheritdoc />
    public TimeSpan OldestAge(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        long stamp;
        lock (_gate)
        {
            if (_stamps.Count == 0)
            {
                return TimeSpan.Zero;
            }

            stamp = _stamps.Peek();
        }

        var age = time.GetElapsedTime(stamp);
        return age < TimeSpan.Zero ? TimeSpan.Zero : age;
    }

    /// <inheritdoc />
    public bool TryEnqueue(AcceptedArticle article)
    {
        if (article.ArtSize < 1 || article.ArtData.Length != article.ArtSize)
        {
            throw new ArgumentException("ArtSize must match ArtData.Length and be at least 1.", nameof(article));
        }

        lock (_gate)
        {
            if (_completed || article.ArtSize > _maxQueuedBytes || _queuedBytes > _maxQueuedBytes - article.ArtSize)
            {
                return false;
            }

            if (!_channel.Writer.TryWrite(article))
            {
                return false;
            }

            _enqueued++;
            _queuedBytes += article.ArtSize;
            _stamps.Enqueue(_time.GetTimestamp());
            return true;
        }
    }

    /// <inheritdoc />
    public bool TryDequeue(out AcceptedArticleDelivery delivery)
    {
        if (!_channel.Reader.TryRead(out var article))
        {
            delivery = default;
            return false;
        }

        lock (_gate)
        {
            _dequeued++;
            _queuedBytes -= article.ArtSize;
            if (_stamps.Count > 0)
            {
                _stamps.Dequeue();
            }
        }

        delivery = new AcceptedArticleDelivery(true, article);
        return true;
    }

    /// <inheritdoc />
    public async ValueTask<AcceptedArticleDelivery> DequeueAsync(CancellationToken cancellationToken)
    {
        try
        {
            var article = await _channel.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                _dequeued++;
                _queuedBytes -= article.ArtSize;
                if (_stamps.Count > 0)
                {
                    _stamps.Dequeue();
                }
            }

            return new AcceptedArticleDelivery(true, article);
        }
        catch (ChannelClosedException)
        {
            return default;
        }
    }

    /// <inheritdoc />
    public void Complete()
    {
        lock (_gate)
        {
            _completed = true;
            _channel.Writer.TryComplete();
        }
    }
}
