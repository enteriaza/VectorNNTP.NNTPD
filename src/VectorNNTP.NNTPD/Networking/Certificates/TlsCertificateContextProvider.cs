using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using VectorNNTP.NNTPD.Acme;

namespace VectorNNTP.NNTPD.Networking.Certificates;

/// <summary>
/// Publishes and leases immutable <see cref="SslStreamCertificateContext"/> instances for TLS handshakes.
/// </summary>
public interface ITlsCertificateContextProvider
{
    /// <summary>Gets whether a certificate context is currently published.</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Acquires a lease on the current context for one handshake (or inspection). Caller must dispose the lease.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when no context is available.</exception>
    TlsCertificateLease Acquire();

    /// <summary>
    /// Builds a new context from a validated PFX and atomically publishes it for new handshakes.
    /// </summary>
    void PublishFromPfx(ReadOnlySpan<byte> pfxBytes, string password);
}

/// <summary>
/// Reference-counted lease over a published <see cref="SslStreamCertificateContext"/>.
/// </summary>
/// <remarks>
/// Dispose releases the lease. The underlying context is disposed only after the last lease
/// (including the publisher's own publication reference) is released.
/// </remarks>
public sealed class TlsCertificateLease : IDisposable
{
    private TlsCertificateHolder? _holder;

    internal TlsCertificateLease(TlsCertificateHolder holder)
    {
        _holder = holder;
        holder.AddRef();
    }

    /// <summary>Gets the immutable certificate context for TLS authentication.</summary>
    public SslStreamCertificateContext Context =>
        (_holder ?? throw new ObjectDisposedException(nameof(TlsCertificateLease))).Context;

    /// <summary>Gets the holder generation (tests).</summary>
    internal int Generation =>
        (_holder ?? throw new ObjectDisposedException(nameof(TlsCertificateLease))).Generation;

    /// <summary>Gets whether the underlying holder has been disposed (tests).</summary>
    internal bool HolderIsDisposed =>
        (_holder ?? throw new ObjectDisposedException(nameof(TlsCertificateLease))).IsDisposed;

    /// <summary>Gets how many times the holder committed disposal (tests).</summary>
    internal int HolderDisposeCount =>
        (_holder ?? throw new ObjectDisposedException(nameof(TlsCertificateLease))).DisposeCount;

    /// <summary>Gets the leased holder for lifetime tests.</summary>
    internal TlsCertificateHolder Holder =>
        _holder ?? throw new ObjectDisposedException(nameof(TlsCertificateLease));

    /// <inheritdoc />
    public void Dispose()
    {
        var holder = Interlocked.Exchange(ref _holder, null);
        holder?.Release();
    }
}

/// <summary>Default atomic publisher for TLS server certificate contexts.</summary>
/// <remarks>
/// <para>
/// Acquire and publication swap share <see cref="_gate"/> so a successful <see cref="Acquire"/> always
/// increments the holder refcount while the publication reference is still held. Retirement (removal from
/// <see cref="_current"/>) therefore cannot race a new lease into a disposing holder.
/// </para>
/// <para>
/// PFX load / context construction run outside the gate. Holder disposal after the last release also runs
/// outside the gate so expensive crypto dispose does not block unrelated acquires of a newer generation.
/// </para>
/// </remarks>
public sealed class TlsCertificateContextProvider : ITlsCertificateContextProvider, IAsyncDisposable
{
    private readonly ILogger<TlsCertificateContextProvider> _logger;
    private readonly object _gate = new();
    private TlsCertificateHolder? _current;
    private int _disposed;

    /// <summary>Initializes a new instance of the <see cref="TlsCertificateContextProvider"/> class.</summary>
    public TlsCertificateContextProvider(ILogger<TlsCertificateContextProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <inheritdoc />
    public bool IsAvailable
    {
        get
        {
            lock (_gate)
            {
                return _current is not null && _disposed == 0;
            }
        }
    }

    /// <inheritdoc />
    public TlsCertificateLease Acquire()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            var current = _current
                          ?? throw new InvalidOperationException("No TLS certificate context is published.");
            // AddRef under the gate while the publication reference is still held (refs >= 1).
            return new TlsCertificateLease(current);
        }
    }

    /// <inheritdoc />
    public void PublishFromPfx(ReadOnlySpan<byte> pfxBytes, string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        // Construct fully outside the gate; failed construction leaves the prior context untouched.
        var holder = TlsCertificateHolder.CreateFromPfx(pfxBytes, password);
        TlsCertificateHolder? previous;
        lock (_gate)
        {
            if (_disposed != 0)
            {
                // Provider shut down during construction — drop the unused holder.
                holder.Release();
                throw new ObjectDisposedException(nameof(TlsCertificateContextProvider));
            }

            previous = _current;
            _current = holder;
            previous?.Retire();
        }

        _logger.LogInformation(
            "TLS certificate context published (generation={Generation}).",
            holder.Generation);

        // Drop the publisher's publication reference outside the gate (may dispose if no leases remain).
        previous?.Release();
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return ValueTask.CompletedTask;
        }

        TlsCertificateHolder? current;
        lock (_gate)
        {
            current = _current;
            _current = null;
            current?.Retire();
        }

        current?.Release();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Reference-counted holder for one immutable certificate context.
/// </summary>
/// <remarks>
/// Lifecycle: Published (publication ref) → optional leases → Retired (removed from provider) →
/// last release → disposed exactly once. Retired holders reject <see cref="AddRef"/>.
/// </remarks>
internal sealed class TlsCertificateHolder
{
    private static int s_generation;

    private int _refs = 1; // publication reference
    private int _retired;
    private int _disposed;
    private int _disposeCount;

    private TlsCertificateHolder(
        SslStreamCertificateContext context,
        X509Certificate2 leaf,
        X509Certificate2Collection ownedCerts,
        int generation)
    {
        Context = context;
        Leaf = leaf;
        _ownedCerts = ownedCerts;
        Generation = generation;
    }

    private readonly X509Certificate2Collection _ownedCerts;

    public SslStreamCertificateContext Context { get; }

    public X509Certificate2 Leaf { get; }

    public int Generation { get; }

    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>Gets how many times <see cref="DisposeCore"/> committed disposal (tests).</summary>
    public int DisposeCount => Volatile.Read(ref _disposeCount);

    public static TlsCertificateHolder CreateFromPfx(ReadOnlySpan<byte> pfxBytes, string password)
    {
        // EphemeralKeySet is unsuitable for SslStreamCertificateContext / server handshake on Windows.
        // Use an in-process user key container for the published TLS context only.
        const X509KeyStorageFlags tlsFlags =
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.UserKeySet;

        X509Certificate2Collection collection;
        try
        {
            collection = X509CertificateLoader.LoadPkcs12Collection(pfxBytes, password, tlsFlags);
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
            throw new AcmeCertificateException("malformed_pfx", ex.GetType().Name);
        }

        X509Certificate2? leaf = null;
        var intermediates = new X509Certificate2Collection();
        try
        {
            foreach (var cert in collection)
            {
                if (leaf is null && cert.HasPrivateKey)
                {
                    leaf = cert;
                    continue;
                }

                intermediates.Add(cert);
            }

            if (leaf is null)
            {
                throw new AcmeCertificateException("malformed_pfx", "missing_leaf_private_key");
            }

            // offline: true avoids OCSP/CRL network during context build at publish time.
            var context = SslStreamCertificateContext.Create(leaf, intermediates, offline: true);
            var generation = Interlocked.Increment(ref s_generation);
            return new TlsCertificateHolder(context, leaf, collection, generation);
        }
        catch
        {
            foreach (var cert in collection)
            {
                cert.Dispose();
            }

            throw;
        }
    }

    /// <summary>Marks this holder as retired; further <see cref="AddRef"/> calls fail.</summary>
    public void Retire() => Volatile.Write(ref _retired, 1);

    public void AddRef()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Volatile.Read(ref _retired) != 0)
        {
            throw new ObjectDisposedException(nameof(TlsCertificateHolder), "Certificate holder is retired.");
        }

        var next = Interlocked.Increment(ref _refs);
        if (next <= 1)
        {
            // Publication/lease refs should never revive a zeroed counter.
            Interlocked.Decrement(ref _refs);
            throw new ObjectDisposedException(nameof(TlsCertificateHolder));
        }
    }

    public void Release()
    {
        var remaining = Interlocked.Decrement(ref _refs);
        if (remaining > 0)
        {
            return;
        }

        if (remaining < 0)
        {
            Interlocked.Increment(ref _refs);
            throw new InvalidOperationException("TLS certificate holder released too many times.");
        }

        DisposeCore();
    }

    private void DisposeCore()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        Interlocked.Increment(ref _disposeCount);

        // SslStreamCertificateContext does not own/dispose certificates in all TFMs; dispose our collection.
        foreach (var cert in _ownedCerts)
        {
            cert.Dispose();
        }

        if (Context is IDisposable disposableContext)
        {
            disposableContext.Dispose();
        }
    }
}
