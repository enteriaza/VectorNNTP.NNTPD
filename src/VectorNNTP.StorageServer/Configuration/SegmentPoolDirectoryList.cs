using System.Collections.ObjectModel;

namespace VectorNNTP.StorageServer.Configuration;

/// <summary>
/// Spool directories for <see cref="SegmentPoolOptions"/>.
/// </summary>
/// <remarks>
/// A newly constructed list contains one default spool. Configuration binding adds entries
/// instead of replacing a pre-seeded list, so the first inserted directory removes that
/// unbound default. Later inserts append. Callers that want the default plus more directories
/// clear the list and add every directory they want to keep.
/// </remarks>
public sealed class SegmentPoolDirectoryList : Collection<SegmentPoolDirectoryOptions>
{
    private bool _replaceUnboundDefault = true;

    /// <summary>Creates the list with one default spool.</summary>
    public SegmentPoolDirectoryList()
    {
        base.InsertItem(0, new SegmentPoolDirectoryOptions());
    }

    /// <summary>Inserts <paramref name="item"/>, replacing the unbound default on the first insert.</summary>
    /// <param name="index">Index requested by the caller. The first insert is placed at 0.</param>
    /// <param name="item">The spool directory.</param>
    protected override void InsertItem(int index, SegmentPoolDirectoryOptions item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (_replaceUnboundDefault)
        {
            _replaceUnboundDefault = false;
            ClearItems();
            index = 0;
        }

        base.InsertItem(index, item);
    }
}
