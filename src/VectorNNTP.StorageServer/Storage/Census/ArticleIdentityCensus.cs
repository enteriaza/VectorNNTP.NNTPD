using VectorNNTP.Common.Articles;
using VectorNNTP.StorageServer.Storage.Engine;
using VectorNNTP.StorageServer.Storage.Engine.FileIndex;
using VectorNNTP.StorageServer.Storage.Engine.FileJournal;

namespace VectorNNTP.StorageServer.Storage.Census;

/// <summary>Metadata-only census state for one article on one StorageServer.</summary>
public enum ArticleCensusState : byte
{
    /// <summary>Index Present. Counts as a durable holder and is an OPEN source.</summary>
    Present = 1,

    /// <summary>Index Evicted. Not a holder. May later be a pinned repair target.</summary>
    Evicted = 2,

    /// <summary>Index Invalid. Not a holder. May later be a pinned repair target.</summary>
    Invalid = 3,

    /// <summary>
    /// Incomplete journal Accept. Counts as a durable holder and must not be an OPEN source.
    /// </summary>
    AcceptedPending = 4,
}

/// <summary>
/// One metadata census row. ArtData is never included.
/// </summary>
/// <param name="ArticleId">Canonical article identity.</param>
/// <param name="ArtHash">XXH3-64 of ArtData from the index or the incomplete Accept.</param>
/// <param name="ArtSize">ArtData length.</param>
/// <param name="State">Merged census state.</param>
/// <param name="HasPhysicalWritten">True when an incomplete Accept already has PhysicalWritten.</param>
/// <param name="ConflictingReservation">
/// True when incomplete sequences for this identity disagree on ArtHash or ArtSize.
/// The row is a holder and is not a clean repair target.
/// </param>
/// <param name="ServerId">StorageServer that produced the census.</param>
public readonly record struct ArticleCensusRow(
    ArticleId ArticleId,
    ulong ArtHash,
    int ArtSize,
    ArticleCensusState State,
    bool HasPhysicalWritten,
    bool ConflictingReservation,
    int ServerId)
{
    /// <summary>True when this server must count toward the two-copy maximum.</summary>
    public bool IsHolder =>
        State is ArticleCensusState.Present or ArticleCensusState.AcceptedPending;
}

/// <summary>One page of a frozen census projection.</summary>
/// <param name="PageIndex">Zero-based page index.</param>
/// <param name="TotalRows">Row count in the frozen projection.</param>
/// <param name="Rows">This page. Empty when <paramref name="PageIndex"/> is past the end.</param>
public readonly record struct ArticleCensusPage(
    int PageIndex,
    int TotalRows,
    IReadOnlyList<ArticleCensusRow> Rows);

/// <summary>
/// Frozen metadata census of one StorageServer. Paging reads only this projection.
/// </summary>
public sealed class ArticleIdentityCensus
{
    /// <summary>Default page size for <see cref="GetPage"/>.</summary>
    public const int DefaultPageSize = 256;

    private readonly ArticleCensusRow[] _rows;
    private readonly int _pageSize;
    private readonly Action<int>? _duringPage;

    private ArticleIdentityCensus(ArticleCensusRow[] rows, int pageSize, Action<int>? duringPage)
    {
        _rows = rows;
        _pageSize = pageSize;
        _duringPage = duringPage;
    }

    /// <summary>Gets the frozen row count.</summary>
    public int TotalRows => _rows.Length;

    /// <summary>Gets the number of pages at this census page size.</summary>
    public int PageCount => _rows.Length == 0 ? 0 : ((_rows.Length - 1) / _pageSize) + 1;

    /// <summary>
    /// Captures a metadata census. Index and journal locks are released before this method returns.
    /// The capture copies identity fields only and does not read journal ArtData.
    /// </summary>
    /// <param name="index">Durable article index.</param>
    /// <param name="journal">Durable article journal.</param>
    /// <param name="serverId">Identity stamped on every row.</param>
    /// <param name="pageSize">Page size. Values below 1 use <see cref="DefaultPageSize"/>.</param>
    /// <param name="betweenSamples">
    /// Invoked after the first index and journal sample and before the second. The index lock is not held.
    /// </param>
    /// <param name="duringPage">
    /// Invoked from <see cref="GetPage"/> before the page is copied out. The index lock is not held.
    /// </param>
    public static ArticleIdentityCensus Capture(
        FileArticleIndex index,
        FileArticleJournal journal,
        int serverId,
        int pageSize = DefaultPageSize,
        Action? betweenSamples = null,
        Action<int>? duringPage = null)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(journal);
        if (pageSize < 1)
        {
            pageSize = DefaultPageSize;
        }

        var merged = new Dictionary<ArticleId, MutableRow>();
        Absorb(merged, ReadJournal(journal), ReadIndex(index));
        betweenSamples?.Invoke();
        Absorb(merged, ReadJournal(journal), ReadIndex(index));

        var rows = new ArticleCensusRow[merged.Count];
        var indexRow = 0;
        foreach (var pair in merged)
        {
            rows[indexRow++] = pair.Value.ToRow(pair.Key, serverId);
        }

        Array.Sort(rows, static (left, right) =>
        {
            Span<byte> leftBytes = stackalloc byte[ArticleId.Length];
            Span<byte> rightBytes = stackalloc byte[ArticleId.Length];
            left.ArticleId.CopyTo(leftBytes);
            right.ArticleId.CopyTo(rightBytes);
            return leftBytes.SequenceCompareTo(rightBytes);
        });
        return new ArticleIdentityCensus(rows, pageSize, duringPage);
    }

    /// <summary>
    /// Copies one page from the frozen projection. Does not touch the index or the journal.
    /// </summary>
    public ArticleCensusPage GetPage(int pageIndex)
    {
        if (pageIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        }

        _duringPage?.Invoke(pageIndex);
        if (_rows.Length == 0 || pageIndex >= PageCount)
        {
            return new ArticleCensusPage(pageIndex, _rows.Length, []);
        }

        var start = pageIndex * _pageSize;
        var count = Math.Min(_pageSize, _rows.Length - start);
        var page = new ArticleCensusRow[count];
        Array.Copy(_rows, start, page, 0, count);
        return new ArticleCensusPage(pageIndex, _rows.Length, page);
    }

    private static void Absorb(
        Dictionary<ArticleId, MutableRow> merged,
        Dictionary<ArticleId, Reservation> journal,
        Dictionary<ArticleId, IndexObservation> index)
    {
        foreach (var pair in journal)
        {
            if (!merged.TryGetValue(pair.Key, out var row))
            {
                row = new MutableRow();
            }

            row.AddReservation(pair.Value);
            merged[pair.Key] = row;
        }

        foreach (var pair in index)
        {
            if (!merged.TryGetValue(pair.Key, out var row))
            {
                row = new MutableRow();
            }

            row.AddIndex(pair.Value);
            merged[pair.Key] = row;
        }
    }

    private static Dictionary<ArticleId, Reservation> ReadJournal(FileArticleJournal journal)
    {
        var incomplete = journal.CopyIncompleteIdentities();
        var map = new Dictionary<ArticleId, Reservation>();
        foreach (var accept in incomplete)
        {
            var observation = new Reservation(
                accept.ArtHash,
                accept.ArtSize,
                accept.HasPhysicalWritten,
                accept.Sequence);
            if (!map.TryGetValue(accept.ArtId, out var existing))
            {
                map[accept.ArtId] = observation;
                continue;
            }

            var conflict = existing.Conflict
                || existing.ArtHash != observation.ArtHash
                || existing.ArtSize != observation.ArtSize;
            var kept = existing.Sequence <= observation.Sequence ? existing : observation;
            map[accept.ArtId] = kept with
            {
                HasPhysicalWritten = existing.HasPhysicalWritten || observation.HasPhysicalWritten,
                Conflict = conflict,
            };
        }

        return map;
    }

    private static Dictionary<ArticleId, IndexObservation> ReadIndex(FileArticleIndex index)
    {
        var snapshot = index.CopyIdentities();
        var map = new Dictionary<ArticleId, IndexObservation>(snapshot.Length);
        foreach (var metadata in snapshot)
        {
            map[metadata.ArtId] = new IndexObservation(metadata.ArtHash, metadata.ArtSize, metadata.State);
        }

        return map;
    }

    private readonly record struct Reservation(
        ulong ArtHash,
        int ArtSize,
        bool HasPhysicalWritten,
        ulong Sequence,
        bool Conflict = false);

    private readonly record struct IndexObservation(ulong ArtHash, int ArtSize, ArticleStorageState State);

    private struct MutableRow
    {
        public bool Holder { get; private set; }

        public bool Pending { get; private set; }

        public bool Conflict { get; private set; }

        public bool HasPhysicalWritten { get; private set; }

        public bool HasPublished { get; private set; }

        public ArticleCensusState Published { get; private set; }

        public ulong ArtHash { get; private set; }

        public int ArtSize { get; private set; }

        public bool HashSet { get; private set; }

        public void AddReservation(Reservation reservation)
        {
            Holder = true;
            Pending = true;
            HasPhysicalWritten |= reservation.HasPhysicalWritten;
            if (!HashSet)
            {
                ArtHash = reservation.ArtHash;
                ArtSize = reservation.ArtSize;
                HashSet = true;
            }
            else if (ArtHash != reservation.ArtHash || ArtSize != reservation.ArtSize)
            {
                Conflict = true;
            }

            Conflict |= reservation.Conflict;
        }

        public void AddIndex(IndexObservation observation)
        {
            if (observation.State == ArticleStorageState.Present)
            {
                Holder = true;
                HasPublished = true;
                Published = ArticleCensusState.Present;
                ArtHash = observation.ArtHash;
                ArtSize = observation.ArtSize;
                HashSet = true;
                return;
            }

            if (Holder)
            {
                return;
            }

            HasPublished = true;
            Published = observation.State == ArticleStorageState.Invalid
                ? ArticleCensusState.Invalid
                : ArticleCensusState.Evicted;
            ArtHash = observation.ArtHash;
            ArtSize = observation.ArtSize;
            HashSet = true;
        }

        public ArticleCensusRow ToRow(ArticleId articleId, int serverId)
        {
            ArticleCensusState state;
            if (Conflict)
            {
                state = ArticleCensusState.AcceptedPending;
            }
            else if (HasPublished && Published == ArticleCensusState.Present)
            {
                state = ArticleCensusState.Present;
            }
            else if (Pending || Holder)
            {
                state = ArticleCensusState.AcceptedPending;
            }
            else
            {
                state = Published;
            }

            return new ArticleCensusRow(
                articleId,
                ArtHash,
                ArtSize,
                state,
                state == ArticleCensusState.AcceptedPending && HasPhysicalWritten,
                Conflict,
                serverId);
        }
    }
}
