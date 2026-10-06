namespace VectorNNTP.StorageServer.Configuration;

/// <summary>
/// Bulk retention watermarks for the <c>CacheDir</c> volume, under
/// <c>StorageServer:Storage:BulkPressure</c>.
/// </summary>
/// <remarks>
/// <para>
/// These percentages classify filesystem used space on the cache volume. They do not
/// replace <see cref="ArticleStorageOptions.JournalSoftLimitBytes"/> /
/// <see cref="ArticleStorageOptions.JournalHardLimitBytes"/>, and they do not replace
/// <see cref="ArticleCapacityOptions"/> admission ceilings. Journal pressure counts
/// outstanding recoverable ingress bytes. Capacity admission counts process-local
/// reservations against <see cref="ArticleCapacityOptions.MaximumUtilization"/>.
/// This object counts physical used space on the cache volume.
/// </para>
/// <para>
/// The reserve percentages are measurements. They are not a filesystem quota and not
/// an allocator. Maintenance uses all three when deciding whether a new low-density
/// rewrite would consume headroom that must stay free. At High, Critical, and Emergency,
/// Accept rejects a new article whose segment copy would leave free space below the
/// recovery reserve. Operational and rewrite reserves are not subtracted from Accept.
/// </para>
/// <para>
/// Classification does not delete a live acknowledged article. An article that has been
/// accepted and is still only in the ingress journal stays recoverable.
/// </para>
/// </remarks>
public sealed class BulkStoragePressureOptions
{
    /// <summary>Used-percent at which the cache volume enters <c>Warning</c>.</summary>
    public const int DefaultWarningPercent = 75;

    /// <summary>Used-percent at which the cache volume enters <c>Pressure</c>.</summary>
    public const int DefaultPressurePercent = 80;

    /// <summary>Used-percent at which the cache volume enters <c>High</c>.</summary>
    public const int DefaultHighPercent = 85;

    /// <summary>Used-percent at which the cache volume enters <c>Critical</c>.</summary>
    public const int DefaultCriticalPercent = 90;

    /// <summary>Used-percent at which the cache volume enters <c>Emergency</c>.</summary>
    public const int DefaultEmergencyPercent = 95;

    /// <summary>Operational reserve, as a percent of volume total bytes.</summary>
    public const int DefaultOperationalReservePercent = 5;

    /// <summary>Recovery reserve, as a percent of volume total bytes.</summary>
    public const int DefaultRecoveryReservePercent = 5;

    /// <summary>
    /// Rewrite/reclamation reserve, as a percent of volume total bytes.
    /// Ten percent is the configured upper end of the five-to-ten percent reserve.
    /// </summary>
    public const int DefaultRewriteReservePercent = 10;

    /// <summary>Gets or sets the Warning used-percent. Default <see cref="DefaultWarningPercent"/>.</summary>
    public int WarningPercent { get; set; } = DefaultWarningPercent;

    /// <summary>Gets or sets the Pressure used-percent. Default <see cref="DefaultPressurePercent"/>.</summary>
    public int PressurePercent { get; set; } = DefaultPressurePercent;

    /// <summary>Gets or sets the High used-percent. Default <see cref="DefaultHighPercent"/>.</summary>
    public int HighPercent { get; set; } = DefaultHighPercent;

    /// <summary>Gets or sets the Critical used-percent. Default <see cref="DefaultCriticalPercent"/>.</summary>
    public int CriticalPercent { get; set; } = DefaultCriticalPercent;

    /// <summary>Gets or sets the Emergency used-percent. Default <see cref="DefaultEmergencyPercent"/>.</summary>
    public int EmergencyPercent { get; set; } = DefaultEmergencyPercent;

    /// <summary>
    /// Gets or sets the operational reserve percent. Default <see cref="DefaultOperationalReservePercent"/>.
    /// </summary>
    public int OperationalReservePercent { get; set; } = DefaultOperationalReservePercent;

    /// <summary>
    /// Gets or sets the recovery reserve percent. Default <see cref="DefaultRecoveryReservePercent"/>.
    /// At High, Critical, and Emergency this percent of total bytes is the Accept floor.
    /// </summary>
    public int RecoveryReservePercent { get; set; } = DefaultRecoveryReservePercent;

    /// <summary>
    /// Gets or sets the rewrite/reclamation reserve percent. Default <see cref="DefaultRewriteReservePercent"/>.
    /// </summary>
    public int RewriteReservePercent { get; set; } = DefaultRewriteReservePercent;
}
