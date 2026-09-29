using System.ComponentModel.DataAnnotations;

namespace VectorNNTP.StorageServer.Configuration;

/// <summary>
/// Bindable process-local article memory-cache options under
/// <c>StorageServer:Storage:ArticleCache</c>.
/// </summary>
/// <remarks>
/// Pure RAM; never durable. <see cref="MaxBytes"/> of <c>0</c> disables the cache
/// (Gets miss; Puts do not retain). Negative values are rejected by validation.
/// </remarks>
public sealed class ArticleMemoryCacheOptions
{
    /// <summary>Default maximum cached ArtData bytes (<c>0</c> = disabled).</summary>
    public const long DefaultMaxBytes = 0;

    /// <summary>
    /// Gets or sets the maximum ArtData bytes retained by the process-local cache.
    /// </summary>
    /// <remarks>
    /// Accounting uses canonical ArtData length only (not CLR object overhead).
    /// <c>0</c> disables caching. Must not be negative.
    /// </remarks>
    [Range(0, long.MaxValue)]
    public long MaxBytes { get; set; } = DefaultMaxBytes;
}
