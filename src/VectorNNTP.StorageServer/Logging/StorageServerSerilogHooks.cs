using System.IO.Compression;
using Serilog.Sinks.File.Archive;

namespace VectorNNTP.StorageServer.Logging;

/// <summary>
/// Public static hook factory for <c>Serilog.Settings.Configuration</c> File <c>hooks</c>.
/// </summary>
/// <remarks>
/// <see cref="ArchiveHooks"/> cannot be constructed from JSON value types. The File sink
/// <c>hooks</c> argument is a type/member string:
/// <c>VectorNNTP.StorageServer.Logging.StorageServerSerilogHooks::DailyGzipFastest, VectorNNTP.StorageServer</c>.
/// Compression is <see cref="CompressionLevel.Fastest"/> with no archive count limit so
/// historical <c>.gz</c> files are not deleted by this hook.
/// </remarks>
public static class StorageServerSerilogHooks
{
    /// <summary>
    /// Gzip completed rolling files beside the active log. The File sink still deletes the
    /// uncompressed original after this hook returns.
    /// </summary>
    public static ArchiveHooks DailyGzipFastest { get; } = new(CompressionLevel.Fastest);
}
