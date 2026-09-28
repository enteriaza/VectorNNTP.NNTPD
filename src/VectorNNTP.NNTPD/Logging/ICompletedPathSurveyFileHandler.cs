namespace VectorNNTP.NNTPD.Logging;

/// <summary>
/// Receives a completed uncompressed Path-survey file before gzip and deletion.
/// </summary>
/// <remarks>
/// Serilog's File sink invokes file-lifecycle delete before gzip-archiving a
/// rolled uncompressed file. The Path-survey archive chain calls this handler
/// first, then gzip-archives the same path, then returns so Serilog can delete
/// the uncompressed original.
/// Production currently registers a no-op. Future ninpaths processing will
/// implement this interface; it is not implemented here.
/// </remarks>
public interface ICompletedPathSurveyFileHandler
{
    /// <summary>
    /// Called with the still-uncompressed completed Path-survey file.
    /// </summary>
    /// <param name="completedFilePath">Absolute or sink-supplied path of the rolled <c>inpaths</c> file.</param>
    /// <remarks>
    /// The file is readable and must not be deleted by the handler. Gzip and
    /// Serilog deletion run after this method returns. Implementations should
    /// not retain Path observations in memory beyond processing the file.
    /// </remarks>
    void OnCompletedFile(string completedFilePath);
}
