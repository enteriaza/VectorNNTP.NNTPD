namespace VectorNNTP.NNTPD.Bench;

/// <summary>
/// Prepared NNTP wire articles from the established <c>.artifacts/Articles</c> catalog.
/// Files stay destuffed on disk; preparation restuffs leading dots and appends
/// <c>CRLF . CRLF</c>. The leftover <c>DATE</c> reader check is not applied.
/// </summary>
internal sealed class IhavePreparedArticles
{
    /// <summary>
    /// In-memory prepare budget. The complete catalog is discovered; only a prefix
    /// that fits is held so the duration bench does not load the full 7.8 GiB corpus.
    /// Articles cycle in catalog order.
    /// </summary>
    public const long DefaultMaxPreparedBytes = 256L * 1024 * 1024;

    private readonly byte[][] _articles;

    public IhavePreparedArticles(
        byte[][] articles,
        int inventoryCount,
        string root,
        long preparedBytes)
    {
        ArgumentNullException.ThrowIfNull(articles);
        if (articles.Length == 0)
        {
            throw new ArgumentException("IHAVE corpus produced no prepared articles.", nameof(articles));
        }

        foreach (var article in articles)
        {
            ArgumentNullException.ThrowIfNull(article);
            if (article.Length < 5)
            {
                throw new ArgumentException("Prepared IHAVE article is shorter than the NNTP terminator.");
            }
        }

        _articles = articles;
        InventoryCount = inventoryCount;
        Root = root;
        PreparedBytes = preparedBytes;
    }

    public string Root { get; }

    public int InventoryCount { get; }

    public int PreparedCount => _articles.Length;

    public long PreparedBytes { get; }

    public byte[] this[int index] => _articles[index];

    public byte[] Next(ref int index)
    {
        if ((uint)index >= (uint)_articles.Length)
        {
            index = 0;
        }

        var article = _articles[index];
        index++;
        if (index >= _articles.Length)
        {
            index = 0;
        }

        return article;
    }

    public static IhavePreparedArticles FromWireArticles(params byte[][] articles) =>
        new(
            articles,
            inventoryCount: articles.Length,
            root: "(inline)",
            preparedBytes: articles.Sum(static a => (long)a.Length));

    public static IhavePreparedArticles FromInventory(
        IhaveCorpusInventory inventory,
        long maxPreparedBytes = DefaultMaxPreparedBytes)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPreparedBytes);
        if (inventory.Count == 0)
        {
            throw new InvalidOperationException($"IHAVE corpus is empty: {inventory.Root}");
        }

        var prepared = new List<byte[]>(capacity: 64);
        var total = 0L;
        foreach (var article in inventory.Articles)
        {
            var stored = File.ReadAllBytes(article.FullPath);
            var wire = IhaveCorpusCatalog.ToWireArticle(stored);
            if (prepared.Count > 0 && total + wire.Length > maxPreparedBytes)
            {
                break;
            }

            prepared.Add(wire);
            total += wire.Length;
        }

        return new IhavePreparedArticles(prepared.ToArray(), inventory.Count, inventory.Root, total);
    }
}
