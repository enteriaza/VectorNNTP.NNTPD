using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Running;

namespace VectorNNTP.NNTPD.PostAllocBench;

public static class Program
{
    public static int Main(string[] args)
    {
        var config = DefaultConfig.Instance.WithArtifactsPath(ResolveArtifactsDirectory());
        var summary = BenchmarkRunner.Run<PostStreamingBench>(config, args);
        return summary.HasCriticalValidationErrors ? 1 : 0;
    }

    /// <summary>
    /// Resolves <c>.artifacts/benchmarks/post-alloc-bench</c> from the solution root.
    /// Generated BenchmarkDotNet output must not land at repository root.
    /// </summary>
    internal static string ResolveArtifactsDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "VectorNNTP.NNTPD.sln")))
            {
                return Path.Combine(dir.FullName, ".artifacts", "benchmarks", "post-alloc-bench");
            }

            dir = dir.Parent;
        }

        return Path.GetFullPath(Path.Combine(".artifacts", "benchmarks", "post-alloc-bench"));
    }
}
