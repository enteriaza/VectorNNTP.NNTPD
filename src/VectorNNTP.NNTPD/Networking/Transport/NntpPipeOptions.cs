using System.IO.Pipelines;

namespace VectorNNTP.NNTPD.Networking.Transport;

/// <summary>
/// Centralized <see cref="PipeOptions"/> defaults for NNTP connection transports.
/// </summary>
/// <remarks>
/// Per-connection thresholds (not global). Values are engineering defaults for bounded
/// memory under many concurrent clients; they are not a claim about 10/40 Gbps throughput.
/// </remarks>
public static class NntpPipeOptions
{
    /// <summary>Pause the pipe writer (and thus receive) when unread bytes reach this size.</summary>
    public const long PauseWriterThreshold = 64 * 1024;

    /// <summary>Resume the pipe writer after unread bytes fall to this size.</summary>
    public const long ResumeWriterThreshold = 32 * 1024;

    /// <summary>Minimum pooled segment size for pipe buffers.</summary>
    public const int MinimumSegmentSize = 4 * 1024;

    /// <summary>Creates reader/writer options for one side of a duplex connection transport.</summary>
    public static PipeOptions Create() =>
        new(
            pool: null,
            readerScheduler: PipeScheduler.ThreadPool,
            writerScheduler: PipeScheduler.ThreadPool,
            pauseWriterThreshold: PauseWriterThreshold,
            resumeWriterThreshold: ResumeWriterThreshold,
            minimumSegmentSize: MinimumSegmentSize,
            useSynchronizationContext: false);
}
