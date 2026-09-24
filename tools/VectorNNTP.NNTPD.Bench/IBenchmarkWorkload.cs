namespace VectorNNTP.NNTPD.Bench;

/// <summary>
/// Selectable benchmark workload. Implementations own their execution, metrics, and output.
/// </summary>
internal interface IBenchmarkWorkload
{
    /// <summary>Stable CLI name (for example <c>BENCHIT</c>, <c>TAKETHIS</c>, <c>CHECK</c>, <c>IHAVE</c>, or <c>SPEEDTEST</c>).</summary>
    string Name { get; }

    /// <summary>Run the workload with the shared parsed options.</summary>
    Task<int> RunAsync(BenchOptions options);
}
