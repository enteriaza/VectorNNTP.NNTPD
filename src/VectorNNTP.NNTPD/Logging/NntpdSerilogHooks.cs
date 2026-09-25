using System.IO.Compression;
using Serilog.Sinks.File.Archive;

namespace VectorNNTP.NNTPD.Logging;

/// <summary>
/// Public static hook factory for <c>Serilog.Settings.Configuration</c> File <c>hooks</c>.
/// </summary>
/// <remarks>
/// <see cref="ArchiveHooks"/> cannot be constructed from JSON value types. The File sink
/// <c>hooks</c> argument is a type/member string:
/// <c>VectorNNTP.NNTPD.Logging.NntpdSerilogHooks::DailyGzipFastest, VectorNNTP.NNTPD</c>.
/// Compression is <see cref="CompressionLevel.Fastest"/> with no archive count limit so
/// historical <c>.gz</c> files are not deleted by this hook.
/// </remarks>
public static class NntpdSerilogHooks
{
    /// <summary>
    /// Gzip completed rolling files beside the active log. The File sink still deletes the
    /// uncompressed original after this hook returns.
    /// </summary>
    public static ArchiveHooks DailyGzipFastest { get; } = new(CompressionLevel.Fastest);
}
