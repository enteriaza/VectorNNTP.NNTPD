namespace VectorNNTP.Common.Acme
{
    /// <summary>
    /// Cross-process exclusive lock for one filesystem path.
    /// </summary>
    /// <remarks>
    /// The lock is a <see cref="FileStream"/> opened with <see cref="FileShare.None"/>.
    /// Distinct paths are independent. The same path is mutually exclusive across
    /// processes and AppDomains. Callers wait until the lock is available or cancelled;
    /// sharing violations are not ignored.
    /// </remarks>
    internal sealed class AcmeExclusiveFileLock : IDisposable
    {
        private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(50);

        private readonly FileStream _stream;
        private int _disposed;

        private AcmeExclusiveFileLock(FileStream stream)
        {
            _stream = stream;
        }

        /// <summary>
        /// Acquires an exclusive lock on <paramref name="path"/>, waiting until it is
        /// available or <paramref name="cancellationToken"/> is cancelled.
        /// </summary>
        /// <param name="path">Lock file path. The parent directory is created when missing.</param>
        /// <param name="cancellationToken">Cancels the wait.</param>
        /// <returns>A held lock that must be disposed to release.</returns>
        internal static AcmeExclusiveFileLock Acquire(string path, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);

            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

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
                        return new AcmeExclusiveFileLock(stream);
                    }
                    catch
                    {
                        stream.Dispose();
                        throw;
                    }
                }
                catch (IOException)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    cancellationToken.WaitHandle.WaitOne(RetryDelay);
                }
                catch (UnauthorizedAccessException)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    cancellationToken.WaitHandle.WaitOne(RetryDelay);
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
    }
}
