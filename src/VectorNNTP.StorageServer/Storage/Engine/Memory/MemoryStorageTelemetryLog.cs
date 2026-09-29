namespace VectorNNTP.StorageServer.Storage.Engine.Memory;

/// <summary>In-memory append-only telemetry log.</summary>
public sealed class MemoryStorageTelemetryLog : IStorageTelemetryLog
{
    private readonly object _gate = new();
    private readonly List<byte[]> _records = [];

    /// <inheritdoc />
    public long RecordCount
    {
        get
        {
            lock (_gate)
            {
                return _records.Count;
            }
        }
    }

    /// <inheritdoc />
    public void Append(ReadOnlySpan<byte> record)
    {
        var copy = record.ToArray();
        lock (_gate)
        {
            _records.Add(copy);
        }
    }

    /// <summary>Returns a snapshot of appended records (tests).</summary>
    public IReadOnlyList<byte[]> Snapshot()
    {
        lock (_gate)
        {
            return _records.ToArray();
        }
    }
}
