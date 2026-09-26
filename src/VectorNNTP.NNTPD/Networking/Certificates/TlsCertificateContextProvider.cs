using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using VectorNNTP.NNTPD.Acme;

namespace VectorNNTP.NNTPD.Networking.Certificates;

/// <summary>
/// Publishes and leases immutable <see cref="SslStreamCertificateContext"/> instances for TLS use.
/// </summary>
/// <remarks>
/// A successful <see cref="Acquire"/> takes ownership of a reference to the currently published context.
/// That <em>object</em> remains alive until the returned <see cref="TlsCertificateLease"/> is disposed.
/// Rotation and provider disposal do not invalidate already-acquired leases. A lease does not guarantee
/// that the X.509 certificate remains within its <c>NotAfter</c> validity window.
/// </remarks>
public interface ITlsCertificateContextProvider
{
    /// <summary>Gets whether a certificate context is currently published.</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Acquires a lease on the currently published certificate context.
    /// </summary>
    /// <returns>
    /// A lease that keeps the leased context object alive until disposed. Callers choose the hold duration
    /// (for example handshake-only or for the full TLS connection); dispose when finished with the context.
    /// </returns>
    /// <exception cref="InvalidOperationException">Thrown when no context is available.</exception>
    /// <exception cref="ObjectDisposedException">Thrown when the provider has been disposed.</exception>
    TlsCertificateLease Acquire();

    /// <summary>
    /// Builds a new context from a validated PFX and atomically publishes it for subsequent <see cref="Acquire"/> calls.
    /// </summary>
    /// <remarks>
    /// Existing leases continue to reference the previous context until they are disposed; that retired
    /// context remains alive while those leases exist.
    /// </remarks>
    void PublishFromPfx(ReadOnlySpan<byte> pfxBytes, string password);
}

/// <summary>
/// Reference-counted lease over a published <see cref="SslStreamCertificateContext"/>.
/// </summary>
/// <remarks>
/// <para>
/// Acquiring a lease establishes ownership of one reference to a published (or already-leased) holder.
/// The leased context object remains valid for the lifetime of this lease: certificate rotation and
/// provider disposal retire the holder from publication but do not dispose it while this lease is outstanding.
/// </para>
/// <para>
/// Dispose releases that reference. The underlying context is disposed only after the last reference
/// (including the provider's publication reference) is released. This contract covers managed object
/// lifetime only; it does not keep an X.509 certificate cryptographically valid past <c>NotAfter</c>.
/// </para>
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
    /// <exception cref="ObjectDisposedException">Thrown when this lease has been disposed.</exception>
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

    /// <summary>
    /// Releases this lease's reference to the certificate context holder.
    /// </summary>
    /// <remarks>
    /// Safe to call more than once. After dispose, <see cref="Context"/> and related members throw
    /// <see cref="ObjectDisposedException"/>. The holder (and context) may remain alive for other leases
    /// or until the provider drops its publication reference.
    /// </remarks>
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
/// <para>
/// <see cref="DisposeAsync"/> retires the current publication and drops the provider's reference; outstanding
/// leases keep their leased context objects alive until those leases are disposed.
/// </para>
/// </remarks>
public sealed class TlsCertificateContextProvider : ITlsCertificateContextProvider, IAcmeCertificatePublisher, IAsyncDisposable
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

        NetworkingLogMessages.TlsCertificateContextPublished(_logger, holder.Generation);

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
/// Lifecycle: Published (publication ref) → optional leases → Retired (removed from provider; no new
/// <see cref="AddRef"/>) → last release → disposed exactly once. Retirement does not dispose while
/// leases still hold references.
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
