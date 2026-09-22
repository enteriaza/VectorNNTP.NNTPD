using System.Threading.Channels;
using Microsoft.Extensions.Options;
using VectorNNTP.NNTPD.Configuration;

namespace VectorNNTP.NNTPD.ArticleIngestion;

/// <summary>
/// Bounded <see cref="Channel{T}"/>-backed article ingestion queue.
/// </summary>
public sealed class ArticleIngestionQueue : IArticleIngestionQueue
{
    private readonly Channel<InboundArticle> _channel;
    private readonly int _capacity;
    private readonly int _maxArticleBytes;
    private int _count;
    private int _completed;

    /// <summary>Initializes a new instance of the <see cref="ArticleIngestionQueue"/> class.</summary>
    public ArticleIngestionQueue(IOptions<NntpdOptions> options)
        : this(GetIngestionOptions(options))
    {
    }

    /// <summary>Initializes a new instance with explicit ingestion options (tests).</summary>
    public ArticleIngestionQueue(ArticleIngestionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.QueueCapacity, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxArticleBytes, 1);

        _capacity = options.QueueCapacity;
        _maxArticleBytes = options.MaxArticleBytes;
        _channel = Channel.CreateBounded<InboundArticle>(new BoundedChannelOptions(_capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
    }

    /// <inheritdoc />
    public int Capacity => _capacity;

    /// <inheritdoc />
    public int MaxArticleBytes => _maxArticleBytes;

    /// <inheritdoc />
    public int Count => Math.Max(0, Volatile.Read(ref _count));

    /// <inheritdoc />
    public bool IsAccepting => Volatile.Read(ref _completed) == 0;

    /// <inheritdoc />
    public async ValueTask<ArticleEnqueueResult> EnqueueAsync(
        InboundArticle article,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(article);

        if (!IsAccepting)
        {
            return ArticleEnqueueResult.Unavailable;
        }

        try
        {
            await _channel.Writer.WriteAsync(article, cancellationToken).ConfigureAwait(false);
            Interlocked.Increment(ref _count);
            return ArticleEnqueueResult.Accepted;
        }
        catch (ChannelClosedException)
        {
            return ArticleEnqueueResult.Unavailable;
        }
        catch (OperationCanceledException)
        {
            return ArticleEnqueueResult.Unavailable;
        }
    }

    /// <inheritdoc />
    public bool TryEnqueue(InboundArticle article)
    {
        ArgumentNullException.ThrowIfNull(article);
        if (!IsAccepting || !_channel.Writer.TryWrite(article))
        {
            return false;
        }

        Interlocked.Increment(ref _count);
        return true;
    }

    /// <inheritdoc />
    public void Complete()
    {
        Interlocked.Exchange(ref _completed, 1);
        _channel.Writer.TryComplete();
    }

    /// <inheritdoc />
    public async ValueTask<InboundArticle?> DequeueAsync(CancellationToken cancellationToken)
    {
        while (await _channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (_channel.Reader.TryRead(out var article))
            {
                Interlocked.Decrement(ref _count);
                return article;
            }
        }

        return null;
    }

    private static ArticleIngestionOptions GetIngestionOptions(IOptions<NntpdOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.Value.ArticleIngestion ?? new ArticleIngestionOptions();
    }
}
