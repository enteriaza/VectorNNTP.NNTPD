namespace VectorNNTP.Common.Acme
{
    /// <summary>
    /// Cross-process exclusive lock for one certificate FQDN's issuance and promotion.
    /// </summary>
    /// <remarks>
    /// The lock file is <c>live/{fqdn}/.issuance.lock</c>. Distinct FQDNs use distinct files
    /// and can issue concurrently. The same FQDN is mutually exclusive across processes.
    /// </remarks>
    internal sealed class AcmeIssuanceLock : IAsyncDisposable, IDisposable
    {
        /// <summary>Milliseconds between sharing-violation retries. The wait observes <c>cancellationToken</c>.</summary>
        private const int RetryDelayMilliseconds = 50;

        /// <summary>Open lock file at <c>live/{fqdn}/.issuance.lock</c>. Disposing it releases the lock.</summary>
        private readonly FileStream _stream;

        /// <summary>Non-zero after <see cref="Dispose"/> has run.</summary>
        private int _disposed;

        /// <summary>Takes ownership of an already exclusive <paramref name="stream"/>.</summary>
        /// <param name="stream">Lock stream. Disposed by <see cref="Dispose"/>.</param>
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
        internal static async Task<AcmeIssuanceLock> AcquireAsync(
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

        /// <summary>Closes the lock stream, releasing the FQDN issuance lock. A second call does nothing.</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            _stream.Dispose();
        }

        /// <summary>Releases the lock synchronously and returns a completed task.</summary>
        /// <returns>A completed <see cref="ValueTask"/>.</returns>
        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
