namespace VectorNNTP.StorageSmoke;

/// <summary>CLI bounds and the lab defaults printed when an option is omitted.</summary>
internal static class SmokeLimits
{
    internal const string DefaultHost = "cache01.usenet.ninja";
    internal const int DefaultPort = 1191;
    internal const int DefaultSessions = 4;
    internal const int DefaultOutstandingStores = 2;
    internal const int DefaultBodyBytes = 768_000;
    internal const int DefaultSmokeArticles = 4;
    internal const int SampleIntervalSeconds = 10;

    internal const int MinSeconds = 1;
    internal const int MaxSeconds = 21_600;
    internal const int MinArticles = 1;
    internal const int MaxArticles = 10_000;
    internal const int MinSessions = 1;
    internal const int MaxSessions = 16;
    internal const int MinBodyBytes = 64;
    internal const int MaxBodyBytes = 2_000_000;

    internal const string SourcePath = "peer.example";
    internal const string TrackerHost = "news.usenet.ninja";
    internal const string PreIngestionPath = TrackerHost + "!" + SourcePath;
}
