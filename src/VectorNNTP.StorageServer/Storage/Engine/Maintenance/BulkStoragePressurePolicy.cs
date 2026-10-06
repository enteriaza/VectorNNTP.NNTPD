using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Storage;

namespace VectorNNTP.StorageServer.Storage.Engine.Maintenance;

/// <summary>
/// Filesystem used-space class for the cache volume. Distinct from
/// <see cref="StorageWritePressure"/>, which classifies outstanding recoverable journal bytes.
/// </summary>
public enum BulkStoragePressureState : byte
{
    /// <summary>Used space is below the Warning watermark.</summary>
    Normal = 0,

    /// <summary>Used space is at or above Warning and below Pressure.</summary>
    Warning = 1,

    /// <summary>Used space is at or above Pressure and below High.</summary>
    Pressure = 2,

    /// <summary>Used space is at or above High and below Critical.</summary>
    High = 3,

    /// <summary>Used space is at or above Critical and below Emergency.</summary>
    Critical = 4,

    /// <summary>Used space is at or above Emergency.</summary>
    Emergency = 5,
}

/// <summary>
/// Deterministic bulk-storage classification for one total/used/free observation.
/// </summary>
/// <param name="TotalBytes">Volume total bytes. Zero when the volume was not measured.</param>
/// <param name="FreeBytes">Volume free bytes, clamped at zero.</param>
/// <param name="UsedBytes">Volume used bytes, clamped at zero.</param>
/// <param name="UsedPercent">Truncated <c>UsedBytes / TotalBytes</c> percent, from 0 to 100.</param>
/// <param name="FreePercent">Truncated <c>FreeBytes / TotalBytes</c> percent, from 0 to 100.</param>
/// <param name="State">Watermark class. <see cref="BulkStoragePressureState.Normal"/> when unmeasured.</param>
/// <param name="NormalMaintenanceSufficient">
/// True for <see cref="BulkStoragePressureState.Normal"/> and
/// <see cref="BulkStoragePressureState.Warning"/>. Age retention is not accelerated.
/// </param>
/// <param name="AccelerateReclamation">
/// True from <see cref="BulkStoragePressureState.Pressure"/> upward. Maintenance prefers
/// reclaimable bytes over a new rewrite. A separate pass may mark old Present articles
/// Evicted. This flag does not delete segment bytes.
/// </param>
/// <param name="RewriteAllowed">
/// State-level permission for a new rewrite, before a specific segment's live bytes are
/// considered. False for <see cref="BulkStoragePressureState.Emergency"/> and when High or
/// Critical already has the configured reserves exhausted.
/// </param>
/// <param name="EmergencyAdmissionProtectionRequired">
/// True only for <see cref="BulkStoragePressureState.Emergency"/>. Accept does not read this
/// flag. Usage-pressure recovery refuses new rewrites and logical eviction while it is set,
/// so the existing utilization ceiling rejects a new accept when recovery cannot free space.
/// </param>
/// <param name="OperationalReserveBytes">Operational reserve computed from total bytes.</param>
/// <param name="RecoveryReserveBytes">Recovery reserve computed from total bytes.</param>
/// <param name="RewriteReserveBytes">Rewrite/reclamation reserve computed from total bytes.</param>
/// <param name="AvailableReserveBytes">
/// Free bytes minus the three reserves, floored at zero. This is not allocated storage.
/// </param>
/// <param name="ReserveExhausted">True when free bytes do not cover the three reserves.</param>
/// <param name="MaintenanceMode">Stable name of the maintenance posture for this state.</param>
/// <param name="Measured">False when <paramref name="TotalBytes"/> was not positive.</param>
public readonly record struct BulkStoragePressureEvaluation(
    long TotalBytes,
    long FreeBytes,
    long UsedBytes,
    int UsedPercent,
    int FreePercent,
    BulkStoragePressureState State,
    bool NormalMaintenanceSufficient,
    bool AccelerateReclamation,
    bool RewriteAllowed,
    bool EmergencyAdmissionProtectionRequired,
    long OperationalReserveBytes,
    long RecoveryReserveBytes,
    long RewriteReserveBytes,
    long AvailableReserveBytes,
    bool ReserveExhausted,
    string MaintenanceMode,
    bool Measured);

/// <summary>
/// Classifies cache-volume used space and decides whether a new low-density rewrite may start.
/// </summary>
/// <remarks>
/// The evaluation is a pure function of byte counts and the configured percentages. It does not
/// read the filesystem, the journal, or the article index. A thin caller supplies one
/// <see cref="StorageCapacitySnapshot"/> per maintenance cycle.
/// </remarks>
public sealed class BulkStoragePressurePolicy
{
    /// <summary>Accept was rejected because the cache-volume recovery reserve would be consumed.</summary>
    public const string RecoveryReserveRejectionReason = "bulk-recovery-reserve";

    /// <summary>Accept was rejected because cache-volume capacity could not be measured.</summary>
    public const string UnmeasuredRejectionReason = "bulk-capacity-unmeasured";

    /// <summary>Accept was rejected because admission recovery failed before the journal ACK.</summary>
    public const string RecoveryFailedRejectionReason = "bulk-recovery-failed";

    /// <summary>Policy constructed from <see cref="BulkStoragePressureOptions"/> defaults.</summary>
    public static BulkStoragePressurePolicy Default { get; } = new();

    /// <summary>Creates a policy and rejects a ladder that startup validation would also reject.</summary>
    /// <param name="options">Watermarks and reserves. Null uses the defaults.</param>
    public BulkStoragePressurePolicy(BulkStoragePressureOptions? options = null)
    {
        Options = options ?? new BulkStoragePressureOptions();
        ValidateThresholds(Options);
    }

    /// <summary>Watermarks and reserves used by <see cref="Evaluate"/>.</summary>
    public BulkStoragePressureOptions Options { get; }

    /// <summary>
    /// Classifies <paramref name="usedBytes"/> against <paramref name="totalBytes"/>.
    /// </summary>
    /// <param name="totalBytes">Volume total. Non-positive yields an unmeasured Normal result.</param>
    /// <param name="usedBytes">Volume used bytes. Negative is treated as zero.</param>
    /// <param name="freeBytes">Volume free bytes. Negative is treated as zero.</param>
    /// <returns>The classification. Unmeasured input does not suppress maintenance.</returns>
    public BulkStoragePressureEvaluation Evaluate(long totalBytes, long usedBytes, long freeBytes)
    {
        if (totalBytes <= 0)
        {
            return Unmeasured();
        }

        var used = usedBytes < 0 ? 0 : usedBytes;
        var free = freeBytes < 0 ? 0 : freeBytes;
        var operational = ReserveBytes(totalBytes, Options.OperationalReservePercent);
        var recovery = ReserveBytes(totalBytes, Options.RecoveryReservePercent);
        var rewrite = ReserveBytes(totalBytes, Options.RewriteReservePercent);
        var reserveSum = SumReserves(operational, recovery, rewrite);
        var available = free <= reserveSum ? 0 : free - reserveSum;
        var exhausted = free < reserveSum || available == 0;
        var state = Classify(used, totalBytes);
        var rewriteAllowed = state switch
        {
            BulkStoragePressureState.Emergency => false,
            BulkStoragePressureState.High or BulkStoragePressureState.Critical => !exhausted,
            _ => true,
        };

        return new BulkStoragePressureEvaluation(
            TotalBytes: totalBytes,
            FreeBytes: free,
            UsedBytes: used,
            UsedPercent: PercentOf(used, totalBytes),
            FreePercent: PercentOf(free, totalBytes),
            State: state,
            NormalMaintenanceSufficient: state is BulkStoragePressureState.Normal
                or BulkStoragePressureState.Warning,
            AccelerateReclamation: state is BulkStoragePressureState.Pressure
                or BulkStoragePressureState.High
                or BulkStoragePressureState.Critical
                or BulkStoragePressureState.Emergency,
            RewriteAllowed: rewriteAllowed,
            EmergencyAdmissionProtectionRequired: state == BulkStoragePressureState.Emergency,
            OperationalReserveBytes: operational,
            RecoveryReserveBytes: recovery,
            RewriteReserveBytes: rewrite,
            AvailableReserveBytes: available,
            ReserveExhausted: exhausted,
            MaintenanceMode: ModeName(state),
            Measured: true);
    }

    /// <summary>
    /// True when a new Accept of <paramref name="segmentBytes"/> may proceed without consuming
    /// the recovery reserve. Normal, Warning, and Pressure do not apply this floor.
    /// </summary>
    /// <param name="evaluation">Current cache-volume classification.</param>
    /// <param name="segmentBytes">
    /// Segment-copy bytes for this article (<c>SegmentRecordCodec.RecordLengthForArtSize</c>).
    /// This is the future SATA copy. Journal outstanding bytes are not added again.
    /// </param>
    /// <param name="unwrittenSegmentBytes">
    /// Segment and compaction bytes already reserved on this volume and not yet in
    /// <see cref="BulkStoragePressureEvaluation.UsedBytes"/>.
    /// </param>
    /// <returns>
    /// False when the volume was not measured, or when High, Critical, or Emergency would
    /// leave free space below <see cref="BulkStoragePressureEvaluation.RecoveryReserveBytes"/>.
    /// </returns>
    public bool AllowsNewAccept(
        in BulkStoragePressureEvaluation evaluation,
        long segmentBytes,
        long unwrittenSegmentBytes)
    {
        if (!evaluation.Measured)
        {
            return false;
        }

        if (evaluation.State is BulkStoragePressureState.Normal
            or BulkStoragePressureState.Warning
            or BulkStoragePressureState.Pressure)
        {
            return true;
        }

        var extra = segmentBytes < 0 ? 0 : segmentBytes;
        var reserved = unwrittenSegmentBytes < 0 ? 0 : unwrittenSegmentBytes;
        try
        {
            var needed = checked(evaluation.RecoveryReserveBytes + reserved + extra);
            return evaluation.FreeBytes >= needed;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    /// <summary>
    /// Free bytes that would remain above the recovery reserve after
    /// <paramref name="segmentBytes"/> and <paramref name="unwrittenSegmentBytes"/>.
    /// Negative when the reserve would be crossed. Zero when the volume was not measured.
    /// </summary>
    /// <param name="evaluation">Current cache-volume classification.</param>
    /// <param name="segmentBytes">Segment-copy bytes for the candidate article.</param>
    /// <param name="unwrittenSegmentBytes">Unflushed segment and compaction reservations.</param>
    /// <returns>The protected headroom in bytes.</returns>
    public static long ProtectedHeadroomBytes(
        in BulkStoragePressureEvaluation evaluation,
        long segmentBytes,
        long unwrittenSegmentBytes)
    {
        if (!evaluation.Measured)
        {
            return 0;
        }

        var extra = segmentBytes < 0 ? 0 : segmentBytes;
        var reserved = unwrittenSegmentBytes < 0 ? 0 : unwrittenSegmentBytes;
        try
        {
            return evaluation.FreeBytes - checked(evaluation.RecoveryReserveBytes + reserved + extra);
        }
        catch (OverflowException)
        {
            return long.MinValue;
        }
    }

    /// <summary>
    /// True when a new rewrite of a closed segment may start. In-progress compaction is not
    /// decided here. <see cref="BulkStoragePressureState.Normal"/>,
    /// <see cref="BulkStoragePressureState.Warning"/>, and
    /// <see cref="BulkStoragePressureState.Pressure"/> keep the existing density economics.
    /// </summary>
    /// <param name="evaluation">Current classification.</param>
    /// <param name="liveBytes">Present bytes that would be copied to the destination.</param>
    /// <param name="deadBytes">Bytes that deleting the source would return after that copy.</param>
    /// <returns>False when the state or the reserve headroom forbids the new write.</returns>
    public bool AllowsNewRewrite(
        in BulkStoragePressureEvaluation evaluation,
        long liveBytes,
        long deadBytes)
    {
        var live = liveBytes < 0 ? 0 : liveBytes;
        var dead = deadBytes < 0 ? 0 : deadBytes;
        switch (evaluation.State)
        {
            case BulkStoragePressureState.Normal:
            case BulkStoragePressureState.Warning:
            case BulkStoragePressureState.Pressure:
                return true;
            case BulkStoragePressureState.High:
                return dead > 0 && PreservesReserves(in evaluation, live);
            case BulkStoragePressureState.Critical:
                return dead > live && PreservesReserves(in evaluation, live);
            case BulkStoragePressureState.Emergency:
                return false;
            default:
                return true;
        }
    }

    /// <summary>Result used when the cache volume total is unknown. Maintenance is not suppressed.</summary>
    public static BulkStoragePressureEvaluation Unmeasured() =>
        new(
            TotalBytes: 0,
            FreeBytes: 0,
            UsedBytes: 0,
            UsedPercent: 0,
            FreePercent: 0,
            State: BulkStoragePressureState.Normal,
            NormalMaintenanceSufficient: true,
            AccelerateReclamation: false,
            RewriteAllowed: true,
            EmergencyAdmissionProtectionRequired: false,
            OperationalReserveBytes: 0,
            RecoveryReserveBytes: 0,
            RewriteReserveBytes: 0,
            AvailableReserveBytes: 0,
            ReserveExhausted: false,
            MaintenanceMode: ModeName(BulkStoragePressureState.Normal),
            Measured: false);

    /// <summary>Stable maintenance-posture name for <paramref name="state"/>.</summary>
    /// <param name="state">Classified watermark.</param>
    /// <returns>A short stable token for logs and results.</returns>
    public static string ModeName(BulkStoragePressureState state) =>
        state switch
        {
            BulkStoragePressureState.Pressure => "PrioritizeReclaimable",
            BulkStoragePressureState.High => "AggressiveReclaim",
            BulkStoragePressureState.Critical => "ImmediateSpaceRecovery",
            BulkStoragePressureState.Emergency => "EmergencyProtectIngress",
            _ => "AgeRetention",
        };

    /// <summary>Floor of <paramref name="totalBytes"/> times <paramref name="percent"/> / 100.</summary>
    /// <param name="totalBytes">Volume total. Non-positive yields zero.</param>
    /// <param name="percent">Integer percent. Non-positive yields zero.</param>
    /// <returns>The reserve size in bytes.</returns>
    public static long ReserveBytes(long totalBytes, int percent)
    {
        if (totalBytes <= 0 || percent <= 0)
        {
            return 0;
        }

        if (percent >= 100)
        {
            return totalBytes;
        }

        return (long)((decimal)totalBytes * percent / 100m);
    }

    /// <summary>
    /// True when <paramref name="usedBytes"/> is at or above <paramref name="percent"/> of
    /// <paramref name="totalBytes"/>, using the same decimal comparison as capacity admission.
    /// </summary>
    /// <param name="usedBytes">Used bytes.</param>
    /// <param name="totalBytes">Total bytes.</param>
    /// <param name="percent">Threshold percent. Zero or below matches any non-negative used count.</param>
    /// <returns>True when the watermark is reached.</returns>
    public static bool AtOrAbove(long usedBytes, long totalBytes, int percent)
    {
        if (totalBytes <= 0)
        {
            return false;
        }

        if (percent <= 0)
        {
            return usedBytes >= 0;
        }

        if (usedBytes <= 0)
        {
            return false;
        }

        return (decimal)usedBytes * 100m >= (decimal)totalBytes * percent;
    }

    private BulkStoragePressureState Classify(long usedBytes, long totalBytes)
    {
        if (AtOrAbove(usedBytes, totalBytes, Options.EmergencyPercent))
        {
            return BulkStoragePressureState.Emergency;
        }

        if (AtOrAbove(usedBytes, totalBytes, Options.CriticalPercent))
        {
            return BulkStoragePressureState.Critical;
        }

        if (AtOrAbove(usedBytes, totalBytes, Options.HighPercent))
        {
            return BulkStoragePressureState.High;
        }

        if (AtOrAbove(usedBytes, totalBytes, Options.PressurePercent))
        {
            return BulkStoragePressureState.Pressure;
        }

        if (AtOrAbove(usedBytes, totalBytes, Options.WarningPercent))
        {
            return BulkStoragePressureState.Warning;
        }

        return BulkStoragePressureState.Normal;
    }

    private static bool PreservesReserves(in BulkStoragePressureEvaluation evaluation, long liveBytes)
    {
        long reserveSum;
        try
        {
            reserveSum = checked(
                evaluation.OperationalReserveBytes
                + evaluation.RecoveryReserveBytes
                + evaluation.RewriteReserveBytes
                + liveBytes);
        }
        catch (OverflowException)
        {
            return false;
        }

        return evaluation.FreeBytes >= reserveSum;
    }

    private static long SumReserves(long operational, long recovery, long rewrite)
    {
        try
        {
            return checked(operational + recovery + rewrite);
        }
        catch (OverflowException)
        {
            return long.MaxValue;
        }
    }

    private static int PercentOf(long part, long total)
    {
        if (total <= 0 || part <= 0)
        {
            return 0;
        }

        if (part >= total)
        {
            return 100;
        }

        return (int)((decimal)part * 100m / total);
    }

    private static void ValidateThresholds(BulkStoragePressureOptions options)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(options.WarningPercent);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.WarningPercent, 100);
        ArgumentOutOfRangeException.ThrowIfNegative(options.PressurePercent);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.PressurePercent, 100);
        ArgumentOutOfRangeException.ThrowIfNegative(options.HighPercent);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.HighPercent, 100);
        ArgumentOutOfRangeException.ThrowIfNegative(options.CriticalPercent);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.CriticalPercent, 100);
        ArgumentOutOfRangeException.ThrowIfNegative(options.EmergencyPercent);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.EmergencyPercent, 100);
        if (options.WarningPercent >= options.PressurePercent
            || options.PressurePercent >= options.HighPercent
            || options.HighPercent >= options.CriticalPercent
            || options.CriticalPercent >= options.EmergencyPercent)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Bulk pressure watermarks must be strictly increasing.");
        }
    }
}
