namespace VectorNNTP.Common.Acme.Protocol
{
    /// <summary>
    /// External account binding credentials (RFC 8555 §7.3.4). Optional; VectorNNTP registration passes null
    /// when the directory does not require EAB.
    /// </summary>
    internal sealed class ExternalAccountBinding
    {
        private readonly byte[] _hmacKey;

        /// <summary>Initializes a new instance with a base64url-encoded HMAC key.</summary>
        public ExternalAccountBinding(string keyId, string hmacKey)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(keyId);
            ArgumentException.ThrowIfNullOrWhiteSpace(hmacKey);

            KeyId = keyId;
            _hmacKey = DecodeKey(hmacKey);
        }

        /// <summary>Initializes a new instance with a raw HMAC key.</summary>
        public ExternalAccountBinding(string keyId, byte[] hmacKey)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(keyId);
            ArgumentNullException.ThrowIfNull(hmacKey);

            KeyId = keyId;
            _hmacKey = (byte[])hmacKey.Clone();
        }

        /// <summary>Gets the key identifier issued by the certificate authority.</summary>
        public string KeyId { get; }

        /// <summary>Returns the HMAC key bytes for JWS signing.</summary>
        internal byte[] GetHmacKey() => _hmacKey;

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
