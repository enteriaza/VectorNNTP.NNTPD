namespace VectorNNTP.StandaloneTxPoc;

internal static class Constants
{
    public const string DefaultHost = "198.18.0.66";
    public const int DefaultPort = 1199;
    public const int ChunkBytes = 64 * 1024;
    public const long DefaultTotalBytes = 64L * 1024 * 1024;
    public const int DefaultRuns = 5;
    public const long PauseWriterThreshold = 64 * 1024;
    public const long ResumeWriterThreshold = 32 * 1024;
    public const int MinimumSegmentSize = 4 * 1024;
}
