using Microsoft.Extensions.Configuration;

namespace VectorNNTP.StorageServer.Configuration;

/// <summary>
/// Dedicated segment-pool directories and occupancy thresholds reserved for a future control loop.
/// Thresholds are validated at startup and are not enforced by eviction, acceptance, or advertisements.
/// </summary>
public sealed class SegmentPoolsOptions
{
    /// <summary>
    /// Occupancy percent at or above which a future control loop would stop accepting articles
    /// and withdraw the server. Not enforced in this phase.
    /// </summary>
    public int CriticalPercent { get; set; } = 85;

    /// <summary>
    /// Occupancy percent at or above which a future eviction loop would start. Not enforced in this phase.
    /// </summary>
    public int HighPercent { get; set; } = 79;

    /// <summary>
    /// Occupancy percent at which a future eviction loop would stop. Not enforced in this phase.
    /// </summary>
    public int LowPercent { get; set; } = 75;

    /// <summary>
    /// Pool directories. Relative entries resolve against <see cref="AppContext.BaseDirectory"/>.
    /// Each directory must be a dedicated mount on its own physical device before the host serves traffic.
    /// The configuration binder appends to a non-empty array, so
    /// <see cref="ReplaceConfiguredDirectories"/> replaces this value when the configuration section exists.
    /// An omitted section keeps this default. An explicit empty section stays empty and fails validation.
    /// </summary>
    public string[] Directories { get; set; } = [DefaultDirectory];

    /// <summary>Default pool directory used when <c>StorageServer:SegmentPools:Directories</c> is omitted.</summary>
    public const string DefaultDirectory = "spool/cache";

    /// <summary>
    /// Replaces <see cref="Directories"/> with the configured children when that section exists.
    /// </summary>
    /// <param name="options">Options already bound from configuration.</param>
    /// <param name="configuration">Application configuration.</param>
    internal static void ReplaceConfiguredDirectories(StorageServerOptions options, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetSection(StorageServerOptions.SectionName + ":SegmentPools:Directories");
        if (!section.Exists())
        {
            return;
        }

        options.SegmentPools ??= new SegmentPoolsOptions();
        options.SegmentPools.Directories = section.GetChildren()
            .OrderBy(static child => int.TryParse(child.Key, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var index) ? index : int.MaxValue)
            .Select(static child => child.Value ?? string.Empty)
            .ToArray();
    }
}
