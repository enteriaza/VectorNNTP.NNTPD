namespace VectorNNTP.NNTPD.Bench;

/// <summary>
/// Registry of selectable benchmark workloads. Add new workloads here; do not put
/// implementation logic in the CLI parser.
/// </summary>
internal static class BenchmarkWorkloadCatalog
{
    private static readonly IBenchmarkWorkload[] Workloads =
    [
        new BenchItWorkload(),
        new TakeThisWorkload(),
        new CheckWorkload(),
        new IhaveWorkload(),
    ];

    /// <summary>Registered workloads in registration order.</summary>
    public static IReadOnlyList<IBenchmarkWorkload> All => Workloads;

    /// <summary>Resolve a workload by CLI name (case-insensitive).</summary>
    public static IBenchmarkWorkload Resolve(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        foreach (var workload in Workloads)
        {
            if (string.Equals(workload.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return workload;
            }
        }

        var valid = string.Join(", ", Workloads.Select(static w => w.Name));
        throw new ArgumentException($"Unknown benchmark '{name}'. Valid values: {valid}.");
    }
}
