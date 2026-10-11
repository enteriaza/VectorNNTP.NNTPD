using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Storage.Engine.FileIndex;
using VectorNNTP.StorageServer.Storage.Engine.Maintenance;

namespace VectorNNTP.StorageServer.Storage.Engine;

/// <summary>Whether this maintenance cycle may logically expire articles because of bulk pressure.</summary>
public enum PressureExpirationMode : byte
{
    /// <summary>Normal, Warning, or an unmeasured volume. No pressure expiration.</summary>
    None = 0,

    /// <summary>
    /// A pass already started and used space is still at or above the recovery target,
    /// but below the Pressure watermark. Expire at the conservative limit.
    /// </summary>
    WindingDown = 1,

    /// <summary>Used space is at Pressure or higher. Expire up to that state's limit.</summary>
    Active = 2,
}

/// <summary>
/// Decides which bulk-committed articles pressure may logically expire.
/// </summary>
/// <remarks>
/// <para>
/// This is not <see cref="ArticleRetentionPolicy.IsExpirationEligible"/>. Age retention uses
/// <c>MaxRetentionAge</c> and skips a missing or future arrival. Pressure expiration does not
/// apply an age gate. It runs only when the maintenance cycle is in <see cref="PressureExpirationMode.Active"/> or
/// <see cref="PressureExpirationMode.WindingDown"/>. Neither predicate deletes segment bytes.
/// </para>
/// <para>
/// Arrival time is <see cref="StoredArticleMetadata.AcceptedUtc"/> and is the first sort key.
/// It is not an eligibility gate. <see cref="StoredArticleMetadata.LastAccessUtc"/> breaks
/// ties: a read after acceptance is kept ahead of an article whose hint is missing or still
/// the arrival instant. That distinction is not an age threshold. A future hint is not
/// selected ahead of either.
/// </para>
/// </remarks>
public static class ArticlePressureRetentionPolicy
{
    /// <summary>Articles one Pressure cycle may expire from the current index window.</summary>
    public const int PressureExpirationLimit = 1;

    /// <summary>Articles one High cycle may expire from the current index window.</summary>
    public const int HighExpirationLimit = 8;

    /// <summary>Articles one Critical cycle may expire from the current index window.</summary>
    public const int CriticalExpirationLimit = 32;

    /// <summary>
    /// Articles one Emergency cycle may expire. The caller also caps the index window, so this
    /// does not scan the retained population.
    /// </summary>
    public const int EmergencyExpirationLimit = 128;

    /// <summary>Used-percent at which a latched pass stops. Below this, Warning does not start a new pass.</summary>
    /// <param name="options">Watermarks. Null uses the defaults.</param>
    /// <returns>Pressure percent minus the recovery margin, floored at zero.</returns>
    public static int RecoveryTargetPercent(BulkStoragePressureOptions? options)
    {
        options ??= new BulkStoragePressureOptions();
        var target = options.PressurePercent - options.PressureRecoveryMarginPercent;
        return target < 0 ? 0 : target;
    }

    /// <summary>
    /// Returns whether pressure expiration should run for this measurement.
    /// </summary>
    /// <param name="evaluation">Current cache-volume classification.</param>
    /// <param name="alreadyLatched">True when an earlier cycle in this process started a pass.</param>
    /// <param name="options">Watermarks and the recovery margin.</param>
    /// <returns>
    /// <see cref="PressureExpirationMode.None"/> when the volume is unmeasured, or when it is below
    /// Pressure and no pass is latched, or when a latched pass has fallen below the recovery target.
    /// </returns>
    public static PressureExpirationMode Mode(
        in BulkStoragePressureEvaluation evaluation,
        bool alreadyLatched,
        BulkStoragePressureOptions options)
    {
        if (!evaluation.Measured)
        {
            return PressureExpirationMode.None;
        }

        if (evaluation.State is BulkStoragePressureState.Pressure
            or BulkStoragePressureState.High
            or BulkStoragePressureState.Critical
            or BulkStoragePressureState.Emergency)
        {
            return PressureExpirationMode.Active;
        }

        if (!alreadyLatched || evaluation.UsedPercent < RecoveryTargetPercent(options))
        {
            return PressureExpirationMode.None;
        }

        return PressureExpirationMode.WindingDown;
    }

    /// <summary>How many eligible rows the current mode may expire from one window.</summary>
    /// <param name="state">Bulk class that produced <paramref name="mode"/>.</param>
    /// <param name="mode">Result of <see cref="Mode"/>.</param>
    /// <returns>Zero when <paramref name="mode"/> is <see cref="PressureExpirationMode.None"/>.</returns>
    public static int ExpirationLimit(BulkStoragePressureState state, PressureExpirationMode mode)
    {
        if (mode == PressureExpirationMode.None)
        {
            return 0;
        }

        if (mode == PressureExpirationMode.WindingDown)
        {
            return PressureExpirationLimit;
        }

        return state switch
        {
            BulkStoragePressureState.High => HighExpirationLimit,
            BulkStoragePressureState.Critical => CriticalExpirationLimit,
            BulkStoragePressureState.Emergency => EmergencyExpirationLimit,
            _ => PressureExpirationLimit,
        };
    }

    /// <summary>
    /// Returns whether a bulk-committed Present article may be pressure-expired.
    /// </summary>
    /// <param name="bulkCommitted">True only for an authoritative Present row.</param>
    /// <param name="arrivalUtc">Durable Accept instant. A missing or future instant does not exclude the row.</param>
    /// <param name="nowUtc">Evaluation instant. Unused; capacity eligibility does not depend on age.</param>
    /// <returns>True when the row is a bulk-committed Present article.</returns>
    public static bool IsPressureExpirationEligible(
        bool bulkCommitted,
        DateTimeOffset? arrivalUtc,
        DateTimeOffset nowUtc)
    {
        _ = arrivalUtc;
        _ = nowUtc;
        return bulkCommitted;
    }

    /// <summary>
    /// Orders two eligible candidates. Older arrival first, then an unread hint before a known
    /// access, then the older access hint, then larger records.
    /// </summary>
    /// <param name="left">First candidate.</param>
    /// <param name="right">Second candidate.</param>
    /// <param name="nowUtc">Evaluation instant.</param>
    /// <returns>Negative when <paramref name="left"/> should expire before <paramref name="right"/>.</returns>
    internal static int CompareExpirationOrder(
        in RetentionScanCandidate left,
        in RetentionScanCandidate right,
        DateTimeOffset nowUtc)
    {
        var age = left.AcceptedUtc.CompareTo(right.AcceptedUtc);
        if (age != 0)
        {
            return age;
        }

        var access = AccessClass(in left, nowUtc).CompareTo(AccessClass(in right, nowUtc));
        if (access != 0)
        {
            return access;
        }

        var hint = left.LastAccessUtc.CompareTo(right.LastAccessUtc);
        if (hint != 0)
        {
            return hint;
        }

        return right.Location.Length.CompareTo(left.Location.Length);
    }

    /// <summary>
    /// 0 when the hint is missing or not after arrival, 1 for a known past access, 2 for a future hint.
    /// Lower expires first. This is not an age threshold.
    /// </summary>
    private static int AccessClass(in RetentionScanCandidate candidate, DateTimeOffset nowUtc)
    {
        var access = candidate.LastAccessUtc;
        if (access == DateTimeOffset.MinValue || access.UtcTicks <= candidate.AcceptedUtc.UtcTicks)
        {
            return 0;
        }

        if (access.UtcTicks > nowUtc.UtcTicks)
        {
            return 2;
        }

        return 1;
    }
}
