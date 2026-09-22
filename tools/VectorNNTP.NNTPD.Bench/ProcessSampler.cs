using System.Diagnostics;

namespace VectorNNTP.NNTPD.Bench;

internal sealed class ProcessSampler
{
    private readonly Process _process;
    private readonly int _processorCount;
    private TimeSpan _cpuStart;
    private long _stampStart;

    private ProcessSampler(Process process)
    {
        _process = process;
        _processorCount = Environment.ProcessorCount;
    }

    public static ProcessSampler? TryStart(int pid)
    {
        try
        {
            var process = Process.GetProcessById(pid);
            return new ProcessSampler(process);
        }
        catch
        {
            return null;
        }
    }

    public void MarkMeasureStart()
    {
        _process.Refresh();
        _cpuStart = _process.TotalProcessorTime;
        _stampStart = Stopwatch.GetTimestamp();
    }

    public ProcessSample MarkMeasureEnd()
    {
        _process.Refresh();
        var cpuEnd = _process.TotalProcessorTime;
        var elapsed = Stopwatch.GetElapsedTime(_stampStart).TotalSeconds;
        var cpuDelta = (cpuEnd - _cpuStart).TotalSeconds;
        var cpuPercent = elapsed > 0 ? (cpuDelta / (elapsed * _processorCount)) * 100.0 : 0;
        return new ProcessSample
        {
            CpuPercent = cpuPercent,
            CpuTimeSeconds = cpuDelta,
            WorkingSetMb = _process.WorkingSet64 / (1024.0 * 1024.0),
            Gen0 = null,
            Gen1 = null,
            Gen2 = null,
        };
    }
}

internal sealed class ProcessSample
{
    public double CpuPercent { get; init; }
    public double CpuTimeSeconds { get; init; }
    public double WorkingSetMb { get; init; }
    public int? Gen0 { get; init; }
    public int? Gen1 { get; init; }
    public int? Gen2 { get; init; }
}
