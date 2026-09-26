namespace VectorNNTP.NNTPD.Acme;

/// <summary>
/// Cross-process exclusive lock for one certificate FQDN's issuance and promotion.
/// </summary>
/// <remarks>
/// The lock file is <c>live/{fqdn}/.issuance.lock</c>. Distinct FQDNs use distinct files
/// and can issue concurrently. The same FQDN is mutually exclusive across processes.
/// </remarks>
public sealed class AcmeIssuanceLock : IAsyncDisposable, IDisposable
{
    private const int RetryDelayMilliseconds = 50;

    private readonly FileStream _stream;
    private int _disposed;

    private AcmeIssuanceLock(FileStream stream)
    {
        _stream = stream;
    }

    /// <summary>
    /// Acquires the FQDN-scoped issuance lock, waiting until it is available or cancelled.
    /// </summary>
    /// <param name="stateDir">Shared ACME state root.</param>
    /// <param name="fqdn">Certificate FQDN that owns the lock.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>A held lock that must be disposed to release.</returns>
    public static async Task<AcmeIssuanceLock> AcquireAsync(
        string stateDir,
        string fqdn,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDir);
        var identity = CertificateIdentities.NormalizeFqdn(fqdn);
        AcmePaths.EnsureCertificateIdentityLayout(stateDir, identity);
        var path = AcmePaths.IssuanceLockPath(stateDir, identity);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var stream = new FileStream(
                    path,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 4096,
                    FileOptions.None);
                try
                {
                    stream.SetLength(0);
                    var payload = System.Text.Encoding.UTF8.GetBytes(
                        Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + Environment.NewLine);
                    stream.Write(payload);
                    stream.Flush(flushToDisk: true);
                    return new AcmeIssuanceLock(stream);
                }
                catch
                {
                    stream.Dispose();
                    throw;
                }
            }
            catch (IOException)
            {
                await Task.Delay(RetryDelayMilliseconds, cancellationToken).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException)
            {
                await Task.Delay(RetryDelayMilliseconds, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _stream.Dispose();
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
