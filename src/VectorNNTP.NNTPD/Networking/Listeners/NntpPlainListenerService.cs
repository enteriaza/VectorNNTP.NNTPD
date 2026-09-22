using System.Collections.Concurrent;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VectorNNTP.NNTPD.Configuration;
using VectorNNTP.NNTPD.Core;
using VectorNNTP.NNTPD.Networking.Transport;

namespace VectorNNTP.NNTPD.Networking.Listeners;

/// <summary>
/// Application service that binds cleartext NNTP TCP listeners and owns accepted plain connections.
/// </summary>
/// <remarks>
/// Starts after Cloudflare DNS reconciliation and before ACME. Does not wait for certificates.
/// </remarks>
public sealed class NntpPlainListenerService : IApplicationService, IAsyncDisposable
{
    private readonly IOptions<NntpdOptions> _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<NntpPlainListenerService> _logger;
    private readonly ConcurrentDictionary<NntpConnection, byte> _connections = new();
    private readonly List<SocketAcceptListener> _listeners = [];
    private readonly CancellationTokenSource _runCts = new();
    private Task? _execution;
    private int _started;
    private int _disposed;

    /// <summary>Initializes a new instance of the <see cref="NntpPlainListenerService"/> class.</summary>
    public NntpPlainListenerService(
        IOptions<NntpdOptions> options,
        ILoggerFactory loggerFactory,
        ILogger<NntpPlainListenerService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options;
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "NntpPlainListener";

    /// <inheritdoc />
    public Task? Execution => _execution;

    /// <summary>Gets the number of active plain connections (tests).</summary>
    internal int ActiveConnectionCount => _connections.Count;

    /// <summary>Gets bound local endpoints after start (tests).</summary>
    internal IReadOnlyList<System.Net.IPEndPoint> LocalEndPoints =>
        _listeners.Select(static l => l.LocalEndPoint).ToArray();

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            return Task.CompletedTask;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var options = _options.Value;
        var bindings = ListenEndpointPlanner.Plan(options.BindAddress, options.BindPort);
        if (bindings.Count == 0)
        {
            throw new InvalidOperationException("No listen bindings were produced for the plain NNTP port.");
        }

        foreach (var binding in bindings)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var listener = new SocketAcceptListener(
                binding,
                OnAcceptedAsync,
                _loggerFactory.CreateLogger($"{nameof(SocketAcceptListener)}.Plain"));
            listener.Start();
            _listeners.Add(listener);
        }

        _execution = WaitUntilStoppedAsync(_runCts.Token);
        _logger.LogInformation(
            "Plain NNTP listeners started ({ListenerCount}) on port {Port}.",
            _listeners.Count,
            options.BindPort);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _runCts.CancelAsync().ConfigureAwait(false);

        foreach (var listener in _listeners)
        {
            await listener.StopAsync().ConfigureAwait(false);
        }

        var connections = _connections.Keys.ToArray();
        foreach (var connection in connections)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await connection.CompleteAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error completing plain connection during stop.");
            }
        }

        foreach (var connection in connections)
        {
            try
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // Best-effort.
            }

            _connections.TryRemove(connection, out _);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        try
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort dispose.
        }

        foreach (var listener in _listeners)
        {
            await listener.DisposeAsync().ConfigureAwait(false);
        }

        _listeners.Clear();
        _runCts.Dispose();
    }

    private async ValueTask OnAcceptedAsync(Socket socket, CancellationToken cancellationToken)
    {
        NntpConnection? connection = null;
        try
        {
            connection = NntpConnection.StartPlain(
                socket,
                _loggerFactory.CreateLogger<NntpConnection>());
            _connections[connection] = 0;
            _logger.LogDebug("Plain connection accepted from {Remote}.", connection.RemoteEndPoint);

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, connection.ConnectionClosed)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Shutdown or connection completed.
            }
        }
        finally
        {
            if (connection is not null)
            {
                _connections.TryRemove(connection, out _);
                try
                {
                    await connection.DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                    // Best-effort.
                }
            }
            else
            {
                socket.Dispose();
            }
        }
    }

    private async Task WaitUntilStoppedAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected.
        }
    }
}
