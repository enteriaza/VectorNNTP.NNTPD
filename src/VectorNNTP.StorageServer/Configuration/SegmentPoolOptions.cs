namespace VectorNNTP.StorageServer.Configuration;

/// <summary>
/// Segment spool collection and bulk-retention watermarks under
/// <c>StorageServer:Storage:SegmentPools</c>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Directories"/> replaces the single <c>Storage:CacheDir</c> setting. Each entry is
/// one spool with its own directory and hard <see cref="SegmentPoolDirectoryOptions.MaxCapacity"/>.
/// The percent fields classify accounted bytes against that capacity and, separately, physical
/// used space against the hosting filesystem. They do not replace
/// <see cref="ArticleCapacityOptions"/> or the journal limits.
/// </para>
/// <para>
/// Accounted spool bytes are the sum of segment file lengths on that spool plus reservations for
/// accepted or relocated copies whose durable flush has not yet returned. Article payload totals
/// and journal outstanding bytes are not that sum. A logical eviction does not reduce it. A
/// physical file delete does.
/// </para>
/// </remarks>
public sealed class SegmentPoolOptions
{
    /// <summary>Used-percent at which a spool or its volume enters Warning.</summary>
    public const int DefaultWarningPercent = BulkStoragePressureOptions.DefaultWarningPercent;

    /// <summary>Used-percent at which proactive reclamation starts.</summary>
    public const int DefaultPressurePercent = BulkStoragePressureOptions.DefaultPressurePercent;

    /// <summary>Used-percent at which reclamation escalates.</summary>
    public const int DefaultHighPercent = BulkStoragePressureOptions.DefaultHighPercent;

    /// <summary>Used-percent at which new writes avoid this spool when another spool is healthier.</summary>
    public const int DefaultCriticalPercent = BulkStoragePressureOptions.DefaultCriticalPercent;

    /// <summary>Used-percent at which the final safety reserve is protected.</summary>
    public const int DefaultEmergencyPercent = BulkStoragePressureOptions.DefaultEmergencyPercent;

    /// <summary>Operational reserve, as a percent of the measured total.</summary>
    public const int DefaultOperationalReservePercent = BulkStoragePressureOptions.DefaultOperationalReservePercent;

    /// <summary>Recovery reserve, as a percent of the measured total.</summary>
    public const int DefaultRecoveryReservePercent = BulkStoragePressureOptions.DefaultRecoveryReservePercent;

    /// <summary>Rewrite reserve, as a percent of the measured total.</summary>
    public const int DefaultRewriteReservePercent = BulkStoragePressureOptions.DefaultRewriteReservePercent;

    /// <summary>
    /// Used-percent points below <see cref="PressurePercent"/> at which a started reclamation pass stops.
    /// </summary>
    public const int DefaultPressureRecoveryMarginPercent = BulkStoragePressureOptions.DefaultPressureRecoveryMarginPercent;

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

    /// <summary>Gets or sets the operational reserve percent.</summary>
    public int OperationalReservePercent { get; set; } = DefaultOperationalReservePercent;

    /// <summary>
    /// Gets or sets the recovery reserve percent.
    /// At High, Critical, and Emergency this percent of total bytes is the Accept floor.
    /// </summary>
    public int RecoveryReservePercent { get; set; } = DefaultRecoveryReservePercent;

    /// <summary>Gets or sets the rewrite/reclamation reserve percent.</summary>
    public int RewriteReservePercent { get; set; } = DefaultRewriteReservePercent;

    /// <summary>
    /// Gets or sets how many used-percent points below <see cref="PressurePercent"/> a started
    /// pressure pass must reach before it stops.
    /// </summary>
    public int PressureRecoveryMarginPercent { get; set; } = DefaultPressureRecoveryMarginPercent;

    /// <summary>
    /// Gets or sets the independently managed segment spools. At least one is required.
    /// The unbound instance contains one default spool. The first directory inserted by configuration
    /// binding replaces that default so a bound array is not appended to it.
    /// </summary>
    public SegmentPoolDirectoryList Directories { get; set; } = new();

    /// <summary>Copies the watermark fields into the policy object maintenance already evaluates.</summary>
    /// <returns>A policy snapshot. Mutations of the result do not change this instance.</returns>
    public BulkStoragePressureOptions ToBulkPressure() =>
        new()
        {
            WarningPercent = WarningPercent,
            PressurePercent = PressurePercent,
            HighPercent = HighPercent,
            CriticalPercent = CriticalPercent,
            EmergencyPercent = EmergencyPercent,
            OperationalReservePercent = OperationalReservePercent,
            RecoveryReservePercent = RecoveryReservePercent,
            RewriteReservePercent = RewriteReservePercent,
            PressureRecoveryMarginPercent = PressureRecoveryMarginPercent,
        };
}
