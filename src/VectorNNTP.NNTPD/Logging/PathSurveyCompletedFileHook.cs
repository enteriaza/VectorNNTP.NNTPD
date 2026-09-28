using Serilog.Sinks.File;

namespace VectorNNTP.NNTPD.Logging;

/// <summary>
/// Invokes <see cref="ICompletedPathSurveyFileHandler"/> when Serilog is about
/// to delete a rolled uncompressed Path-survey file.
/// </summary>
/// <remarks>
/// This hook does not gzip or delete the file. Chain it before
/// <see cref="NntpdSerilogHooks.DailyGzipFastest"/> so ninpaths can open the
/// uncompressed file, then gzip runs, then Serilog deletes the original.
/// Handler failures are logged and do not skip gzip.
/// </remarks>
internal sealed class PathSurveyCompletedFileHook : FileLifecycleHooks
{
    private readonly ICompletedPathSurveyFileHandler _handler;
    private readonly Microsoft.Extensions.Logging.ILogger _logger;

    /// <summary>Initializes a hook that forwards completed files to <paramref name="handler"/>.</summary>
    public PathSurveyCompletedFileHook(ICompletedPathSurveyFileHandler handler, Microsoft.Extensions.Logging.ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(logger);
        _handler = handler;
        _logger = logger;
    }

    /// <inheritdoc />
    public override void OnFileDeleting(string path)
    {
        try
        {
            _handler.OnCompletedFile(path);
        }
        catch (Exception ex)
        {
            PathSurveyLogMessages.CompletedFileHandlerFailed(_logger, ex, path);
        }
    }
}
