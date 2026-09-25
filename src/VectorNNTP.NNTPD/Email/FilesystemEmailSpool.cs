using Microsoft.Extensions.Options;
using VectorNNTP.NNTPD.Configuration;

namespace VectorNNTP.NNTPD.Email;

/// <summary>Filesystem spool under <c>spool/smtp</c> (configurable).</summary>
/// <remarks>
/// <para>
/// The delivery scanner enumerates only the spool directory itself
/// (<see cref="SearchOption.TopDirectoryOnly"/>), never <c>failed/</c>:
/// </para>
/// <list type="table">
/// <listheader><term>Suffix / path</term><description>Meaning</description></listheader>
/// <item><term><c>.tmp</c></term><description>Incomplete atomic write. Never delivered.</description></item>
/// <item><term><c>.eml</c></term><description>Pending outbound message. The only automatically deliverable state.</description></item>
/// <item><term><c>.wrk</c></term><description>In-flight claim. Crash recovery may rename this back to <c>.eml</c>.</description></item>
/// <item><term><c>failed/*.eml</c></term><description>Permanent failure. Retained for operators. Never automatically delivered or moved back.</description></item>
/// <item><term><c>.delivered</c></term><description>SMTP already accepted the message but local delete failed. Retained for operators. Never automatically delivered, claimed, or renamed to <c>.eml</c> (retry would duplicate).</description></item>
/// </list>
/// </remarks>
public sealed class FilesystemEmailSpool : IEmailSpool
{
    private readonly EmailSpoolOptions _options;
    private readonly ILogger<FilesystemEmailSpool> _logger;
    private readonly SemaphoreSlim _signal = new(0);
    private readonly object _claimGate = new();
    private readonly HashSet<string> _claimed = new(StringComparer.Ordinal);
    private readonly HashSet<string> _deferred = new(StringComparer.Ordinal);
    private readonly string _directory;
    private readonly string _failedDirectory;
    private int _accepting = 1;

    /// <summary>Initializes a new filesystem spool from <see cref="EmailOptions"/>.</summary>
    public FilesystemEmailSpool(IOptions<EmailOptions> options, ILogger<FilesystemEmailSpool> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options.Value.Spool ?? new EmailSpoolOptions();
        _logger = logger;
        _directory = Path.GetFullPath(
            string.IsNullOrWhiteSpace(_options.Directory)
                ? EmailSpoolOptions.DefaultDirectory
                : _options.Directory.Trim());
        _failedDirectory = Path.Combine(_directory, "failed");
    }

    /// <inheritdoc />
    public string Directory => _directory;

    /// <inheritdoc />
    public bool IsAccepting => Volatile.Read(ref _accepting) != 0;

    /// <inheritdoc />
    public void EnsureDirectory()
    {
        System.IO.Directory.CreateDirectory(_directory);
        System.IO.Directory.CreateDirectory(_failedDirectory);
    }

    /// <inheritdoc />
    public void StopAccepting() => Volatile.Write(ref _accepting, 0);

    /// <inheritdoc />
    public void RecoverClaims()
    {
        lock (_claimGate)
        {
            RecoverClaimsCore();
        }
    }

    private void RecoverClaimsCore()
    {
        EnsureDirectory();
        foreach (var work in System.IO.Directory.EnumerateFiles(
            _directory,
            "*.wrk",
            SearchOption.TopDirectoryOnly))
        {
            var id = Path.GetFileNameWithoutExtension(work);
            lock (_claimed)
            {
                if (_claimed.Contains(id))
                {
                    continue;
                }
            }

            var eml = Path.ChangeExtension(work, ".eml");
            try
            {
                if (File.Exists(eml))
                {
                    File.Delete(work);
                    continue;
                }

                File.Move(work, eml);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                EmailLogMessages.SpoolRecoverFailed(_logger, ex, Path.GetFileName(work));
            }
        }
    }

    /// <inheritdoc />
    public async Task<string> WriteAsync(EmailWorkItem item, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!IsAccepting)
        {
            throw new InvalidOperationException("The email spool is not accepting submissions.");
        }

        EnsureDirectory();
        var payload = EmailSpoolRecord.Serialize(item);
        for (var attempt = 0; attempt < 8; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = EmailSpoolFileName.NewId();
            var tmp = Path.Combine(_directory, id + ".tmp");
            var eml = Path.Combine(_directory, id + ".eml");
            if (File.Exists(eml) || File.Exists(tmp) || File.Exists(Path.Combine(_directory, id + ".wrk")))
            {
                continue;
            }

            try
            {
                await using (var stream = new FileStream(
                    tmp,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 4096,
                    FileOptions.Asynchronous))
                {
                    await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    stream.Flush(flushToDisk: true);
                }

                File.Move(tmp, eml);
                Signal();
                return eml;
            }
            catch (Exception ex)
            {
                TryDelete(tmp);
                if (ex is IOException && attempt < 7)
                {
                    continue;
                }

                throw;
            }
        }

        throw new IOException("Could not allocate a unique email spool filename.");
    }

    /// <inheritdoc />
    public EmailSpoolClaim? TryClaimNext()
    {
        lock (_claimGate)
        {
            return TryClaimNextCore();
        }
    }

    private EmailSpoolClaim? TryClaimNextCore()
    {
        EnsureDirectory();
        string[] pending;
        try
        {
            pending = System.IO.Directory.GetFiles(
                _directory,
                "*.eml",
                SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        Array.Sort(pending, StringComparer.Ordinal);
        foreach (var eml in pending)
        {
            var id = Path.GetFileNameWithoutExtension(eml);
            if (!EmailSpoolFileName.IsId(id))
            {
                continue;
            }

            lock (_deferred)
            {
                if (_deferred.Contains(id))
                {
                    continue;
                }
            }

            var wrk = Path.Combine(_directory, id + ".wrk");
            try
            {
                File.Move(eml, wrk);
                lock (_claimed)
                {
                    _claimed.Add(id);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(wrk);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ForgetClaim(id);
                TryReleasePath(wrk, eml);
                continue;
            }

            if (!EmailSpoolRecord.TryParse(bytes, out var item) || item is null)
            {
                var claim = new EmailSpoolClaim(id, wrk, new EmailWorkItem
                {
                    EncodedMessage = bytes,
                    EnvelopeSender = new EmailAddress("invalid@invalid.invalid"),
                    Recipients = [new EmailAddress("invalid@invalid.invalid")],
                });
                Quarantine(claim);
                EmailLogMessages.SpoolMalformed(_logger, id);
                continue;
            }

            return new EmailSpoolClaim(id, wrk, item);
        }

        return null;
    }

    /// <inheritdoc />
    public void Release(EmailSpoolClaim claim)
    {
        ArgumentNullException.ThrowIfNull(claim);
        ForgetClaim(claim.Id);
        var eml = Path.Combine(_directory, claim.Id + ".eml");
        TryReleasePath(claim.WorkPath, eml);
    }

    /// <inheritdoc />
    public void Defer(EmailSpoolClaim claim)
    {
        ArgumentNullException.ThrowIfNull(claim);
        lock (_deferred)
        {
            _deferred.Add(claim.Id);
        }

        Release(claim);
    }

    /// <inheritdoc />
    public void Quarantine(EmailSpoolClaim claim)
    {
        ArgumentNullException.ThrowIfNull(claim);
        ForgetClaim(claim.Id);
        EnsureDirectory();
        var dest = Path.Combine(_failedDirectory, claim.Id + ".eml");
        try
        {
            if (File.Exists(dest))
            {
                File.Delete(dest);
            }

            File.Move(claim.WorkPath, dest);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            EmailLogMessages.SpoolQuarantineFailed(_logger, ex, claim.Id);
        }
    }

    /// <inheritdoc />
    public void Complete(EmailSpoolClaim claim)
    {
        ArgumentNullException.ThrowIfNull(claim);
        ForgetClaim(claim.Id);
        try
        {
            File.Delete(claim.WorkPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var delivered = Path.Combine(_directory, claim.Id + ".delivered");
            try
            {
                if (File.Exists(delivered))
                {
                    File.Delete(claim.WorkPath);
                    return;
                }

                File.Move(claim.WorkPath, delivered);
            }
            catch (Exception moveEx) when (moveEx is IOException or UnauthorizedAccessException)
            {
                EmailLogMessages.SpoolDeleteAfterAcceptFailed(_logger, moveEx, claim.Id);
                return;
            }

            EmailLogMessages.SpoolDeleteAfterAcceptFailed(_logger, ex, claim.Id);
        }
    }

    /// <inheritdoc />
    public void Signal()
    {
        try
        {
            _signal.Release();
        }
        catch (SemaphoreFullException)
        {
        }
    }

    /// <inheritdoc />
    public async Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            _ = await _signal.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
    }

    private void ForgetClaim(string id)
    {
        lock (_claimed)
        {
            _claimed.Remove(id);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryReleasePath(string work, string eml)
    {
        try
        {
            if (!File.Exists(work))
            {
                return;
            }

            if (File.Exists(eml))
            {
                File.Delete(work);
                return;
            }

            File.Move(work, eml);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
