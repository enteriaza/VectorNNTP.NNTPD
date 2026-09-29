using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VectorNNTP.BackFiller.Configuration;
using VectorNNTP.BackFiller.Hosting.Systemd;
using VectorNNTP.BackFiller.Tests.Fixtures;

namespace VectorNNTP.BackFiller.Tests.Hosting.Systemd;

public sealed class SystemdLifecycleNotifierTests
{
    [Fact]
    public async Task Readiness_FollowsApplicationStarted_AndIsNotDuplicated()
    {
        var notify = new FakeSystemdNotifyBridge();
        var lifetime = new TestHostApplicationLifetime();
        var health = new BackFillerApplicationHealth();
        using var notifier = CreateNotifier(lifetime, health, notify);

        await notifier.StartAsync(CancellationToken.None);
        Assert.Equal(0, notify.ReadyCount);
        Assert.Contains(notify.Notifications, n => n.Contains("Starting", StringComparison.Ordinal));

        lifetime.SignalStarted();
        Assert.Equal(1, notify.ReadyCount);
        Assert.True(health.IsHealthyForWatchdog);
        Assert.Contains(notify.Notifications, n => n.Contains("Running", StringComparison.Ordinal));

        lifetime.SignalStopping();
        Assert.Equal(1, notify.StoppingCount);
        Assert.False(health.IsHealthyForWatchdog);

        await notifier.StopAsync(CancellationToken.None);
        Assert.Equal(1, notify.ReadyCount);
        Assert.Equal(1, notify.StoppingCount);
    }

    [Fact]
    public async Task Readiness_IsNotSentBeforeApplicationStarted()
    {
        var notify = new FakeSystemdNotifyBridge();
        var lifetime = new TestHostApplicationLifetime();
        var health = new BackFillerApplicationHealth();
        using var notifier = CreateNotifier(lifetime, health, notify);

        await notifier.StartAsync(CancellationToken.None);

        Assert.Equal(0, notify.ReadyCount);
        Assert.False(health.IsHealthyForWatchdog);
    }

    [Fact]
    public async Task Readiness_IsNotSentWhenNotifyDisabled()
    {
        var notify = new FakeSystemdNotifyBridge { IsEnabled = false };
        var lifetime = new TestHostApplicationLifetime();
        var health = new BackFillerApplicationHealth();
        using var notifier = CreateNotifier(lifetime, health, notify);

        await notifier.StartAsync(CancellationToken.None);
        lifetime.SignalStarted();
        lifetime.SignalStopping();
        await notifier.StopAsync(CancellationToken.None);

        Assert.Empty(notify.Notifications);
        Assert.Equal(0, notify.ReadyCount);
        Assert.Equal(0, notify.StoppingCount);
    }

    [Fact]
    public async Task Shutdown_PreventsSubsequentReadinessReporting()
    {
        var notify = new FakeSystemdNotifyBridge();
        var lifetime = new TestHostApplicationLifetime();
        var health = new BackFillerApplicationHealth();
        using var notifier = CreateNotifier(lifetime, health, notify);

        await notifier.StartAsync(CancellationToken.None);
        lifetime.SignalStopping();
        Assert.Equal(1, notify.StoppingCount);

        lifetime.SignalStarted();
        Assert.Equal(0, notify.ReadyCount);
        Assert.False(health.IsHealthyForWatchdog);

        await notifier.StopAsync(CancellationToken.None);
        Assert.Equal(1, notify.StoppingCount);
    }

    [Fact]
    public async Task RepeatedStop_IsIdempotent()
    {
        var notify = new FakeSystemdNotifyBridge();
        var lifetime = new TestHostApplicationLifetime();
        var health = new BackFillerApplicationHealth();
        using var notifier = CreateNotifier(lifetime, health, notify);

        await notifier.StartAsync(CancellationToken.None);
        lifetime.SignalStarted();
        lifetime.SignalStopping();

        await notifier.StopAsync(CancellationToken.None);
        await notifier.StopAsync(CancellationToken.None);

        Assert.Equal(1, notify.ReadyCount);
        Assert.Equal(1, notify.StoppingCount);
    }

    [Fact]
    public async Task NotificationFailure_DoesNotCrashNotifier()
    {
        var notify = new FakeSystemdNotifyBridge
        {
            ThrowOnNotify = new InvalidOperationException("notify-failed"),
        };
        var lifetime = new TestHostApplicationLifetime();
        var health = new BackFillerApplicationHealth();
        using var notifier = CreateNotifier(lifetime, health, notify);

        await notifier.StartAsync(CancellationToken.None);
        lifetime.SignalStarted();
        lifetime.SignalStopping();
        await notifier.StopAsync(CancellationToken.None);

        Assert.Equal(0, notify.ReadyCount);
        Assert.Equal(0, notify.StoppingCount);
    }

    [Fact]
    public void ApplicationHealth_TracksStartedStoppingAndUnexpectedTermination()
    {
        var health = new BackFillerApplicationHealth();
        Assert.False(health.IsHealthyForWatchdog);

        health.MarkStarted();
        Assert.True(health.IsHealthyForWatchdog);

        health.MarkUnexpectedTermination();
        Assert.False(health.IsHealthyForWatchdog);

        var health2 = new BackFillerApplicationHealth();
        health2.MarkStarted();
        health2.MarkStopping();
        Assert.False(health2.IsHealthyForWatchdog);
    }

    private static SystemdLifecycleNotifier CreateNotifier(
        IHostApplicationLifetime lifetime,
        BackFillerApplicationHealth health,
        FakeSystemdNotifyBridge notify,
        Action<BackFillerOptions>? configure = null)
    {
        var options = BackFillerTestOptions.CreateValid();
        configure?.Invoke(options);

        return new SystemdLifecycleNotifier(
            lifetime,
            health,
            notify,
            RecordingSystemdRuntimeFactory.LinuxUnderSystemd(),
            Options.Create(options),
            NullLogger<SystemdLifecycleNotifier>.Instance);
    }
}
