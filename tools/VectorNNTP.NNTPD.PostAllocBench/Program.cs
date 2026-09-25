using BenchmarkDotNet.Running;

namespace VectorNNTP.NNTPD.PostAllocBench;

public static class Program
{
    public static int Main(string[] args)
    {
        var summary = BenchmarkRunner.Run<PostStreamingBench>(args: args);
        return summary.HasCriticalValidationErrors ? 1 : 0;
    }
}
