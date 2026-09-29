using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTP.BackFiller.Configuration;
using VectorNNTP.BackFiller.Tests.Fixtures;
using VectorNNTP.NNTPD.Core;
using BackFillerApplicationServiceManager = VectorNNTP.BackFiller.Core.ApplicationServiceManager;

namespace VectorNNTP.BackFiller.Tests.Core;

public sealed class ApplicationServiceManagerTests
{
    [Fact]
    public async Task Starts_in_registration_order_and_stops_in_reverse()
    {
        var order = new List<string>();
        var first = new TrackingService("acme", order);
        var second = new TrackingService("listener", order);
        var runtime = BackFillerRuntimeOptionsFactory.Create(
            BackFillerTestOptions.CreateValid(),
            BackFillerTestOptions.CreateValidNntpDb());
        var manager = new BackFillerApplicationServiceManager(
            [first, second],
            runtime,
            NullLogger<BackFillerApplicationServiceManager>.Instance);

        await manager.StartAsync(CancellationToken.None);
        Assert.Equal(["start:acme", "start:listener"], order);
        Assert.Equal([first, second], manager.StartedServices);

        await manager.StopAsync(CancellationToken.None);
        Assert.Equal(["start:acme", "start:listener", "stop:listener", "stop:acme"], order);
        Assert.Empty(manager.StartedServices);
    }

    [Fact]
    public async Task Startup_failure_rolls_back_already_started_services()
    {
        var order = new List<string>();
        var first = new TrackingService("acme", order);
        var second = new TrackingService("listener", order, startException: new InvalidOperationException("listener blocked"));
        var runtime = BackFillerRuntimeOptionsFactory.Create(
            BackFillerTestOptions.CreateValid(),
            BackFillerTestOptions.CreateValidNntpDb());
        var manager = new BackFillerApplicationServiceManager(
            [first, second],
            runtime,
            NullLogger<BackFillerApplicationServiceManager>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.StartAsync(CancellationToken.None));
        Assert.Equal(["start:acme", "start:listener", "stop:acme"], order);
        Assert.Empty(manager.StartedServices);
    }

    private sealed class TrackingService : IApplicationService
    {
        private readonly List<string> _order;
        private readonly Exception? _startException;

        public TrackingService(string name, List<string> order, Exception? startException = null)
        {
            Name = name;
            _order = order;
            _startException = startException;
        }

        public string Name { get; }

        public Task? Execution => null;

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _order.Add("start:" + Name);
            if (_startException is not null)
            {
                throw _startException;
            }

            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _order.Add("stop:" + Name);
            return Task.CompletedTask;
        }
    }
}
