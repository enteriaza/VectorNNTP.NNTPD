using VectorNNTP.NNTPD.Core;

namespace VectorNNTP.NNTPD.Acme;

/// <summary>
/// NNTPD lifecycle wrapper around the shared ACME certificate implementation.
/// </summary>
public sealed class AcmeCertificateApplicationService : IApplicationService, IAsyncDisposable
{
    private readonly AcmeCertificateService _inner;

    /// <summary>Initializes a new wrapper.</summary>
    public AcmeCertificateApplicationService(AcmeCertificateService inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
    }

    /// <inheritdoc />
    public string Name => _inner.Name;

    /// <inheritdoc />
    public Task? Execution => _inner.Execution;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken) => _inner.StartAsync(cancellationToken);

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => _inner.StopAsync(cancellationToken);

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _inner.DisposeAsync();
}
