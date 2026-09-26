namespace VectorNNTP.NNTPD.Acme;

/// <summary>
/// Signals that a usable ACME certificate has been loaded or issued.
/// </summary>
/// <remarks>
/// TLS-only hosts must not open a listener until <see cref="IsReady"/> is
/// <see langword="true"/>. The flag is set only after successful publish.
/// </remarks>
public interface IAcmeCertificateReadiness
{
    /// <summary>Gets whether a usable certificate has been published.</summary>
    bool IsReady { get; }

    /// <summary>Marks the certificate as ready for TLS listeners.</summary>
    void MarkReady();
}

/// <summary>Process-wide ACME readiness gate.</summary>
public sealed class AcmeCertificateReadiness : IAcmeCertificateReadiness
{
    private int _ready;

    /// <inheritdoc />
    public bool IsReady => Volatile.Read(ref _ready) != 0;

    /// <inheritdoc />
    public void MarkReady() => Volatile.Write(ref _ready, 1);
}
