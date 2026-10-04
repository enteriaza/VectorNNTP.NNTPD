using System.IO.Compression;
using Serilog.Sinks.File;
using Serilog.Sinks.File.Archive;

namespace VectorNNTP.NNTPD.Logging;

/// <summary>
/// Public static hook factory for the File <c>hooks</c> argument.
/// </summary>
/// <remarks>
/// <see cref="ArchiveHooks"/> cannot be constructed from JSON value types. The File sink
/// <c>hooks</c> argument remains the type/member string
/// <c>VectorNNTP.NNTPD.Logging.NntpdSerilogHooks::DailyGzipFastest, VectorNNTP.NNTPD</c>.
/// The host logger maps that string to <see cref="DailyGzipFastest"/> directly.
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

    /// <summary>
    /// Chains a Path-survey completed-file handler before <see cref="DailyGzipFastest"/>.
    /// </summary>
    /// <param name="completedFileHandler">Receives the uncompressed rolled Path-survey file.</param>
    /// <param name="hookLogger">Logger for handler failures; gzip still runs after a failure.</param>
    /// <returns>
    /// A File lifecycle chain: completed-file handler, then gzip. Serilog deletes the
    /// uncompressed file after the chain returns. News logging continues to use
    /// <see cref="DailyGzipFastest"/> directly.
    /// </returns>
    public static FileLifecycleHooks CreatePathSurveyHooks(
        ICompletedPathSurveyFileHandler completedFileHandler,
        Microsoft.Extensions.Logging.ILogger hookLogger)
    {
        ArgumentNullException.ThrowIfNull(completedFileHandler);
        ArgumentNullException.ThrowIfNull(hookLogger);
        return new PathSurveyCompletedFileHook(completedFileHandler, hookLogger).Then(DailyGzipFastest);
    }
}
