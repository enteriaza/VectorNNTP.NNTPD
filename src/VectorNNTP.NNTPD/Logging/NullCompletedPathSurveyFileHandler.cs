namespace VectorNNTP.NNTPD.Logging;

/// <summary>
/// No-op completed-file handler. Path-survey files are still gzip-archived.
/// Future ninpaths processing replaces this registration.
/// </summary>
public sealed class NullCompletedPathSurveyFileHandler : ICompletedPathSurveyFileHandler
{
    /// <summary>Gets the shared no-op instance.</summary>
    public static NullCompletedPathSurveyFileHandler Instance { get; } = new();

    /// <inheritdoc />
    public void OnCompletedFile(string completedFilePath)
    {
    }
}
