using System.Net;
using VectorNNTP.NNTPD.Authentication.Sasl;
using VectorNNTP.NNTPD.NntpDb;
using VectorNNTP.NNTPD.Session;
using VectorNNTP.NNTPD.Session.Authentication;

namespace VectorNNTP.NNTPD.Authentication;

/// <summary>
/// Validates AUTHINFO PASS / SASL password mechanisms and finalizes SCRAM/CRAM against MySQL.
/// </summary>
public sealed class MySqlNntpCredentialValidator
{
    /// <summary>Reader + posting privileges granted to every successful MySQL account.</summary>
    public static NntpAuthorization ReaderPrivileges { get; } = new(
        isAuthenticated: true,
        authorizedReader: true,
        authorizedTransit: false,
        postingPermitted: true,
        streamingPermitted: false);

    private readonly INntpUserRecordStore _store;
    private readonly NntpUserRecordCache _cache;
    private readonly ILogger<MySqlNntpCredentialValidator> _logger;

    /// <summary>Initializes a new instance of the <see cref="MySqlNntpCredentialValidator"/> class.</summary>
    public MySqlNntpCredentialValidator(INntpUserRecordStore store, ILogger<MySqlNntpCredentialValidator> logger)
        : this(store, logger, new NntpUserRecordCache())
    {
    }

    /// <summary>Initializes a new instance with an explicit burst cache (tests).</summary>
    internal MySqlNntpCredentialValidator(
        INntpUserRecordStore store,
        ILogger<MySqlNntpCredentialValidator> logger,
        NntpUserRecordCache cache)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(cache);
        _store = store;
        _logger = logger;
        _cache = cache;
    }

    /// <summary>Validates a cleartext password for AUTHINFO PASS, PLAIN, or LOGIN.</summary>
    public async ValueTask<NntpAuthenticationResult> ValidatePasswordAsync(
        string mechanism,
        string username,
        string password,
        IPAddress clientIp,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(mechanism);
        if (string.IsNullOrWhiteSpace(username))
        {
            return NntpAuthenticationResult.Failed;
        }

        ArgumentNullException.ThrowIfNull(password);
        ArgumentNullException.ThrowIfNull(clientIp);
        var ip = FormatClientIp(clientIp);
        try
        {
            var fingerprint = NntpUserRecordCache.ComputePasswordFingerprint(password);
            if (!_cache.TryGet(username, fingerprint, out var record))
            {
                record = await _store.TryGetUserAsync(username, cancellationToken).ConfigureAwait(false);
            }

            if (record is null
                || !record.IsEnabled
                || !record.AllowAuthPlain
                || !NntpPasswordComparer.EqualsAscii(record.AccountPassword, password))
            {
                AuthenticationLogMessages.InvalidCredentials(_logger, mechanism, username, ip);
                return NntpAuthenticationResult.Failed;
            }

            _cache.Put(username, fingerprint, record);
            return Succeed(mechanism, record, ip);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AuthenticationLogMessages.BackendFailed(_logger, ex, mechanism, username);
            return NntpAuthenticationResult.TransientFailure;
        }
    }

    /// <summary>
    /// Loads the <c>nntpusers</c> row for a SCRAM username without applying
    /// enabled/SCRAM policy. Callers decide real vs dummy verifier.
    /// </summary>
    public async ValueTask<NntpUserRecord?> TryLoadUserAsync(
        string username,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            return null;
        }

        try
        {
            return await _store.TryGetUserAsync(username, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (NntpDbUnavailableException)
        {
            throw;
        }
    }

    /// <summary>Loads SCRAM material when the account may use SCRAM-SHA-256.</summary>
    public async ValueTask<NntpUserRecord?> TryGetScramRecordAsync(
        string username,
        CancellationToken cancellationToken = default)
    {
        var record = await TryLoadUserAsync(username, cancellationToken).ConfigureAwait(false);
        if (!ScramStoredCredential.TryGetAuthorized(record, out _))
        {
            return null;
        }

        return record;
    }

    /// <summary>Loads a CRAM-MD5 HMAC key from the decrypted password.</summary>
    public async ValueTask<NntpUserRecord?> TryGetCramRecordAsync(
        string username,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            return null;
        }

        try
        {
            var record = await _store.TryGetUserAsync(username, cancellationToken).ConfigureAwait(false);
            if (record is null
                || !record.IsEnabled
                || !record.AllowAuthPlain
                || !NntpPasswordComparer.IsAscii(record.AccountPassword)
                || record.AccountPassword.Length == 0)
            {
                return null;
            }

            return record;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (NntpDbUnavailableException)
        {
            throw;
        }
    }

    /// <summary>Finalizes SCRAM or CRAM after wire-level proof verification.</summary>
    public async ValueTask<NntpAuthenticationResult> CompleteSaslAccountAsync(
        string mechanism,
        string username,
        IPAddress clientIp,
        bool requireScram,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            return NntpAuthenticationResult.Failed;
        }

        ArgumentNullException.ThrowIfNull(clientIp);
        var ip = FormatClientIp(clientIp);
        try
        {
            var record = await _store.TryGetUserAsync(username, cancellationToken).ConfigureAwait(false);
            if (record is null
                || !record.IsEnabled
                || !(requireScram ? record.AllowAuthScram256 : record.AllowAuthPlain))
            {
                AuthenticationLogMessages.InvalidCredentials(_logger, mechanism, username, ip);
                return NntpAuthenticationResult.Failed;
            }

            _cache.Put(username, NntpUserRecordCache.UsernameOnlyFingerprint, record);
            return Succeed(mechanism, record, ip);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AuthenticationLogMessages.BackendFailed(_logger, ex, mechanism, username);
            return NntpAuthenticationResult.TransientFailure;
        }
    }

    private NntpAuthenticationResult Succeed(string mechanism, NntpUserRecord record, string clientIp)
    {
        AuthenticationLogMessages.Succeeded(
            _logger,
            mechanism,
            record.AccountName,
            clientIp,
            record.AccountType,
            record.CustomerId);
        return NntpAuthenticationResult.Success(
            record.AccountName,
            ReaderPrivileges,
            NntpAccountPolicy.FromRecord(record));
    }

    internal static string FormatClientIp(IPAddress clientIp) =>
        InMemoryNntpSessionAdmissionTracker.FormatSourceAddress(clientIp);
}
