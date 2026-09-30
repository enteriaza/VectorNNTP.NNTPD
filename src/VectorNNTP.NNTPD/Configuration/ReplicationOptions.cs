namespace VectorNNTP.NNTPD.Configuration;

/// <summary>
/// Second-copy placement settings for one NNTPD process.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="SecondCopySender"/> is the deployment invariant that exactly one NNTPD
/// instance may pin and STORE a second copy. This process does not elect a leader.
/// </para>
/// <para>
/// When the flag is false, primary placement still runs and replica STORE does not.
/// A new pin is pending. Completing it rewrites that same pin and does not change the
/// target. Completion is not inferred from a census. Decommissioning a StorageServer
/// is not configured here.
/// </para>
/// </remarks>
public sealed class ReplicationOptions
{
    /// <summary>
    /// Gets or sets whether this process is the single second-copy sender.
    /// </summary>
    public bool SecondCopySender { get; set; }

    /// <summary>
    /// Gets or sets the directory for the durable StorageServer roster and replication pins.
    /// Required when <see cref="SecondCopySender"/> is true.
    /// </summary>
    public string Directory { get; set; } = string.Empty;
}
