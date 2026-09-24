using System.Buffers;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Text;
using VectorNNTP.NNTPD.ArticleIngestion;
using VectorNNTP.NNTPD.Session.Framing;

namespace VectorNNTP.NNTPD.Bench;

/// <summary>
/// Forensic real-corpus IHAVE Pipe-reader measure. Not the <c>--benchmark IHAVE</c>
/// command workload. Mode A writes the whole article before the reader starts.
/// Mode B writes incrementally while the reader consumes. TAKETHIS production
/// code is not modified.
/// </summary>
internal static class IhaveReaderMeasure
{
    internal const int MaxArticleBytes = 8 * 1024 * 1024;

    internal static readonly byte[] TakeThisCommand = "TAKETHIS <ihave-corpus-bench@example.com>\r\n"u8.ToArray();

    public static async Task<IhaveCorpusRun> RunAsync(IhaveCorpusInventory inventory)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        WriteUsedFiles(inventory);

        var stats = IhaveCorpusStats.From(inventory);
        Console.WriteLine();
        Console.WriteLine(stats.Format());

        var rows = new List<IhaveMeasureRow>();
        rows.Add(await MeasureAsync(inventory, "IHAVE", ArrivalMode.Prebuffered, chunkBytes: 0).ConfigureAwait(false));
        foreach (var chunk in IhaveCorpusCatalog.StreamingChunkBytes)
        {
            rows.Add(await MeasureAsync(inventory, "IHAVE", ArrivalMode.Streaming, chunk).ConfigureAwait(false));
        }

        var takeThisPossible = true;
        try
        {
            rows.Add(await MeasureAsync(inventory, "TAKETHIS", ArrivalMode.Prebuffered, chunkBytes: 0).ConfigureAwait(false));
            foreach (var chunk in IhaveCorpusCatalog.StreamingChunkBytes)
            {
                rows.Add(await MeasureAsync(inventory, "TAKETHIS", ArrivalMode.Streaming, chunk).ConfigureAwait(false));
            }
        }
        catch (Exception ex)
        {
            takeThisPossible = false;
            Console.WriteLine();
            Console.WriteLine("TAKETHIS observational measure skipped: " + ex.Message);
        }

        return new IhaveCorpusRun(inventory, stats, rows, takeThisPossible);
    }

    private static async Task<IhaveMeasureRow> MeasureAsync(
        IhaveCorpusInventory inventory,
        string readerName,
        ArrivalMode mode,
        int chunkBytes)
    {
        var label = mode == ArrivalMode.Prebuffered
            ? $"{readerName} prebuffered"
            : $"{readerName} streaming {chunkBytes / 1024} KiB";
        Console.WriteLine();
        Console.WriteLine($"--- {label} ({inventory.Count} articles) ---");

        var acc = new MeasureAccumulator();
        var leftoverFailures = 0;
        var index = 0;
        foreach (var article in inventory.Articles)
        {
            index++;
            var stored = await File.ReadAllBytesAsync(article.FullPath).ConfigureAwait(false);
            var framed = IhaveCorpusCatalog.ToWireWithLeftover(stored);
            var payload = readerName == "TAKETHIS" ? PrefixTakeThis(framed) : framed;
            var articleWireBytes = framed.Length - IhaveCorpusCatalog.LeftoverCommand.Length;
            var observed = await ReadOneAsync(readerName, mode, chunkBytes, payload).ConfigureAwait(false);
            if (!observed.LeftoverIntact)
            {
                leftoverFailures++;
                throw new InvalidOperationException(
                    $"{label} leftover command was consumed for {article.RelativePath}: '{observed.Leftover}'");
            }

            acc.Add(article, articleWireBytes, observed);
            if (index % 1000 == 0 || index == inventory.Count)
            {
                Console.WriteLine($"  {index}/{inventory.Count}");
            }
        }

        var row = acc.ToRow(readerName, mode, chunkBytes, leftoverFailures);
        Console.WriteLine(
            $"{label}: reads={row.ReadAsyncPerArticle:F2} adv={row.AdvanceToPerArticle:F2} " +
            $"bulkOps={row.BulkOperationsPerArticle:F2} bulkB={row.BulkBytesPerArticle:F0} " +
            $"B/read={row.BytesPerReadAsync:F0} {row.MegabytesPerSecond:F1} MB/s {row.ArticlesPerSecond:F1} art/s");
        return row;
    }

    private static async Task<ObservedRead> ReadOneAsync(
        string readerName,
        ArrivalMode mode,
        int chunkBytes,
        byte[] payload)
    {
        var options = mode == ArrivalMode.Prebuffered
            ? IhaveCorpusProducer.PrebufferedOptions
            : IhaveCorpusProducer.StreamingOptions(chunkBytes > 0 ? chunkBytes : 64 * 1024);
        var pipe = new Pipe(options);
        var counting = new CountingPipeReader(pipe.Reader);
        var started = Stopwatch.GetTimestamp();

        IhaveArticleObservation observation;
        if (mode == ArrivalMode.Prebuffered)
        {
            await IhaveCorpusProducer.WriteAllAsync(pipe.Writer, payload).ConfigureAwait(false);
            observation = await InvokeReaderAsync(readerName, counting).ConfigureAwait(false);
        }
        else
        {
            var read = InvokeReaderAsync(readerName, counting);
            await Task.Yield();
            await IhaveCorpusProducer
                .WriteChunkedAsync(pipe.Writer, payload, chunkBytes)
                .ConfigureAwait(false);
            observation = await read.ConfigureAwait(false);
        }

        var elapsed = Stopwatch.GetElapsedTime(started);
        var readAsync = counting.ReadAsyncCount;
        var advanceTo = counting.AdvanceToCount;
        var inspections = counting.BufferInspections;
        await pipe.Writer.CompleteAsync().ConfigureAwait(false);
        var leftover = await ReadLeftoverAsync(pipe.Reader).ConfigureAwait(false);
        var leftoverIntact = leftover.StartsWith("DATE\r\n", StringComparison.Ordinal);
        return new ObservedRead(
            readAsync,
            advanceTo,
            inspections,
            observation,
            leftover,
            leftoverIntact,
            elapsed);
    }

    private static async Task<IhaveArticleObservation> InvokeReaderAsync(string readerName, PipeReader reader)
    {
        if (readerName == "IHAVE")
        {
            var result = await IHaveArticleReader
                .ReadAsync(reader, MaxArticleBytes, CancellationToken.None)
                .ConfigureAwait(false);
            if (result.Status != NntpMultilineReadStatus.Completed)
            {
                throw new InvalidOperationException($"IHAVE reader status {result.Status}");
            }

            return new IhaveArticleObservation(
                result.Metrics.PipeReads,
                LinesProcessed: 0,
                BulkBytes: result.Payload.Length,
                BulkOperations: 1,
                HeaderDerivedExpectedSize: -1,
                YencDerivedExpectedSize: -1,
                FallbackChunkReads: 0,
                result.Metrics.ArticleSize,
                HeaderBytes: -1,
                BodyBytes: result.Payload.Length,
                ReadMode: "RawWire",
                Type: ArticleType.None);
        }

        var parser = new NntpContinuousRxParser();
        var unit = await NntpContinuousRxReader
            .ReadUnitAsync(reader, parser, consumeTakeThisArticle: true, MaxArticleBytes, CancellationToken.None)
            .ConfigureAwait(false);
        if (unit.Kind != NntpContinuousRxKind.TakeThis || unit.Article.Status != NntpMultilineReadStatus.Completed)
        {
            throw new InvalidOperationException($"TAKETHIS observational status {unit.Kind}/{unit.Article.Status}");
        }

        return new IhaveArticleObservation(
            PipeReads: 0,
            LinesProcessed: 0,
            BulkBytes: unit.Article.Payload.Length,
            BulkOperations: 1,
            HeaderDerivedExpectedSize: -1,
            YencDerivedExpectedSize: -1,
            FallbackChunkReads: 0,
            ArticleSize: unit.Article.Payload.Length,
            HeaderBytes: -1,
            BodyBytes: unit.Article.Payload.Length,
            ReadMode: "TakeThisStreamWireCopy",
            Type: ArticleType.None);
    }

    private static async Task<string> ReadLeftoverAsync(PipeReader reader)
    {
        var result = await reader.ReadAsync().ConfigureAwait(false);
        var leftoverBytes = new byte[result.Buffer.Length];
        result.Buffer.CopyTo((Span<byte>)leftoverBytes);
        var text = Encoding.ASCII.GetString(leftoverBytes);
        reader.AdvanceTo(result.Buffer.End);
        return text;
    }

    private static byte[] PrefixTakeThis(byte[] framed)
    {
        var combined = new byte[TakeThisCommand.Length + framed.Length];
        TakeThisCommand.CopyTo(combined, 0);
        framed.CopyTo(combined.AsSpan(TakeThisCommand.Length));
        return combined;
    }

    private static void WriteUsedFiles(IhaveCorpusInventory inventory)
    {
        var dir = Path.Combine(FindArtifactsRoot(inventory.Root), "ihave-corpus-bench");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "used-files.txt");
        File.WriteAllLines(path, inventory.Articles.Select(static a => a.RelativePath));
        Console.WriteLine($"Used-file list: {path} ({inventory.Count} files, complete corpus)");
    }

    internal static string FindArtifactsRoot(string articlesRoot) =>
        Path.GetDirectoryName(articlesRoot)
        ?? throw new InvalidOperationException("Corpus root has no parent.");
}

internal enum ArrivalMode
{
    Prebuffered,
    Streaming,
}

internal readonly record struct IhaveArticleObservation(
    int PipeReads,
    int LinesProcessed,
    int BulkBytes,
    int BulkOperations,
    int HeaderDerivedExpectedSize,
    int YencDerivedExpectedSize,
    int FallbackChunkReads,
    int ArticleSize,
    int HeaderBytes,
    int BodyBytes,
    string ReadMode,
    ArticleType Type);

internal readonly record struct ObservedRead(
    int ReadAsyncCount,
    int AdvanceToCount,
    int BufferInspections,
    IhaveArticleObservation Observation,
    string Leftover,
    bool LeftoverIntact,
    TimeSpan Elapsed);

internal sealed class MeasureAccumulator
{
    private long _articles;
    private long _wireBytes;
    private long _fileBytes;
    private long _readAsync;
    private long _advanceTo;
    private long _inspections;
    private long _lines;
    private long _bulkOps;
    private long _bulkBytes;
    private long _copied;
    private long _productionPipeReads;
    private readonly Dictionary<string, TypeBucket> _types = new(StringComparer.Ordinal);
    private readonly List<YencSample> _yenc = [];
    private TimeSpan _elapsed;

    public void Add(IhaveCorpusArticle article, int wireBytes, ObservedRead observed)
    {
        _articles++;
        _wireBytes += wireBytes;
        _fileBytes += article.FileBytes;
        _readAsync += observed.ReadAsyncCount;
        _advanceTo += observed.AdvanceToCount;
        _inspections += observed.BufferInspections;
        _lines += observed.Observation.LinesProcessed;
        _bulkOps += observed.Observation.BulkOperations;
        _bulkBytes += observed.Observation.BulkBytes;
        _copied += observed.Observation.ArticleSize;
        _productionPipeReads += observed.Observation.PipeReads;
        _elapsed += observed.Elapsed;

        foreach (var name in TypeNames(article.Type))
        {
            if (!_types.TryGetValue(name, out var bucket))
            {
                bucket = new TypeBucket();
                _types[name] = bucket;
            }

            bucket.Add(article.FileBytes, wireBytes, observed);
        }

        if ((article.Type & ArticleType.YEncoded) != 0 || article.YencSize >= 0)
        {
            _yenc.Add(new YencSample(
                article.RelativePath,
                article.FileBytes,
                article.YencSize,
                article.HeaderBytes,
                observed.ReadAsyncCount,
                observed.Observation.BulkOperations,
                observed.Observation.BulkBytes,
                observed.Elapsed));
        }
    }

    public IhaveMeasureRow ToRow(string readerName, ArrivalMode mode, int chunkBytes, int leftoverFailures)
    {
        var seconds = Math.Max(_elapsed.TotalSeconds, 1e-9);
        return new IhaveMeasureRow(
            readerName,
            mode,
            chunkBytes,
            (int)_articles,
            _articles == 0 ? 0 : _fileBytes / (double)_articles,
            _articles == 0 ? 0 : _readAsync / (double)_articles,
            _articles == 0 ? 0 : _advanceTo / (double)_articles,
            _articles == 0 ? 0 : _inspections / (double)_articles,
            _articles == 0 ? 0 : _lines / (double)_articles,
            _articles == 0 ? 0 : _bulkOps / (double)_articles,
            _articles == 0 ? 0 : _bulkBytes / (double)_articles,
            _articles == 0 ? 0 : _copied / (double)_articles,
            _readAsync == 0 ? 0 : _wireBytes / (double)_readAsync,
            (_wireBytes / (1024.0 * 1024.0)) / seconds,
            _articles / seconds,
            leftoverFailures,
            _types.ToDictionary(static kv => kv.Key, static kv => kv.Value.ToRow(), StringComparer.Ordinal),
            _yenc);
    }

    private static IEnumerable<string> TypeNames(ArticleType type)
    {
        if ((type & ArticleType.YEncoded) != 0)
        {
            yield return "yEnc";
        }

        if ((type & ArticleType.Mime) != 0)
        {
            yield return "MIME";
        }

        if ((type & ArticleType.Base64) != 0)
        {
            yield return "BASE64";
        }

        if ((type & ArticleType.UuEncode) != 0)
        {
            yield return "UUENCODE";
        }

        if ((type & ArticleType.Binary) != 0)
        {
            yield return "binary";
        }

        if (type == ArticleType.Default || type == ArticleType.None)
        {
            yield return "text";
        }

        if ((type & ArticleType.Multipart) != 0)
        {
            yield return "multipart";
        }
    }
}

internal sealed class TypeBucket
{
    private long _articles;
    private long _fileBytes;
    private long _readAsync;
    private long _advanceTo;
    private long _lines;
    private long _bulkOps;
    private long _bulkBytes;
    private long _copied;
    private long _wire;
    private TimeSpan _elapsed;

    public void Add(int fileBytes, int wireBytes, ObservedRead observed)
    {
        _articles++;
        _fileBytes += fileBytes;
        _wire += wireBytes;
        _readAsync += observed.ReadAsyncCount;
        _advanceTo += observed.AdvanceToCount;
        _lines += observed.Observation.LinesProcessed;
        _bulkOps += observed.Observation.BulkOperations;
        _bulkBytes += observed.Observation.BulkBytes;
        _copied += observed.Observation.ArticleSize;
        _elapsed += observed.Elapsed;
    }

    public IhaveTypeRow ToRow()
    {
        var seconds = Math.Max(_elapsed.TotalSeconds, 1e-9);
        return new IhaveTypeRow(
            (int)_articles,
            _articles == 0 ? 0 : _fileBytes / (double)_articles,
            _articles == 0 ? 0 : _readAsync / (double)_articles,
            _articles == 0 ? 0 : _advanceTo / (double)_articles,
            _articles == 0 ? 0 : _lines / (double)_articles,
            _articles == 0 ? 0 : _bulkOps / (double)_articles,
            _articles == 0 ? 0 : _bulkBytes / (double)_articles,
            _readAsync == 0 ? 0 : _wire / (double)_readAsync,
            (_wire / (1024.0 * 1024.0)) / seconds,
            _articles / seconds);
    }
}

internal readonly record struct YencSample(
    string RelativePath,
    int ArticleSize,
    int DetectedYencSize,
    int HeaderToBody,
    int ReadAsyncCount,
    int BulkOperations,
    int BulkBytes,
    TimeSpan Elapsed);

internal readonly record struct IhaveTypeRow(
    int Articles,
    double AverageSize,
    double ReadAsyncPerArticle,
    double AdvanceToPerArticle,
    double LinesPerArticle,
    double BulkOperationsPerArticle,
    double BulkBytesPerArticle,
    double BytesPerReadAsync,
    double MegabytesPerSecond,
    double ArticlesPerSecond);

internal readonly record struct IhaveMeasureRow(
    string Reader,
    ArrivalMode Mode,
    int ChunkBytes,
    int Articles,
    double AverageSize,
    double ReadAsyncPerArticle,
    double AdvanceToPerArticle,
    double BufferInspectionsPerArticle,
    double LinesPerArticle,
    double BulkOperationsPerArticle,
    double BulkBytesPerArticle,
    double BytesCopiedPerArticle,
    double BytesPerReadAsync,
    double MegabytesPerSecond,
    double ArticlesPerSecond,
    int LeftoverFailures,
    IReadOnlyDictionary<string, IhaveTypeRow> Types,
    IReadOnlyList<YencSample> YencSamples);

internal sealed class IhaveCorpusRun
{
    public IhaveCorpusRun(
        IhaveCorpusInventory inventory,
        IhaveCorpusStats stats,
        IReadOnlyList<IhaveMeasureRow> rows,
        bool takeThisMeasured)
    {
        Inventory = inventory;
        Stats = stats;
        Rows = rows;
        TakeThisMeasured = takeThisMeasured;
    }

    public IhaveCorpusInventory Inventory { get; }

    public IhaveCorpusStats Stats { get; }

    public IReadOnlyList<IhaveMeasureRow> Rows { get; }

    public bool TakeThisMeasured { get; }
}
