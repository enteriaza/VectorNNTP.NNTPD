using System.IO.Compression;
using Serilog.Sinks.File.Archive;

namespace VectorNNTP.BackFiller.Logging;

/// <summary>
/// File lifecycle hooks for the BackFiller daily rolling log.
/// </summary>
/// <remarks>
/// Registered by direct reference from <see cref="BackFillerFileLogging.ConfigureLogger"/>
/// (not via <c>Serilog.Settings.Configuration</c> type/member strings). Compression is
/// <see cref="CompressionLevel.Fastest"/> with no archive count limit so historical
/// <c>.gz</c> files are not deleted by this hook.
/// </remarks>
public static class BackFillerSerilogHooks
{
    /// <summary>
    /// Gzip completed rolling files beside the active log. The File sink still deletes the
    /// uncompressed original after this hook returns.
    /// </summary>
    public static ArchiveHooks DailyGzipFastest { get; } = new(CompressionLevel.Fastest);
}
