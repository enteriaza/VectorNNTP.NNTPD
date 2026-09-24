using System.Globalization;
using System.Text;
using VectorNNTP.NNTPD.ArticleIngestion;

namespace VectorNNTP.NNTPD.Bench;

internal sealed class IhaveCorpusStats
{
    private static readonly (string Name, long Lo, long Hi)[] Buckets =
    [
        ("< 10 KiB", 0, 10L * 1024),
        ("10–100 KiB", 10L * 1024, 100L * 1024),
        ("100–500 KiB", 100L * 1024, 500L * 1024),
        ("500 KiB–1 MiB", 500L * 1024, 1024L * 1024),
        ("1–2 MiB", 1024L * 1024, 2L * 1024 * 1024),
        ("2–5 MiB", 2L * 1024 * 1024, 5L * 1024 * 1024),
        ("> 5 MiB", 5L * 1024 * 1024, long.MaxValue),
    ];

    private IhaveCorpusStats()
    {
    }

    public required string Root { get; init; }

    public required int Articles { get; init; }

    public required long TotalBytes { get; init; }

    public required double Average { get; init; }

    public required double Median { get; init; }

    public required double P50 { get; init; }

    public required double P90 { get; init; }

    public required double P95 { get; init; }

    public required double P99 { get; init; }

    public required int Min { get; init; }

    public required int Max { get; init; }

    public required IReadOnlyDictionary<string, TypeCount> Types { get; init; }

    public required IReadOnlyList<BucketRow> SizeBuckets { get; init; }

    public required int ContentLengthCount { get; init; }

    public required int YencSizeCount { get; init; }

    public required int BytesHeaderCount { get; init; }

    public static IhaveCorpusStats From(IhaveCorpusInventory inventory)
    {
        var sizes = inventory.Articles.Select(static a => a.FileBytes).OrderBy(static s => s).ToArray();
        var n = sizes.Length;
        var total = sizes.Sum(static s => (long)s);
        var types = new Dictionary<string, TypeCount>(StringComparer.Ordinal)
        {
            ["yEnc"] = CountFlag(inventory, ArticleType.YEncoded),
            ["MIME"] = CountFlag(inventory, ArticleType.Mime),
            ["BASE64"] = CountFlag(inventory, ArticleType.Base64),
            ["UUENCODE"] = CountFlag(inventory, ArticleType.UuEncode),
            ["binary"] = CountFlag(inventory, ArticleType.Binary),
            ["text"] = CountExact(inventory, ArticleType.Default) + CountExact(inventory, ArticleType.None),
            ["multipart"] = CountFlag(inventory, ArticleType.Multipart),
        };

        var buckets = new List<BucketRow>();
        foreach (var (name, lo, hi) in Buckets)
        {
            var matches = inventory.Articles.Where(a => a.FileBytes >= lo && a.FileBytes < hi).ToArray();
            var bytes = matches.Sum(static a => (long)a.FileBytes);
            buckets.Add(new BucketRow(
                name,
                matches.Length,
                n == 0 ? 0 : 100.0 * matches.Length / n,
                bytes,
                matches.Length == 0 ? 0 : bytes / (double)matches.Length));
        }

        return new IhaveCorpusStats
        {
            Root = inventory.Root,
            Articles = n,
            TotalBytes = total,
            Average = n == 0 ? 0 : total / (double)n,
            Median = Percentile(sizes, 50),
            P50 = Percentile(sizes, 50),
            P90 = Percentile(sizes, 90),
            P95 = Percentile(sizes, 95),
            P99 = Percentile(sizes, 99),
            Min = n == 0 ? 0 : sizes[0],
            Max = n == 0 ? 0 : sizes[^1],
            Types = types,
            SizeBuckets = buckets,
            ContentLengthCount = inventory.Articles.Count(static a => a.ContentLength >= 0),
            YencSizeCount = inventory.Articles.Count(static a => a.YencSize >= 0),
            BytesHeaderCount = inventory.Articles.Count(static a => a.BytesHeader >= 0),
        };
    }

    public string Format()
    {
        var sb = new StringBuilder();
        sb.AppendLine("CORPUS");
        sb.AppendLine($"  root           {Root}");
        sb.AppendLine($"  articles       {Articles}");
        sb.AppendLine($"  total bytes    {TotalBytes}");
        sb.AppendLine($"  average        {Average:F1}");
        sb.AppendLine($"  median / p50   {Median:F1}");
        sb.AppendLine($"  p90            {P90:F1}");
        sb.AppendLine($"  p95            {P95:F1}");
        sb.AppendLine($"  p99            {P99:F1}");
        sb.AppendLine($"  min / max      {Min} / {Max}");
        sb.AppendLine($"  Content-Length {ContentLengthCount}");
        sb.AppendLine($"  yEnc size=     {YencSizeCount}");
        sb.AppendLine($"  Bytes: header  {BytesHeaderCount}");
        sb.AppendLine("  types:");
        foreach (var (name, count) in Types)
        {
            var pct = Articles == 0 ? 0 : 100.0 * count / Articles;
            sb.AppendLine($"    {name,-10} {count,6}  {pct,6:F2}%");
        }

        sb.AppendLine("  size buckets:");
        foreach (var bucket in SizeBuckets)
        {
            sb.AppendLine(
                $"    {bucket.Name,-14} n={bucket.Count,5} ({bucket.Percent,6:F2}%)  bytes={bucket.TotalBytes}  avg={bucket.Average:F0}");
        }

        return sb.ToString();
    }

    private static TypeCount CountFlag(IhaveCorpusInventory inventory, ArticleType flag) =>
        inventory.Articles.Count(a => (a.Type & flag) != 0);

    private static TypeCount CountExact(IhaveCorpusInventory inventory, ArticleType exact) =>
        inventory.Articles.Count(a => a.Type == exact);

    private static double Percentile(int[] sorted, double p)
    {
        if (sorted.Length == 0)
        {
            return 0;
        }

        if (sorted.Length == 1)
        {
            return sorted[0];
        }

        var k = (sorted.Length - 1) * (p / 100.0);
        var f = (int)k;
        var c = Math.Min(f + 1, sorted.Length - 1);
        if (f == c)
        {
            return sorted[f];
        }

        return sorted[f] + ((sorted[c] - sorted[f]) * (k - f));
    }

    internal readonly record struct BucketRow(string Name, int Count, double Percent, long TotalBytes, double Average);

    internal readonly record struct TypeCount(int Count)
    {
        public static implicit operator TypeCount(int count) => new(count);

        public static implicit operator int(TypeCount count) => count.Count;

        public override string ToString() => Count.ToString(CultureInfo.InvariantCulture);
    }
}
