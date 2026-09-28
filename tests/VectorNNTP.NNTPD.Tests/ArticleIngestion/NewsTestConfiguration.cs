using Microsoft.Extensions.Configuration;
using VectorNNTP.NNTPD.Logging;

namespace VectorNNTP.NNTPD.Tests.ArticleIngestion;

internal static class NewsTestConfiguration
{
    public static IConfiguration Create(
        string directory,
        string rollingInterval = "Day",
        int retainedFileCountLimit = 14,
        bool buffered = false,
        string? hooks = null,
        long? fileSizeLimitBytes = null,
        bool rollOnFileSizeLimit = false,
        int bufferSize = 1000,
        bool blockWhenFull = true)
    {
        Directory.CreateDirectory(directory);
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Nntpd:LogDir"] = directory,
                [NntpdNewsLogging.SectionName + ":path"] = Path.Combine(directory, "news-.log"),
                [NntpdNewsLogging.SectionName + ":rollingInterval"] = rollingInterval,
                [NntpdNewsLogging.SectionName + ":retainedFileCountLimit"] = retainedFileCountLimit.ToString(),
                [NntpdNewsLogging.SectionName + ":buffered"] = buffered ? "true" : "false",
                [NntpdNewsLogging.SectionName + ":rollOnFileSizeLimit"] = rollOnFileSizeLimit ? "true" : "false",
                [NntpdNewsLogging.SectionName + ":fileSizeLimitBytes"] = fileSizeLimitBytes?.ToString(),
                [NntpdNewsLogging.SectionName + ":bufferSize"] = bufferSize.ToString(),
                [NntpdNewsLogging.SectionName + ":blockWhenFull"] = blockWhenFull ? "true" : "false",
                [NntpdNewsLogging.SectionName + ":hooks"] = hooks
                    ?? "VectorNNTP.NNTPD.Logging.NntpdSerilogHooks::DailyGzipFastest, VectorNNTP.NNTPD",
            })
            .Build();
    }

    public static string ReadNewsFile(string directory)
    {
        var files = Directory.GetFiles(directory, "news*")
            .Where(static path => !path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (files.Length == 0)
        {
            return string.Empty;
        }

        using var stream = new FileStream(files[0], FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
