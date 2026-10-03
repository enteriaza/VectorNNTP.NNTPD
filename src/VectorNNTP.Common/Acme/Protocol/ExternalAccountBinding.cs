namespace VectorNNTP.Common.Acme.Protocol
{
    /// <summary>
    /// External account binding credentials (RFC 8555 §7.3.4). Optional; VectorNNTP registration passes null
    /// when the directory does not require EAB.
    /// </summary>
    internal sealed class ExternalAccountBinding
    {
        /// <summary>HMAC key bytes. Returned by <see cref="GetHmacKey"/> without copying.</summary>
        private readonly byte[] _hmacKey;

        /// <summary>Initializes a new instance with a base64url-encoded HMAC key.</summary>
        /// <param name="keyId">CA key identifier stored as <see cref="KeyId"/>.</param>
        /// <param name="hmacKey">Base64url HMAC key. Invalid base64url throws <see cref="ArgumentException"/>.</param>
        private ExternalAccountBinding(string keyId, string hmacKey)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(keyId);
            ArgumentException.ThrowIfNullOrWhiteSpace(hmacKey);

            KeyId = keyId;
            _hmacKey = DecodeKey(hmacKey);
        }

        /// <summary>Initializes a new instance with a raw HMAC key.</summary>
        /// <param name="keyId">CA key identifier stored as <see cref="KeyId"/>.</param>
        /// <param name="hmacKey">Raw HMAC key. A copy is stored.</param>
        private ExternalAccountBinding(string keyId, byte[] hmacKey)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(keyId);
            ArgumentNullException.ThrowIfNull(hmacKey);

            KeyId = keyId;
            _hmacKey = (byte[])hmacKey.Clone();
        }

        /// <summary>Gets the key identifier issued by the certificate authority.</summary>
        internal string KeyId { get; }

        /// <summary>Returns the HMAC key bytes for JWS signing.</summary>
        /// <returns>The stored key. The caller must not mutate it; it is not a copy.</returns>
        internal byte[] GetHmacKey() => _hmacKey;

        /// <summary>Decodes a base64url HMAC key.</summary>
        /// <param name="hmacKey">Base64url text.</param>
        /// <returns>The raw key bytes.</returns>
        /// <exception cref="ArgumentException">Thrown when <paramref name="hmacKey"/> is not valid base64url.</exception>
        private static byte[] DecodeKey(string hmacKey)
        {
            try
            {
                return Base64Url.Decode(hmacKey);
            }
            catch (FormatException ex)
            {
                throw new ArgumentException("The HMAC key is not valid base64url.", nameof(hmacKey), ex);
            }
        }
    }
}
