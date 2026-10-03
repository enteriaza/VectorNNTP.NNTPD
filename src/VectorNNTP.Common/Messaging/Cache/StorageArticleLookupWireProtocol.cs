using System.Buffers;
using System.Text.Encodings.Web;
using System.Text.Json;
using VectorNNTP.Common.Articles;

namespace VectorNNTP.Common.Messaging.Cache
{
    /// <summary>
    /// Canonical compact System.Text.Json contracts for StorageServer article-lookup v1 bodies.
    /// </summary>
    /// <remarks>
    /// Request properties: <c>version</c>, <c>requestId</c>, <c>articleId</c>.
    /// Response properties: <c>version</c>, <c>requestId</c>, <c>serverId</c>, <c>fqdn</c>,
    /// <c>articleId</c>, <c>vatpPort</c>. A response that includes <c>uri</c> is rejected.
    /// AMQP <c>CorrelationId</c>, <c>ReplyTo</c>, and
    /// <c>Expiration</c> are never JSON fields.
    /// </remarks>
    internal static class StorageArticleLookupWireProtocol
    {
        /// <summary>Current application protocol version.</summary>
        public const int CurrentVersion = 1;

        /// <summary>AMQP <c>ContentType</c> for lookup bodies.</summary>
        public const string JsonContentType = "application/json";

        /// <summary>Serializes a version-1 lookup request.</summary>
        internal static byte[] SerializeRequestV1(StorageArticleLookupRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);
            if (request.Version != CurrentVersion)
            {
                throw new InvalidOperationException($"Unsupported storage lookup request version '{request.Version}'.");
            }

            if (request.RequestId == Guid.Empty)
            {
                throw new InvalidOperationException("Storage lookup request requires a non-empty requestId.");
            }

            var writer = new ArrayBufferWriter<byte>();
            using var jsonWriter = CreateWriter(writer);
            jsonWriter.WriteStartObject();
            jsonWriter.WriteNumber("version", request.Version);
            jsonWriter.WriteString("requestId", request.RequestId);
            jsonWriter.WriteString("articleId", request.ArticleId.ToLowerHexString());
            jsonWriter.WriteEndObject();
            jsonWriter.Flush();
            return writer.WrittenSpan.ToArray();
        }

        /// <summary>Attempts to parse a version-1 lookup request.</summary>
        internal static bool TryParseRequestV1(
            ReadOnlySpan<byte> payload,
            out StorageArticleLookupRequest? request,
            out string reason)
        {
            request = null;
            reason = "Lookup request payload is empty.";
            if (payload.IsEmpty)
            {
                return false;
            }

            if (!TryParseObject(payload, out var document, out reason))
            {
                return false;
            }

            using (document)
            {
                var root = document.RootElement;
                if (!TryReadVersion(root, out var version, out reason) || version != CurrentVersion)
                {
                    reason = $"Unsupported storage lookup request version '{version}'.";
                    return false;
                }

                if (!TryReadGuid(root, "requestId", out var requestId, out reason) || requestId == Guid.Empty)
                {
                    reason = "Lookup request requestId must be a non-empty UUID.";
                    return false;
                }

                if (!TryReadArticleId(root, out var articleId, out reason))
                {
                    return false;
                }

                request = new StorageArticleLookupRequest(version, requestId, articleId);
                reason = string.Empty;
                return true;
            }
        }

        /// <summary>Serializes a version-1 positive lookup response.</summary>
        internal static byte[] SerializeResponseV1(StorageArticleLookupResponse response)
        {
            ArgumentNullException.ThrowIfNull(response);
            if (response.Version != CurrentVersion)
            {
                throw new InvalidOperationException($"Unsupported storage lookup response version '{response.Version}'.");
            }

            if (response.RequestId == Guid.Empty)
            {
                throw new InvalidOperationException("Storage lookup response requires a non-empty requestId.");
            }

            if (response.ServerId <= 0)
            {
                throw new InvalidOperationException("Storage lookup response requires a positive serverId.");
            }

            if (string.IsNullOrWhiteSpace(response.Fqdn))
            {
                throw new InvalidOperationException("Storage lookup response requires a non-empty fqdn.");
            }

            if (!VatpEndpointFields.IsCanonicalFqdn(response.Fqdn))
            {
                throw new InvalidOperationException("Storage lookup response requires a canonical fqdn.");
            }

            if (!VatpEndpointFields.IsCanonicalPort(response.VatpPort))
            {
                throw new InvalidOperationException("Storage lookup response requires a vatpPort in the range 1–65535.");
            }

            var writer = new ArrayBufferWriter<byte>();
            using var jsonWriter = CreateWriter(writer);
            jsonWriter.WriteStartObject();
            jsonWriter.WriteNumber("version", response.Version);
            jsonWriter.WriteString("requestId", response.RequestId);
            jsonWriter.WriteNumber("serverId", response.ServerId);
            jsonWriter.WriteString("fqdn", response.Fqdn);
            jsonWriter.WriteString("articleId", response.ArticleId.ToLowerHexString());
            jsonWriter.WriteNumber("vatpPort", response.VatpPort);
            jsonWriter.WriteEndObject();
            jsonWriter.Flush();
            return writer.WrittenSpan.ToArray();
        }

        /// <summary>Attempts to parse a version-1 positive lookup response.</summary>
        internal static bool TryParseResponseV1(
            ReadOnlySpan<byte> payload,
            out StorageArticleLookupResponse? response,
            out string reason)
        {
            response = null;
            reason = "Lookup response payload is empty.";
            if (payload.IsEmpty)
            {
                return false;
            }

            if (!TryParseObject(payload, out var document, out reason))
            {
                return false;
            }

            using (document)
            {
                var root = document.RootElement;
                if (!TryReadVersion(root, out var version, out reason) || version != CurrentVersion)
                {
                    reason = $"Unsupported storage lookup response version '{version}'.";
                    return false;
                }

                if (!TryReadGuid(root, "requestId", out var requestId, out reason) || requestId == Guid.Empty)
                {
                    reason = "Lookup response requestId must be a non-empty UUID.";
                    return false;
                }

                if (!TryReadInt32(root, "serverId", out var serverId, out reason) || serverId <= 0)
                {
                    reason = "Lookup response serverId must be a positive integer.";
                    return false;
                }

                if (!TryReadString(root, "fqdn", out var fqdn, out reason) || string.IsNullOrWhiteSpace(fqdn))
                {
                    reason = "Lookup response fqdn is required.";
                    return false;
                }

                if (!TryReadArticleId(root, out var articleId, out reason))
                {
                    return false;
                }

                if (root.TryGetProperty("uri", out _))
                {
                    reason = "Lookup response must not include 'uri'.";
                    return false;
                }

                if (!TryReadInt32(root, "vatpPort", out var vatpPort, out reason)
                    || !VatpEndpointFields.IsCanonicalPort(vatpPort))
                {
                    reason = "Lookup response vatpPort must be an integer in the range 1–65535.";
                    return false;
                }

                if (!VatpEndpointFields.IsCanonicalFqdn(fqdn))
                {
                    reason = "Lookup response fqdn must be a lowercase dotted DNS name.";
                    return false;
                }

                response = new StorageArticleLookupResponse(
                    version,
                    requestId,
                    serverId,
                    fqdn.Trim(),
                    articleId,
                    vatpPort);
                reason = string.Empty;
                return true;
            }
        }

        private static Utf8JsonWriter CreateWriter(ArrayBufferWriter<byte> writer) =>
            new(writer, new JsonWriterOptions
            {
                Indented = false,
                SkipValidation = false,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });

        private static bool TryParseObject(ReadOnlySpan<byte> payload, out JsonDocument document, out string reason)
        {
            reason = string.Empty;
            try
            {
                document = JsonDocument.Parse(payload.ToArray());
            }
            catch (JsonException)
            {
                document = null!;
                reason = "Payload was not valid JSON.";
                return false;
            }

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                document.Dispose();
                document = null!;
                reason = "Payload root must be a JSON object.";
                return false;
            }

            return true;
        }

        private static bool TryReadVersion(JsonElement root, out int version, out string reason) =>
            TryReadInt32(root, "version", out version, out reason);

        private static bool TryReadArticleId(JsonElement root, out ArticleId articleId, out string reason)
        {
            articleId = default;
            if (!TryReadString(root, "articleId", out var hex, out reason))
            {
                return false;
            }

            if (!ArticleId.TryParseLowerHex(hex, out articleId))
            {
                reason = "Property 'articleId' is not a 64-character lowercase hexadecimal ArticleId.";
                return false;
            }

            return true;
        }

        private static bool TryReadGuid(JsonElement root, string name, out Guid value, out string reason)
        {
            value = Guid.Empty;
            if (!TryReadString(root, name, out var text, out reason))
            {
                return false;
            }

            if (!Guid.TryParse(text, out value))
            {
                reason = $"Property '{name}' is not a UUID.";
                return false;
            }

            return true;
        }

        private static bool TryReadInt32(JsonElement root, string name, out int value, out string reason)
        {
            value = 0;
            reason = string.Empty;
            if (!root.TryGetProperty(name, out var property))
            {
                reason = $"Payload is missing '{name}'.";
                return false;
            }

            if (property.ValueKind != JsonValueKind.Number || !property.TryGetInt32(out value))
            {
                reason = $"Property '{name}' must be an integer.";
                return false;
            }

            return true;
        }

        private static bool TryReadString(JsonElement root, string name, out string value, out string reason)
        {
            value = string.Empty;
            reason = string.Empty;
            if (!root.TryGetProperty(name, out var property))
            {
                reason = $"Payload is missing '{name}'.";
                return false;
            }

            if (property.ValueKind != JsonValueKind.String)
            {
                reason = $"Property '{name}' must be a string.";
                return false;
            }

            value = property.GetString() ?? string.Empty;
            return true;
        }
    }
}
