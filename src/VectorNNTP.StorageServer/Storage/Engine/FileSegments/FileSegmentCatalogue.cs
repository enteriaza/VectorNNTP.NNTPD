namespace VectorNNTP.StorageServer.Storage.Engine.FileSegments;

/// <summary>
/// In-memory segment catalogue with Retired fencing, reconstructed from segment files on store open.
/// </summary>
/// <remarks>
/// <para>
/// Phase 2B does not introduce a durable catalogue database. Physical segment files under
/// CacheDir are authoritative for discovery and <see cref="SegmentInfo.SizeBytes"/>;
/// this catalogue tracks Live/Dead/Generation for the open store instance and persists
/// Retired via on-disk <c>.retired</c> rename.
/// </para>
/// <para>
/// On store open, discovery sets <c>LiveBytes = 0</c> and <c>DeadBytes = 0</c>.
/// The durable article storage engine then rebuilds Live/Dead from the article index
/// (Present → Live, Evicted/Invalid → Dead) using location Length, and clears
/// <see cref="SegmentInfo.ExtentAccountingComplete"/>. Live/Dead are process-local views
/// repaired from the authoritative index — they are not independently durable metrics.
/// A later closed-segment scan may replace DeadBytes and set the accounted bit.
/// </para>
/// </remarks>
public sealed class FileSegmentCatalogue : ISegmentCatalogue
{
    private readonly object _gate = new();
    private readonly Dictionary<ulong, SegmentInfo> _entries = new();
    private ulong _nextGeneration = 1;
    private Action<SegmentId, ulong, DateTimeOffset>? _onRetire;

    /// <summary>Installs an optional retire hook (rename segment file to .retired).</summary>
    internal void SetRetireHook(Action<SegmentId, ulong, DateTimeOffset>? onRetire) =>
        _onRetire = onRetire;

    /// <inheritdoc />
    public bool TryGet(SegmentId segmentId, out SegmentInfo info)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(segmentId.Value, out info);
        }
    }

    /// <inheritdoc />
    public void Upsert(in SegmentInfo info)
    {
        lock (_gate)
        {
            UpsertUnlocked(info);
        }
    }

    /// <inheritdoc />
    public bool TryRetire(SegmentId segmentId, ulong expectedGeneration, DateTimeOffset utcNow)
    {
        Action<SegmentId, ulong, DateTimeOffset>? hook;
        lock (_gate)
        {
            if (!_entries.TryGetValue(segmentId.Value, out var existing))
            {
                return false;
            }

            if (existing.State == SegmentState.Retired
                && existing.Generation == expectedGeneration)
            {
                return true;
            }

            if (existing.State != SegmentState.Closed || existing.Generation != expectedGeneration)
            {
                return false;
            }

            hook = _onRetire;
        }

        // Rename / physical transition first so a failed rename does not leave catalogue Retired.
        hook?.Invoke(segmentId, expectedGeneration, utcNow);

        lock (_gate)
        {
            if (!_entries.TryGetValue(segmentId.Value, out var existing))
            {
                return false;
            }

            if (existing.State == SegmentState.Retired
                && existing.Generation == expectedGeneration)
            {
                return true;
            }

            if (existing.State != SegmentState.Closed || existing.Generation != expectedGeneration)
            {
                return false;
            }

            _entries[segmentId.Value] = existing with
            {
                State = SegmentState.Retired,
                ClosedUtc = existing.ClosedUtc ?? utcNow,
            };
            return true;
        }
    }

    /// <summary>
    /// Removes a Closed catalogue entry after its physical <c>.closed</c> file has been deleted.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when the entry was removed or already absent;
    /// <see langword="false"/> when the entry exists but is not Closed.
    /// </returns>
    public bool TryRemoveClosed(SegmentId segmentId)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(segmentId.Value, out var existing))
            {
                return true;
            }

            if (existing.State != SegmentState.Closed)
            {
                return false;
            }

            return _entries.Remove(segmentId.Value);
        }
    }

    /// <summary>
    /// Removes a Retired catalogue entry after its physical <c>.retired</c> file has been deleted.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when the entry was removed or already absent;
    /// <see langword="false"/> when the entry exists but is not Retired.
    /// </returns>
    public bool TryRemoveRetired(SegmentId segmentId)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(segmentId.Value, out var existing))
            {
                return true;
            }

            if (existing.State != SegmentState.Retired)
            {
                return false;
            }

            return _entries.Remove(segmentId.Value);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<SegmentInfo> Snapshot()
    {
        lock (_gate)
        {
            return _entries.Values.OrderBy(static s => s.SegmentId.Value).ToArray();
        }
    }

    /// <summary>Allocates the next catalogue generation.</summary>
    public ulong AllocateGeneration()
    {
        lock (_gate)
        {
            return _nextGeneration++;
        }
    }

    /// <summary>
    /// Applies live/dead accounting without relocating bytes.
    /// Does not free physical holes — next append remains at EOF; SizeBytes unchanged.
    /// </summary>
    public void ApplyLiveDeadDelta(SegmentId segmentId, long liveDelta, long deadDelta)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(segmentId.Value, out var existing))
            {
                throw new InvalidOperationException($"Unknown segment {segmentId}.");
            }

            _entries[segmentId.Value] = existing with
            {
                LiveBytes = Math.Max(0L, existing.LiveBytes + liveDelta),
                DeadBytes = Math.Max(0L, existing.DeadBytes + deadDelta),
            };
        }
    }

    /// <summary>
    /// Rebuilds LiveBytes/DeadBytes from durable article index metadata.
    /// </summary>
    /// <remarks>
    /// Resets Live/Dead to zero for every catalogue entry and clears
    /// <see cref="SegmentInfo.ExtentAccountingComplete"/>, then accumulates
    /// <see cref="StoredArticleLocation.Length"/> for Present (live) and Evicted/Invalid (dead).
    /// SizeBytes, State, and Generation are unchanged. Unknown segment ids are ignored.
    /// </remarks>
    public void RebuildLiveDeadFromIndex(IEnumerable<StoredArticleMetadata> articles)
    {
        ArgumentNullException.ThrowIfNull(articles);
        lock (_gate)
        {
            foreach (var key in _entries.Keys.ToArray())
            {
                var existing = _entries[key];
                _entries[key] = existing with
                {
                    LiveBytes = 0,
                    DeadBytes = 0,
                    ExtentAccountingComplete = false,
                };
            }

            foreach (var article in articles)
            {
                var segmentKey = article.Location.SegmentId.Value;
                if (!_entries.TryGetValue(segmentKey, out var existing))
                {
                    continue;
                }

                var extent = article.Location.Length;
                if (extent <= 0)
                {
                    continue;
                }

                _entries[segmentKey] = article.State switch
                {
                    ArticleStorageState.Present => existing with
                    {
                        LiveBytes = existing.LiveBytes + extent,
                    },
                    ArticleStorageState.Evicted or ArticleStorageState.Invalid => existing with
                    {
                        DeadBytes = existing.DeadBytes + extent,
                    },
                    _ => existing,
                };
            }
        }
    }

    /// <summary>
    /// Runs <paramref name="action"/> while holding the catalogue lock used by live/dead mutations.
    /// </summary>
    internal void ExecuteLocked(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (_gate)
        {
            action();
        }
    }

    /// <summary>
    /// True when every Closed segment has completed historical extent accounting.
    /// Vacuous when no Closed segment exists. Retired and Active entries are ignored.
    /// </summary>
    internal bool AreAllClosedSegmentsAccounted()
    {
        lock (_gate)
        {
            foreach (var info in _entries.Values)
            {
                if (info.State == SegmentState.Closed && !info.ExtentAccountingComplete)
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>
    /// Replaces DeadBytes on a Closed segment and marks historical accounting complete.
    /// Refuses unless <c>SizeBytes == LiveBytes + deadBytes</c>. Does not change LiveBytes.
    /// </summary>
    /// <returns>True when this call published the accounted bit.</returns>
    internal bool TryCommitClosedExtentAccounting(SegmentId segmentId, long deadBytes)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(segmentId.Value, out var existing)
                || existing.State != SegmentState.Closed
                || existing.ExtentAccountingComplete
                || deadBytes < 0
                || existing.SizeBytes != existing.LiveBytes + deadBytes)
            {
                return false;
            }

            _entries[segmentId.Value] = existing with
            {
                DeadBytes = deadBytes,
                ExtentAccountingComplete = true,
            };
            return true;
        }
    }

    internal void ReplaceAll(IEnumerable<SegmentInfo> entries)
    {
        lock (_gate)
        {
            _entries.Clear();
            _nextGeneration = 1;
            foreach (var info in entries.OrderBy(static e => e.SegmentId.Value))
            {
                UpsertUnlocked(info);
            }
        }
    }

    internal void RecordAppend(SegmentId segmentId, long recordBytes, long sizeBytes)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(segmentId.Value, out var existing))
            {
                throw new InvalidOperationException($"Unknown segment {segmentId}.");
            }

            if (existing.State != SegmentState.Active)
            {
                throw new InvalidOperationException($"Segment {segmentId} is not Active.");
            }

            _entries[segmentId.Value] = existing with
            {
                SizeBytes = sizeBytes,
                LiveBytes = existing.LiveBytes + recordBytes,
            };
        }
    }

    private void UpsertUnlocked(in SegmentInfo info)
    {
        if (_entries.TryGetValue(info.SegmentId.Value, out var existing))
        {
            if (existing.State == SegmentState.Retired)
            {
                throw new InvalidOperationException(
                    $"Segment {info.SegmentId} is Retired and cannot be upserted (retirement is terminal).");
            }

            if (info.Generation < existing.Generation)
            {
                throw new InvalidOperationException(
                    $"Segment {info.SegmentId} generation regression is forbidden " +
                    $"({info.Generation} < {existing.Generation}).");
            }

            if (info.State == SegmentState.Retired
                && existing.State is not (SegmentState.Closed or SegmentState.Retired))
            {
                throw new InvalidOperationException(
                    $"Segment {info.SegmentId} must be Closed before Upsert to Retired; use TryRetire.");
            }
        }

        _entries[info.SegmentId.Value] = info;
        if (info.Generation >= _nextGeneration)
        {
            _nextGeneration = info.Generation + 1;
        }
    }
}
