using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VectorNNTP.NNTPD.Configuration;
using VectorNNTP.NNTPD.Core;

namespace VectorNNTP.NNTPD.Tests;

internal static class TestHostFactory
{
    public static NntpdOptions CreateOptions(
        TimeSpan? gracefulShutdownTimeout = null,
        TimeSpan? startupTimeout = null)
    {
        return new NntpdOptions
        {
            ApplicationName = "VectorNNTP.NNTPD.Tests",
            GracefulShutdownTimeout = gracefulShutdownTimeout ?? TimeSpan.FromSeconds(5),
            StartupTimeout = startupTimeout,
            StopHostOnUnexpectedServiceTermination = true,
        };
    }

    public static ApplicationServiceManager CreateServiceManager(
        IEnumerable<IApplicationService> services,
        NntpdOptions? options = null)
    {
        return new ApplicationServiceManager(
            services,
            Options.Create(options ?? CreateOptions()),
            NullLogger<ApplicationServiceManager>.Instance);
    }

    public static ApplicationLifecycle CreateLifecycle(
        IEnumerable<IApplicationService> services,
        NntpdOptions? options = null)
    {
        options ??= CreateOptions();
        var manager = CreateServiceManager(services, options);
        return new ApplicationLifecycle(
            manager,
            Options.Create(options),
            NullLogger<ApplicationLifecycle>.Instance);
    }
}

internal sealed class FakeApplicationService : IApplicationService
{
    private readonly Func<CancellationToken, Task>? _onStart;
    private readonly Func<CancellationToken, Task>? _onStop;
    private readonly TaskCompletionSource? _executionTcs;
    private int _startCount;
    private int _stopCount;

    public FakeApplicationService(
        string name,
        Func<CancellationToken, Task>? onStart = null,
        Func<CancellationToken, Task>? onStop = null,
        bool withExecution = false)
    {
        Name = name;
        _onStart = onStart;
        _onStop = onStop;
        if (withExecution)
        {
            _executionTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    public string Name { get; }

    public Task? Execution => _executionTcs?.Task;

    public int StartCount => Volatile.Read(ref _startCount);

    public int StopCount => Volatile.Read(ref _stopCount);

    public List<string> SharedStartOrder { get; set; } = [];

    public List<string> SharedStopOrder { get; set; } = [];

    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource StopGate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool HoldStopUntilReleased { get; set; }

    public void CompleteExecution(Exception? exception = null)
    {
        if (_executionTcs is null)
        {
            throw new InvalidOperationException("Execution was not enabled for this fake.");
        }

        if (exception is null)
        {
            _executionTcs.TrySetResult();
        }
        else
        {
            _executionTcs.TrySetException(exception);
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_onStart is not null)
        {
            await _onStart(cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _startCount);
        SharedStartOrder.Add(Name);
        Started.TrySetResult();
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (HoldStopUntilReleased)
        {
            await StopGate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        if (_onStop is not null)
        {
            await _onStop(cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _stopCount);
        SharedStopOrder.Add(Name);
    }
}
