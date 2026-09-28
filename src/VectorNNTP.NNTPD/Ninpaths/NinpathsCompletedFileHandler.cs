using Microsoft.Extensions.Options;
using VectorNNTP.NNTPD.Configuration;
using VectorNNTP.NNTPD.Logging;

namespace VectorNNTP.NNTPD.Ninpaths;

/// <summary>
/// Rotation-hook handoff: opens the completed Path-survey file and enqueues it
/// for background ninpaths processing. Does not parse the file.
/// </summary>
/// <remarks>
/// The stream is opened with <see cref="FileShare.ReadWrite"/> and
/// <see cref="FileShare.Delete"/> so gzip and Serilog deletion can proceed
/// while the worker still reads. Failures are logged and never thrown to the
/// Serilog hook.
/// </remarks>
public sealed class NinpathsCompletedFileHandler : ICompletedPathSurveyFileHandler
{
    private readonly NinpathsProcessingService _processor;
    private readonly IOptions<NntpdOptions> _options;
    private readonly ILogger<NinpathsCompletedFileHandler> _logger;

    /// <summary>Initializes a handler that enqueues completed files onto <paramref name="processor"/>.</summary>
    public NinpathsCompletedFileHandler(
        NinpathsProcessingService processor,
        IOptions<NntpdOptions> options,
        ILogger<NinpathsCompletedFileHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(processor);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _processor = processor;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public void OnCompletedFile(string completedFilePath)
    {
        if (string.IsNullOrWhiteSpace(completedFilePath))
        {
            return;
        }

        if (!NinpathsTop1000.IsEnabled(_options.Value))
        {
            NinpathsLogMessages.Disabled(_logger, completedFilePath);
            return;
        }

        FileStream stream;
        try
        {
            stream = new FileStream(
                completedFilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                NinpathsConstants.ReadBufferSize,
                FileOptions.SequentialScan);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            NinpathsLogMessages.OpenFailed(_logger, ex, completedFilePath);
            return;
        }

        var file = new NinpathsCompletedFile(completedFilePath, stream);
        if (!_processor.TryEnqueue(file))
        {
            file.Dispose();
        }
    }
}
