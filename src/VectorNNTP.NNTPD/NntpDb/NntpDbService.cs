using Microsoft.Extensions.Options;
using VectorNNTP.NNTPD.Configuration;
using VectorNNTP.NNTPD.Core;

namespace VectorNNTP.NNTPD.NntpDb;

/// <summary>
/// Application-level NntpDB service: configuration, lifecycle, and startup connectivity.
/// </summary>
/// <remarks>
/// <para>
/// NNTPD owns the database service and lifecycle. MySqlConnector owns physical
/// connection pooling. This type does not cache, idle-reap, or reserve connections.
/// </para>
/// <para>
/// Startup validates the connection string with
/// <c>MySqlConnectionStringBuilder</c>, opens a logical connection, executes
/// <c>SELECT 1</c>, disposes that connection, and fails the host when the check
/// does not succeed. A malformed connection string fails immediately without
/// retry. There is no degraded or in-memory fallback.
/// </para>
/// </remarks>
public sealed class NntpDbService : IApplicationService, IAsyncDisposable
{
    private static readonly TimeSpan StartupRetryDelay = TimeSpan.FromSeconds(1);

    private readonly INntpDbConnectionFactory _factory;
    private readonly IOptions<NntpDbOptions> _options;
    private readonly ILogger<NntpDbService> _logger;
    private readonly TimeProvider _timeProvider;
    private int _started;
    private int _accepting;
    private int _disposed;

    /// <summary>Initializes a new instance of the <see cref="NntpDbService"/> class.</summary>
    public NntpDbService(
        INntpDbConnectionFactory factory,
        IOptions<NntpDbOptions> options,
        ILogger<NntpDbService> logger)
        : this(factory, options, logger, TimeProvider.System)
    {
    }

    /// <summary>Initializes a new instance with an explicit clock (tests).</summary>
    internal NntpDbService(
        INntpDbConnectionFactory factory,
        IOptions<NntpDbOptions> options,
        ILogger<NntpDbService> logger,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _factory = factory;
        _options = options;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public string Name => "NntpDb";

    /// <inheritdoc />
    public Task? Execution => null;

    /// <summary>Gets whether <see cref="StartAsync"/> has completed successfully (tests).</summary>
    internal bool HasStarted => Volatile.Read(ref _started) == 1;

    /// <summary>Gets whether the service is accepting database work (tests).</summary>
    internal bool IsAccepting => Volatile.Read(ref _accepting) == 1;

    /// <summary>Gets how many startup connect attempts ran (tests).</summary>
    internal int StartupConnectAttempts { get; private set; }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            return;
        }

        NntpDbLogMessages.Initializing(_logger);

        try
        {
            await ConnectWithStartupBudgetAsync(_options.Value, cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _accepting, 1);
        }
        catch (Exception ex)
        {
            if (ex is NntpDbConfigurationException configuration)
            {
                NntpDbLogMessages.InvalidConnectionString(_logger, configuration.Reason, configuration);
            }
            else if (ex is not OperationCanceledException)
            {
                NntpDbLogMessages.StartupCheckFailed(_logger, ex);
            }

            Volatile.Write(ref _accepting, 0);
            Interlocked.Exchange(ref _started, 0);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Opens a logical MySQL connection using the configured NntpDB connection string.
    /// </summary>
    /// <param name="cancellationToken">Token used to cancel the open.</param>
    /// <returns>An open logical connection. Dispose returns it to MySqlConnector's pool.</returns>
    public async ValueTask<INntpDbConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _accepting) == 0)
        {
            throw new InvalidOperationException("NntpDB is not accepting database work.");
        }

        return await _factory.OpenAsync(_options.Value.ConnectionString, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return ValueTask.CompletedTask;
        }

        Volatile.Write(ref _accepting, 0);
        NntpDbLogMessages.ShuttingDown(_logger);
        NntpDbLogMessages.Stopped(_logger);
        return ValueTask.CompletedTask;
    }

    private async Task ConnectWithStartupBudgetAsync(NntpDbOptions options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        NntpDbConnectionString.Validate(options.ConnectionString);

        var deadline = _timeProvider.GetUtcNow() + options.StartupTimeout;
        StartupConnectAttempts = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StartupConnectAttempts++;
            try
            {
                await using var connection = await _factory
                    .OpenAsync(options.ConnectionString, cancellationToken)
                    .ConfigureAwait(false);
                var result = await connection.SelectOneAsync(cancellationToken).ConfigureAwait(false);
                if (result != 1)
                {
                    throw new NntpDbUnavailableException("MySQL health query SELECT 1 did not return 1.");
                }

                NntpDbLogMessages.StartupCheckSucceeded(_logger);
                return;
            }
            catch (NntpDbConfigurationException)
            {
                throw;
            }
            catch (NntpDbAuthenticationException)
            {
                throw;
            }
            catch (NntpDbUnavailableException ex) when (IsSelectOneFailure(ex))
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (_timeProvider.GetUtcNow() >= deadline)
                {
                    throw new NntpDbUnavailableException(
                        "NntpDB startup connectivity check failed after the configured startup timeout.",
                        ex);
                }

                NntpDbLogMessages.StartupRetry(_logger, StartupConnectAttempts, ex);
                var remaining = deadline - _timeProvider.GetUtcNow();
                var delay = remaining < StartupRetryDelay ? remaining : StartupRetryDelay;
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    private static bool IsSelectOneFailure(NntpDbUnavailableException exception) =>
        exception.Message.Contains("SELECT 1", StringComparison.Ordinal);
}
