namespace VectorNNTP.BackFiller.Configuration;

/// <summary>
/// Bindable BackFiller logging targets under <c>BackFiller:Logging</c>.
/// </summary>
/// <remarks>
/// This is the only logging configuration surface. A <c>Serilog</c> section is not read.
/// File logs use <see cref="BackFillerFileLoggingTargetOptions.LogDir"/>.
/// </remarks>
public sealed class BackFillerLoggingOptions
{
    /// <summary>Configuration section name under <see cref="BackFillerOptions.SectionName"/>.</summary>
    public const string SectionName = "Logging";

    /// <summary>Default Serilog minimum level when <see cref="LogLevel"/> is omitted.</summary>
    public const string DefaultLogLevel = "Debug";

    /// <summary>Default daily file retention when <see cref="LogRetentionDays"/> is omitted.</summary>
    public const int DefaultLogRetentionDays = 1;

    /// <summary>Minimum accepted <see cref="LogRetentionDays"/>.</summary>
    public const int MinimumLogRetentionDays = 1;

    /// <summary>Maximum accepted <see cref="LogRetentionDays"/>.</summary>
    public const int MaximumLogRetentionDays = 3650;

    /// <summary>Gets or sets the Serilog minimum level applied to every enabled target.</summary>
    /// <remarks>
    /// Accepted values are <c>Verbose</c>, <c>Debug</c>, <c>Information</c>, <c>Warning</c>,
    /// <c>Error</c>, and <c>Fatal</c>.
    /// </remarks>
    public string LogLevel { get; set; } = DefaultLogLevel;

    /// <summary>Gets or sets how many daily files the File target retains.</summary>
    /// <remarks>
    /// Range <see cref="MinimumLogRetentionDays"/>–<see cref="MaximumLogRetentionDays"/>.
    /// Applied only when <see cref="BackFillerFileLoggingTargetOptions.Enabled"/> is true.
    /// </remarks>
    public int LogRetentionDays { get; set; } = DefaultLogRetentionDays;

    /// <summary>
    /// Gets or sets whether enabled targets that accept a text formatter use JSON.
    /// </summary>
    /// <remarks>There is no per-target formatter setting.</remarks>
    public bool Json { get; set; }

    /// <summary>Gets or sets the file target.</summary>
    public BackFillerFileLoggingTargetOptions File { get; set; } = new();

    /// <summary>Gets or sets the RabbitMQ target.</summary>
    /// <remarks>
    /// Connectivity stays in <c>RabbitMq.json</c>. This object does not carry broker credentials.
    /// </remarks>
    public BackFillerRabbitMqLoggingTargetOptions RabbitMq { get; set; } = new();

    /// <summary>Gets or sets the syslog target.</summary>
    public BackFillerSyslogLoggingTargetOptions Syslog { get; set; } = new();
}

/// <summary>File logging target under <c>BackFiller:Logging:File</c>.</summary>
public sealed class BackFillerFileLoggingTargetOptions
{
    /// <summary>Default relative log directory.</summary>
    public const string DefaultLogDir = "logs";

    /// <summary>Gets or sets whether the rolling file sink is created. Default is enabled.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gets or sets the file-log directory. Required when <see cref="Enabled"/> is true.
    /// </summary>
    /// <remarks>
    /// Old key: <c>DirLogs</c>, then <c>BackFiller:LogDir</c>. Relative paths resolve through
    /// Common <c>ApplicationLocalPath.ResolveApplicationLocalPath</c> against
    /// <see cref="AppContext.BaseDirectory"/>, not the process working directory.
    /// An empty value is accepted when <see cref="Enabled"/> is false.
    /// </remarks>
    public string LogDir { get; set; } = DefaultLogDir;
}

/// <summary>RabbitMQ logging target under <c>BackFiller:Logging:RabbitMQ</c>.</summary>
public sealed class BackFillerRabbitMqLoggingTargetOptions
{
    /// <summary>Default exchange name.</summary>
    public const string DefaultExchange = "logs";

    /// <summary>Default routing key.</summary>
    public const string DefaultRoutingKey = "backfiller";

    /// <summary>Gets or sets whether RabbitMQ logging is requested. Default is disabled.</summary>
    public bool Enabled { get; set; }

    /// <summary>Gets or sets the exchange used when this target is enabled.</summary>
    public string Exchange { get; set; } = DefaultExchange;

    /// <summary>Gets or sets the routing key used when this target is enabled.</summary>
    public string RoutingKey { get; set; } = DefaultRoutingKey;
}

/// <summary>Syslog logging target under <c>BackFiller:Logging:Syslog</c>.</summary>
public sealed class BackFillerSyslogLoggingTargetOptions
{
    /// <summary>Default syslog port.</summary>
    public const int DefaultPort = 514;

    /// <summary>UDP protocol name.</summary>
    public const string UdpProtocol = "Udp";

    /// <summary>TCP protocol name.</summary>
    public const string TcpProtocol = "Tcp";

    /// <summary>Gets or sets whether the syslog sink is created. Default is disabled.</summary>
    public bool Enabled { get; set; }

    /// <summary>Gets or sets the syslog server host. Required when <see cref="Enabled"/> is true.</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>Gets or sets the syslog server port. Default is <see cref="DefaultPort"/>.</summary>
    public int Port { get; set; } = DefaultPort;

    /// <summary>Gets or sets the transport. Only <c>Udp</c> and <c>Tcp</c> are accepted when enabled.</summary>
    public string Protocol { get; set; } = UdpProtocol;
}
