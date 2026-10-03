using System.Text.Json;

namespace VectorNNTP.Common.Acme
{
    /// <summary>
    /// Loads and persists ACME account PKCS#8 DER key + registration metadata.
    /// </summary>
    /// <remarks>
    /// Terminology: the persisted credential is an <strong>ACME account private key</strong> (PKCS#8 DER),
    /// not an X.509 "account certificate". Metadata is stored beside the key in <c>registration.json</c>.
    /// </remarks>
    internal sealed class AccountStore
    {
        /// <summary>Shared ACME state root. Account files live under <c>account/</c>.</summary>
        private readonly string _stateDir;

        /// <summary>Creates the shared account and journal directories under <paramref name="stateDir"/>.</summary>
        /// <param name="stateDir">ACME state root. Whitespace throws <see cref="ArgumentException"/>.</param>
        internal AccountStore(string stateDir)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(stateDir);
            _stateDir = stateDir;
            AcmePaths.EnsureStateLayout(stateDir);
        }

        /// <summary>
        /// Returns persisted account state, or <see langword="null"/> when absent.
        /// Malformed or incomplete state throws without deleting files.
        /// </summary>
        internal AcmeAccountState? Load()
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
        /// <param name="state">Account URI, directory URL, PKCS#8 key, and registration body. A null registration body is stored as empty.</param>
        /// <exception cref="AcmeAccountException">Thrown with category <c>account_key_conflict</c> when a different key is already on disk.</exception>
        /// <exception cref="AcmeStorageException">Thrown with category <c>account_persist_failed</c> when a write throws <see cref="IOException"/>.</exception>
        internal void Save(AcmeAccountState state)
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
                var payload = new AcmeAccountRegistrationPayload
                {
                    AccountUri = state.AccountUri,
                    DirectoryUrl = state.DirectoryUrl,
                    RegistrationBody = state.RegistrationBody ?? string.Empty,
                };
                AtomicFile.WriteText(
                    AcmePaths.AccountMetaPath(_stateDir),
                    JsonSerializer.Serialize(payload, AcmeJsonSerializerContext.Default.AcmeAccountRegistrationPayload)
                    + Environment.NewLine);
            }
            catch (IOException ex)
            {
                throw new AcmeStorageException("account_persist_failed", ex.GetType().Name);
            }
        }

        /// <summary>
        /// Create-or-load the local ACME account (pending-key crash recovery matching pyNNTPD).
        /// A complete account whose directory differs from <paramref name="directoryUrl"/> throws category <c>directory_mismatch</c>.
        /// </summary>
        /// <param name="directoryUrl">Directory URL that must match a persisted or pending account.</param>
        /// <param name="generateKeyDer">Creates a new PKCS#8 key when nothing can be resumed. An empty result throws category <c>malformed_pending</c>.</param>
        /// <param name="register">Registers the key and returns the account URI and body. Exceptions other than <see cref="AcmeAccountException"/> and cancellation become category <c>registration_failed</c>.</param>
        /// <param name="cancellationToken">Checked before load and before <paramref name="register"/>. Not passed into <paramref name="register"/>.</param>
        /// <returns>The persisted account reloaded from disk after save, or the existing account when one was already complete.</returns>
        internal AcmeAccountState EnsureRegistered(
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

        /// <summary>
        /// Returns a complete account, or <see langword="null"/> when nothing is stored or the pair is incomplete.
        /// Other <see cref="AcmeAccountException"/> values, including malformed JSON, propagate.
        /// </summary>
        /// <returns>The loaded account, or <see langword="null"/>.</returns>
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

        /// <summary>
        /// Returns a pending PKCS#8 key, or the committed key when metadata is missing.
        /// A pending directory that differs from <paramref name="directoryUrl"/> throws <see cref="AcmeAccountException"/> category <c>directory_mismatch</c>.
        /// </summary>
        /// <param name="directoryUrl">Directory URL required for a pending registration.</param>
        /// <returns>The key bytes, or <see langword="null"/> when there is nothing to resume.</returns>
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

        /// <summary>Writes <c>private_key.der.pending</c> and <c>registration.pending.json</c> version 1 before account creation.</summary>
        /// <param name="privateKeyDer">PKCS#8 DER key that registration will use.</param>
        /// <param name="directoryUrl">Directory URL stored in the pending metadata.</param>
        private void SavePendingRegistration(byte[] privateKeyDer, string directoryUrl)
        {
            AcmePaths.EnsureStateLayout(_stateDir);
            AtomicFile.WriteBytes(AcmePaths.AccountPendingKeyPath(_stateDir), privateKeyDer);
            var payload = new AcmeAccountPendingPayload
            {
                Version = 1,
                DirectoryUrl = directoryUrl,
            };
            AtomicFile.WriteText(
                AcmePaths.AccountPendingMetaPath(_stateDir),
                JsonSerializer.Serialize(payload, AcmeJsonSerializerContext.Default.AcmeAccountPendingPayload)
                + Environment.NewLine);
        }

        /// <summary>Deletes the pending key and pending metadata files. Missing files are ignored.</summary>
        private void ClearPendingRegistration()
        {
            AtomicFile.TryDelete(AcmePaths.AccountPendingKeyPath(_stateDir));
            AtomicFile.TryDelete(AcmePaths.AccountPendingMetaPath(_stateDir));
        }

        /// <summary>
        /// Reads <c>directory_url</c> from pending metadata.
        /// A missing file returns <see langword="null"/>. An empty URL or unreadable JSON throws category <c>malformed_pending</c>.
        /// </summary>
        /// <returns>The pending directory URL, or <see langword="null"/> when the pending metadata file is absent.</returns>
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
}
