namespace VectorNNTP.StorageSmoke;

/// <summary>Refuses to reuse a results directory that already belongs to a run.</summary>
internal static class ResultsLayout
{
    internal const string ClientCsv = "client.csv";
    internal const string RunMetadata = "run.txt";
    internal const string StopRequest = "stop.request";
    internal const string ClientPid = "client.pid";
    internal const string FailuresLog = "failures.log";

    internal const string ClientCsvHeader =
        "utc,offered,accepted,duplicate,rejected,failed,ambiguous,cancelled,acceptedBytes";

    internal static int EnsureFresh(string resultsDirectory, TextWriter error)
    {
        if (File.Exists(resultsDirectory))
        {
            error.WriteLine("results path is a file, not a directory.");
            return 2;
        }

        Directory.CreateDirectory(resultsDirectory);
        foreach (var name in new[] { ClientCsv, RunMetadata, StopRequest })
        {
            if (File.Exists(Path.Combine(resultsDirectory, name)))
            {
                error.WriteLine($"results directory already contains {name}. Choose a new directory.");
                return 2;
            }
        }

        return 0;
    }
}
