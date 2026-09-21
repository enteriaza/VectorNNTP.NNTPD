using System.ComponentModel.DataAnnotations;

namespace VectorNNTP.NNTPD.Configuration;

/// <summary>
/// Strongly typed lifecycle and hosting options for VectorNNTP.NNTPD.
/// </summary>
public sealed class NntpdOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Nntpd";

    /// <summary>Gets or sets the application display name used in logs and service registration metadata.</summary>
    [Required(AllowEmptyStrings = false)]
    [MaxLength(128)]
    public string ApplicationName { get; set; } = "VectorNNTP.NNTPD";

    /// <summary>
    /// Gets or sets the maximum time allowed for graceful shutdown of application services.
    /// </summary>
    public TimeSpan GracefulShutdownTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets or sets the maximum time allowed for application startup before cancellation is considered a failure.
    /// </summary>
    /// <remarks>
    /// When <see langword="null"/>, startup is bounded only by the host cancellation token.
    /// </remarks>
    public TimeSpan? StartupTimeout { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the process should exit if an application service
    /// terminates unexpectedly while running.
    /// </summary>
    public bool StopHostOnUnexpectedServiceTermination { get; set; } = true;

    /// <summary>
    /// Gets or sets systemd-specific notification and watchdog options.
    /// </summary>
    /// <remarks>
    /// Isolated from future NNTP settings. Does not override systemd-supplied watchdog deadlines.
    /// </remarks>
    [Required]
    public SystemdOptions Systemd { get; set; } = new();
}
