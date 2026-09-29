using System.IO.Compression;
using Serilog.Sinks.File.Archive;

namespace VectorNNTP.StorageServer.Logging;

/// <summary>
/// File lifecycle hooks for the StorageServer daily rolling log.
/// </summary>
public static class StorageServerSerilogHooks
{
    /// <summary>
    /// Gzip completed rolling files beside the active log.
    /// </summary>
    public static ArchiveHooks DailyGzipFastest { get; } = new(CompressionLevel.Fastest);
}
