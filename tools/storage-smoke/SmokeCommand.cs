namespace VectorNNTP.StorageSmoke;

/// <summary>Resolved smoke or soak invocation. Defaults are already applied.</summary>
internal sealed record SmokeCommand(
    SmokeMode Mode,
    int Seconds,
    int Articles,
    int Sessions,
    int OutstandingStores,
    int BodyBytes,
    string ResultsDirectory,
    string Host,
    int Port);

/// <summary>Which bound the run uses.</summary>
internal enum SmokeMode
{
    /// <summary>Stop after a fixed number of offered articles.</summary>
    Smoke = 1,

    /// <summary>Stop at a UTC deadline.</summary>
    Soak = 2,
}
