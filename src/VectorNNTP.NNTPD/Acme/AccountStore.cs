using System.Text.Json;

namespace VectorNNTP.NNTPD.Acme;

/// <summary>
/// Loads and persists ACME account PKCS#8 DER key + registration metadata.
/// </summary>
/// <remarks>
/// Terminology: the persisted credential is an <strong>ACME account private key</strong> (PKCS#8 DER),
/// not an X.509 "account certificate". Metadata is stored beside the key in <c>registration.json</c>.
/// </remarks>
public sealed class AccountStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly string _stateDir;

    /// <summary>Initializes a new instance of the <see cref="AccountStore"/> class.</summary>
    public AccountStore(string stateDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDir);
        _stateDir = stateDir;
        AcmePaths.EnsureStateLayout(stateDir);
    }

    /// <summary>
    /// Returns persisted account state, or <see langword="null"/> when absent.
    /// Malformed or incomplete state throws without deleting files.
    /// </summary>
    public AcmeAccountState? Load()
    {
        var keyPath = AcmePaths.AccountKeyPath(_stateDir);
        var metaPath = AcmePaths.AccountMetaPath(_stateDir);
        var keyExists = File.Exists(keyPath);
        var metaExists = File.Exists(metaPath);
        if (!keyExists && !metaExists)
        {
            return null;
        }

        if (!keyExists || !metaExists)
        {
            throw new AcmeAccountException("incomplete_account", "account key or registration metadata missing");
        }

        try
        {
            var privateKeyDer = File.ReadAllBytes(keyPath);
            if (privateKeyDer.Length == 0 || DerCrypto.LooksLikePem(privateKeyDer))
            {
                throw new AcmeAccountException("malformed_account", "account key must be non-empty DER");
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(metaPath));
            var root = doc.RootElement;
            var accountUri = root.GetProperty("account_uri").GetString() ?? string.Empty;
            var directoryUrl = root.GetProperty("directory_url").GetString() ?? string.Empty;
            var registrationBody = root.TryGetProperty("registration_body", out var body)
                ? body.GetString() ?? string.Empty
                : string.Empty;

            if (string.IsNullOrWhiteSpace(accountUri) || string.IsNullOrWhiteSpace(directoryUrl))
            {
                throw new AcmeAccountException("malformed_account", "empty required fields");
            }

            return new AcmeAccountState(accountUri, directoryUrl, privateKeyDer, registrationBody);
        }
        catch (AcmeAccountException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new AcmeAccountException("malformed_account", ex.GetType().Name);
        }
    }

    /// <summary>Atomically persists account key and metadata; refuses to overwrite a different key.</summary>
    public void Save(AcmeAccountState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        AcmePaths.EnsureStateLayout(_stateDir);

        var keyPath = AcmePaths.AccountKeyPath(_stateDir);
        if (File.Exists(keyPath))
        {
            var existing = File.ReadAllBytes(keyPath);
            if (!existing.AsSpan().SequenceEqual(state.PrivateKeyDer))
            {
                throw new AcmeAccountException(
                    "account_key_conflict",
                    "refusing to overwrite existing account private key");
            }
        }

        try
        {
            AtomicFile.WriteBytes(keyPath, state.PrivateKeyDer);
            var payload = new Dictionary<string, string>
            {
                ["account_uri"] = state.AccountUri,
                ["directory_url"] = state.DirectoryUrl,
                ["registration_body"] = state.RegistrationBody ?? string.Empty,
            };
            AtomicFile.WriteText(
                AcmePaths.AccountMetaPath(_stateDir),
                JsonSerializer.Serialize(payload, JsonOptions) + Environment.NewLine);
        }
        catch (IOException ex)
        {
            throw new AcmeStorageException("account_persist_failed", ex.GetType().Name);
        }
    }

    /// <summary>
    /// Create-or-load the local ACME account (pending-key crash recovery matching pyNNTPD).
    /// </summary>
    public AcmeAccountState EnsureRegistered(
        string directoryUrl,
        Func<byte[]> generateKeyDer,
        Func<byte[], (string AccountUri, string RegistrationBody)> register,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryUrl);
        ArgumentNullException.ThrowIfNull(generateKeyDer);
        ArgumentNullException.ThrowIfNull(register);

        cancellationToken.ThrowIfCancellationRequested();

        var existing = LoadCompleteOrNone();
        if (existing is not null)
        {
            if (!string.Equals(existing.DirectoryUrl, directoryUrl, StringComparison.Ordinal))
            {
                throw new AcmeAccountException(
                    "directory_mismatch",
                    "persisted account directory differs from configuration");
            }

            ClearPendingRegistration();
            return existing;
        }

        var keyDer = ResumePrivateKey(directoryUrl);
        if (keyDer is null)
        {
            keyDer = generateKeyDer();
            if (keyDer.Length == 0)
            {
                throw new AcmeAccountException("malformed_pending", "generated empty key");
            }

            SavePendingRegistration(keyDer, directoryUrl);
        }

        cancellationToken.ThrowIfCancellationRequested();

        string accountUri;
        string registrationBody;
        try
        {
            (accountUri, registrationBody) = register(keyDer);
        }
        catch (AcmeAccountException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new AcmeAccountException("registration_failed", ex.GetType().Name);
        }

        if (string.IsNullOrWhiteSpace(accountUri))
        {
            throw new AcmeAccountException("registration_failed", "empty account_uri");
        }

        existing = LoadCompleteOrNone();
        if (existing is not null)
        {
            if (!existing.PrivateKeyDer.AsSpan().SequenceEqual(keyDer))
            {
                throw new AcmeAccountException(
                    "account_key_conflict",
                    "persisted account key differs from registration key");
            }

            ClearPendingRegistration();
            return existing;
        }

        var state = new AcmeAccountState(accountUri, directoryUrl, keyDer, registrationBody ?? string.Empty);
        Save(state);
        ClearPendingRegistration();
        return Load() ?? throw new AcmeAccountException("account_persist_failed", "account missing after save");
    }

    private AcmeAccountState? LoadCompleteOrNone()
    {
        try
        {
            return Load();
        }
        catch (AcmeAccountException ex) when (ex.Category == "incomplete_account")
        {
            return null;
        }
    }

    private byte[]? ResumePrivateKey(string directoryUrl)
    {
        var pendingPath = AcmePaths.AccountPendingKeyPath(_stateDir);
        if (File.Exists(pendingPath))
        {
            var pendingDir = PendingDirectoryUrl();
            if (pendingDir is not null
                && !string.Equals(pendingDir, directoryUrl, StringComparison.Ordinal))
            {
                throw new AcmeAccountException(
                    "directory_mismatch",
                    "pending registration directory differs from configuration");
            }

            var data = File.ReadAllBytes(pendingPath);
            if (data.Length == 0 || DerCrypto.LooksLikePem(data))
            {
                throw new AcmeAccountException("malformed_pending", "empty or PEM pending private key");
            }

            return data;
        }

        var keyPath = AcmePaths.AccountKeyPath(_stateDir);
        var metaPath = AcmePaths.AccountMetaPath(_stateDir);
        if (File.Exists(keyPath) && !File.Exists(metaPath))
        {
            var data = File.ReadAllBytes(keyPath);
            if (data.Length == 0)
            {
                throw new AcmeAccountException("incomplete_account", "empty account key");
            }

            return data;
        }

        return null;
    }

    private void SavePendingRegistration(byte[] privateKeyDer, string directoryUrl)
    {
        AcmePaths.EnsureStateLayout(_stateDir);
        AtomicFile.WriteBytes(AcmePaths.AccountPendingKeyPath(_stateDir), privateKeyDer);
        var payload = new Dictionary<string, object>
        {
            ["version"] = 1,
            ["directory_url"] = directoryUrl,
        };
        AtomicFile.WriteText(
            AcmePaths.AccountPendingMetaPath(_stateDir),
            JsonSerializer.Serialize(payload, JsonOptions) + Environment.NewLine);
    }

    private void ClearPendingRegistration()
    {
        AtomicFile.TryDelete(AcmePaths.AccountPendingKeyPath(_stateDir));
        AtomicFile.TryDelete(AcmePaths.AccountPendingMetaPath(_stateDir));
    }

    private string? PendingDirectoryUrl()
    {
        var path = AcmePaths.AccountPendingMetaPath(_stateDir);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var value = doc.RootElement.GetProperty("directory_url").GetString();
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new AcmeAccountException("malformed_pending", "empty directory_url");
            }

            return value;
        }
        catch (AcmeAccountException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException)
        {
            throw new AcmeAccountException("malformed_pending", ex.GetType().Name);
        }
    }
}
