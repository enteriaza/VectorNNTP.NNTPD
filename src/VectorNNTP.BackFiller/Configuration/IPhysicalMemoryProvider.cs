namespace VectorNNTP.BackFiller.Configuration;

/// <summary>
/// Supplies total physical memory for article-retention capacity validation.
/// </summary>
public interface IPhysicalMemoryProvider
{
    /// <summary>
    /// Returns total physical memory in bytes.
    /// </summary>
    /// <returns>Total physical memory in bytes.</returns>
    /// <exception cref="InvalidOperationException">Thrown when physical memory cannot be determined.</exception>
    long GetTotalPhysicalMemoryBytes();
}

/// <summary>
/// Resolves physical memory from <see cref="GC.GetGCMemoryInfo()"/>.
/// </summary>
public sealed class GcPhysicalMemoryProvider : IPhysicalMemoryProvider
{
    /// <inheritdoc />
    public long GetTotalPhysicalMemoryBytes()
    {
        var total = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        if (total <= 0)
        {
            throw new InvalidOperationException("Total physical memory could not be determined.");
        }

        return total;
    }
}
