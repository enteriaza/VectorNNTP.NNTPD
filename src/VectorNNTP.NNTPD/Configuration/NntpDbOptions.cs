namespace VectorNNTP.NNTPD.Configuration;

/// <summary>
/// Application-level NntpDB options. Physical connection pooling is owned by MySqlConnector.
/// </summary>
/// <remarks>
/// <para>
/// The configuration section name is <see cref="SectionName"/> (<c>NntpDb</c>).
/// The connection string is <c>ConnectionStrings:NntpDB</c> (<see cref="ConnectionStringName"/>).
/// Provider pooling, pool size, and idle reaping belong in that connection string.
/// </para>
/// <para>
/// Do not log <see cref="ConnectionString"/>.
/// </para>
/// </remarks>
public sealed class NntpDbOptions
{
    /// <summary>Top-level NntpDB application-options section name.</summary>
    public const string SectionName = "NntpDb";

    /// <summary>Connection-string name under <c>ConnectionStrings</c>.</summary>
    public const string ConnectionStringName = "NntpDB";

    /// <summary>Default wall-clock budget for the startup connectivity check.</summary>
    public static readonly TimeSpan DefaultStartupTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Gets or sets the MySQL connection string copied from
    /// <c>ConnectionStrings:NntpDB</c> when this property is empty.
    /// </summary>
    /// <remarks>Secret. Never log this value.</remarks>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the wall-clock budget for startup connect and <c>SELECT 1</c>.
    /// </summary>
    /// <remarks>
    /// Connectivity failures may retry until this budget elapses. Authentication
    /// and <c>SELECT 1</c> result failures fail immediately.
    /// </remarks>
    public TimeSpan StartupTimeout { get; set; } = DefaultStartupTimeout;
}
