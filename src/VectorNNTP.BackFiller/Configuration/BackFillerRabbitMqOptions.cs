namespace VectorNNTP.BackFiller.Configuration;

/// <summary>
/// RabbitMQ connection and policy options bound from <c>BackFiller:RabbitMQ</c>.
/// </summary>
/// <remarks>
/// Never log <see cref="Password"/> or a complete instance. Canonical secret environment
/// variables are <see cref="BackFillerOptions.RabbitMqUsernameEnvironmentVariable"/> and
/// <see cref="BackFillerOptions.RabbitMqPasswordEnvironmentVariable"/>.
/// </remarks>
public sealed class BackFillerRabbitMqOptions
{
    /// <summary>Maximum admitted work-request envelope size in bytes.</summary>
    public int? WorkRequestMaxPayloadBytes { get; set; } = 1024;

    /// <summary>Channel-lease timeout in seconds.</summary>
    public int? ChannelLeaseTimeoutSeconds { get; set; } = 60;

    /// <summary>RPC operation timeout in seconds.</summary>
    public int? RpcTimeoutSeconds { get; set; } = 30;

    /// <summary>Broker-blocked connection timeout in seconds.</summary>
    public int? ConnectionBlockedTimeoutSeconds { get; set; } = 30;

    /// <summary>Broker host endpoints.</summary>
    public string[]? Hosts { get; set; } = [];

    /// <summary>Broker username.</summary>
    public string? Username { get; set; }

    /// <summary>Broker password. Secret.</summary>
    public string? Password { get; set; }

    /// <summary>Virtual host.</summary>
    public string? VirtualHost { get; set; } = "/";

    /// <summary>Whether connections use TLS.</summary>
    public bool? EnableSsl { get; set; } = true;

    /// <summary>AMQP TCP port.</summary>
    public int? Port { get; set; } = 5672;

    /// <summary>Bounded channel-pool size policy.</summary>
    public int? ChannelPoolSize { get; set; } = 512;

    /// <summary>Minimum connection-count policy.</summary>
    public int? MinConnections { get; set; } = 4;

    /// <summary>Maximum connection-count policy.</summary>
    public int? MaxConnections { get; set; } = 16;

    /// <summary>Consecutive recovery-failure budget.</summary>
    public int? MaxConsecutiveRecoveryFailures { get; set; } = 5;

    /// <summary>Maximum pending channel-lease waiters.</summary>
    public int? MaxPendingLeaseWaiters { get; set; } = 1024;

    /// <summary>Idle seconds before scale-down.</summary>
    public int? ConnectionScaleDownIdleSeconds { get; set; } = 300;

    /// <summary>Scale-down cooldown in seconds.</summary>
    public int? ScaleDownCooldownSeconds { get; set; } = 30;

    /// <summary>Network recovery interval in seconds.</summary>
    public int? NetworkRecoveryIntervalSeconds { get; set; } = 5;

    /// <summary>Application reconnect base delay in milliseconds.</summary>
    public int? PoolReconnectBaseDelayMs { get; set; } = 250;

    /// <summary>Application reconnect maximum delay in milliseconds.</summary>
    public int? PoolReconnectMaxDelayMs { get; set; } = 30000;

    /// <summary>Minimum healthy connection lifetime in seconds.</summary>
    public int? MinimumConnectionLifetimeSeconds { get; set; } = 300;

    /// <summary>Publisher-confirm timeout in seconds.</summary>
    public int? PublishConfirmTimeoutSeconds { get; set; } = 10;

    /// <summary>Shutdown-drain budget in seconds. Must be ≤ shutdown grace period.</summary>
    public int? MaximumShutdownDrainTimeoutSeconds { get; set; } = 30;

    /// <summary>Degraded-capacity threshold (0, 1].</summary>
    public double? DegradedThreshold { get; set; } = 0.75;

    /// <summary>Consecutive-unhealthy threshold.</summary>
    public int? UnhealthyThreshold { get; set; } = 5;

    /// <summary>Requested heartbeat in seconds. 0 disables heartbeats.</summary>
    public int? RequestedHeartbeatSeconds { get; set; } = 60;

    /// <summary>Socket I/O timeout in seconds.</summary>
    public int? SocketTimeoutSeconds { get; set; } = 30;

    /// <summary>Requested channel limit per connection.</summary>
    public int? RequestedChannelMax { get; set; } = 2047;

    /// <summary>Optional Basic.Qos prefetch.</summary>
    public ushort? ConsumerPrefetchCount { get; set; }

    /// <summary>Optional AMQP CorrelationId that gates payload diagnostics.</summary>
    public string? DiagnosticPayloadCorrelationId { get; set; }
}
