namespace VectorNNTP.StandaloneTxPoc;

internal readonly record struct RunResult(
    double ElapsedMs,
    double GbitPerSecond,
    double MbitPerSecond,
    long WriteAsyncCount,
    double AverageWriteBytes,
    long SyncWrites,
    long AsyncWrites,
    long Bytes);
