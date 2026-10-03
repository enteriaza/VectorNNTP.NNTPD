using System.Text;
using System.Text.Json;

namespace VectorNNTP.Common.Acme.Protocol
{
    /// <summary>Builds ACME JWS (RFC 8555 §6.2) and external-account-binding HMAC JWS.</summary>
    internal static class JsonWebSignature
    {
        /// <summary>Encodes a signed JWS object for an ACME request.</summary>
        internal static string Encode(AcmeAccountKey key, Uri url, string? nonce, string? keyId, string payload)
        {
            string protectedHeader = Base64Url.Encode(WriteProtectedHeader(key, url, nonce, keyId));
            string encodedPayload = payload.Length == 0 ? string.Empty : Base64Url.Encode(Encoding.UTF8.GetBytes(payload));

            byte[] signingInput = Encoding.ASCII.GetBytes(protectedHeader + "." + encodedPayload);
            string signature = Base64Url.Encode(key.Sign(signingInput));

            return $"{{\"protected\":\"{protectedHeader}\",\"payload\":\"{encodedPayload}\",\"signature\":\"{signature}\"}}";
        }

        /// <summary>Encodes an HS256 JWS for external account binding.</summary>
        internal static string EncodeHmac(byte[] hmacKey, string keyId, Uri url, string payload)
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WriteString("alg", "HS256");
                writer.WriteString("kid", keyId);
                writer.WriteString("url", url.AbsoluteUri);
                writer.WriteEndObject();
            }

            string protectedHeader = Base64Url.Encode(stream.ToArray());
            string encodedPayload = Base64Url.Encode(Encoding.UTF8.GetBytes(payload));

            byte[] signingInput = Encoding.ASCII.GetBytes(protectedHeader + "." + encodedPayload);
            string signature = Base64Url.Encode(System.Security.Cryptography.HMACSHA256.HashData(hmacKey, signingInput));

            return $"{{\"protected\":\"{protectedHeader}\",\"payload\":\"{encodedPayload}\",\"signature\":\"{signature}\"}}";
        }

        private static byte[] WriteProtectedHeader(AcmeAccountKey key, Uri url, string? nonce, string? keyId)
        {
            using var stream = new MemoryStream();
            using var writer = new Utf8JsonWriter(stream);

            writer.WriteStartObject();
            writer.WriteString("alg", key.SignatureAlgorithm);

            if (keyId is null)
            {
                writer.WritePropertyName("jwk");
                writer.WriteRawValue(key.Jwk, skipInputValidation: true);
            }
            else
            {
                writer.WriteString("kid", keyId);
            }

            if (nonce is not null)
            {
                writer.WriteString("nonce", nonce);
            }

            writer.WriteString("url", url.AbsoluteUri);
            writer.WriteEndObject();
            writer.Flush();

            return stream.ToArray();
        }
    }
}
