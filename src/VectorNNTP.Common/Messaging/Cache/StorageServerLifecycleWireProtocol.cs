using System.Buffers;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace VectorNNTP.Common.Messaging.Cache
{
    /// <summary>
    /// Canonical compact JSON contract for StorageServer lifecycle announcement v1 bodies.
    /// </summary>
    /// <remarks>
    /// Property names are <c>version</c>, <c>serverId</c>, <c>fqdn</c>, <c>state</c>
    /// (<c>Draining</c>), <c>timestamp</c> (UTC ISO-8601 round-trip), and <c>vatpPort</c>.
    /// Version 1 accepts only <c>Draining</c>. Any other <c>state</c> string, including
    /// <c>Ready</c>, is invalid and is not treated as an advertisement. An object without
    /// <c>state</c> is not a lifecycle announcement. Unknown properties are ignored.
    /// AMQP expiration and persistence are not JSON fields.
    /// </remarks>
    internal static class StorageServerLifecycleWireProtocol
    {
        /// <summary>Current application protocol version.</summary>
        public const int CurrentVersion = 1;

        /// <summary>Reason returned when the payload is an object without <c>state</c>.</summary>
        public const string NotLifecycleReason = "Payload is not a StorageServer lifecycle announcement.";

        /// <summary>Serializes a version-1 lifecycle announcement into compact UTF-8 JSON.</summary>
        /// <param name="announcement">Concrete valid v1 announcement.</param>
        /// <returns>UTF-8 bytes containing the canonical properties.</returns>
        internal static byte[] SerializeV1(StorageServerLifecycleAnnouncement announcement)
        {
            ArgumentNullException.ThrowIfNull(announcement);
            if (announcement.Version != CurrentVersion)
            {
                throw new InvalidOperationException(
                    $"Unsupported StorageServer lifecycle announcement version '{announcement.Version}'.");
            }

            if (announcement.ServerId <= 0)
            {
                throw new InvalidOperationException("StorageServer lifecycle announcement requires a positive serverId.");
            }

            if (string.IsNullOrWhiteSpace(announcement.Fqdn))
            {
                throw new InvalidOperationException("StorageServer lifecycle announcement requires a non-empty fqdn.");
            }

            if (announcement.State != StorageServerLifecycleState.Draining)
            {
                throw new InvalidOperationException(
                    $"Unsupported StorageServer lifecycle state '{announcement.State}'.");
            }

            if (announcement.VatpPort is < 1 or > 65535)
            {
                throw new InvalidOperationException(
                    "StorageServer lifecycle announcement vatpPort must be in the range 1–65535.");
            }

            var writer = new ArrayBufferWriter<byte>();
            using var jsonWriter = new Utf8JsonWriter(writer, new JsonWriterOptions
            {
                Indented = false,
                SkipValidation = false,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });

            jsonWriter.WriteStartObject();
            jsonWriter.WriteNumber("version", announcement.Version);
            jsonWriter.WriteNumber("serverId", announcement.ServerId);
            jsonWriter.WriteString("fqdn", announcement.Fqdn);
            jsonWriter.WriteString("state", announcement.State.ToString());
            jsonWriter.WriteString(
                "timestamp",
                announcement.Timestamp.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
            jsonWriter.WriteNumber("vatpPort", announcement.VatpPort);
            jsonWriter.WriteEndObject();
            jsonWriter.Flush();
            return writer.WrittenSpan.ToArray();
        }

        /// <summary>Attempts to parse and validate a version-1 lifecycle announcement.</summary>
        /// <param name="payload">UTF-8 JSON body.</param>
        /// <param name="announcement">Parsed announcement when validation succeeds.</param>
        /// <param name="reason">Rejection reason when parsing fails.</param>
        /// <param name="recognized">
        /// <see langword="true"/> when the payload contains <c>state</c> and must not be parsed as a
        /// capacity advertisement.
        /// </param>
        /// <returns><see langword="true"/> when the payload is a valid v1 lifecycle announcement.</returns>
        internal static bool TryParseV1(
            ReadOnlySpan<byte> payload,
            out StorageServerLifecycleAnnouncement? announcement,
            out string reason,
            out bool recognized)
        {
            announcement = null;
            recognized = false;
            reason = "Lifecycle announcement payload is empty.";
            if (payload.IsEmpty)
            {
                return false;
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(payload.ToArray());
            }
            catch (JsonException)
            {
                reason = "Lifecycle announcement payload was not valid JSON.";
                return false;
            }

            using (document)
            {
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    reason = "Lifecycle announcement payload root must be a JSON object.";
                    return false;
                }

                var root = document.RootElement;
                if (!root.TryGetProperty("state", out _))
                {
                    reason = NotLifecycleReason;
                    return false;
                }

                recognized = true;
                if (!TryReadInt32(root, "version", out var version, out reason) || version != CurrentVersion)
                {
                    reason = version == 0 && reason.Length > 0
                        ? reason
                        : $"Unsupported StorageServer lifecycle announcement version '{version}'.";
                    return false;
                }

                if (!TryReadInt32(root, "serverId", out var serverId, out reason) || serverId <= 0)
                {
                    reason = serverId <= 0 && string.IsNullOrEmpty(reason)
                        ? "Lifecycle announcement serverId must be a positive integer."
                        : reason;
                    return false;
                }

                if (!TryReadString(root, "fqdn", out var fqdn, out reason) || string.IsNullOrWhiteSpace(fqdn))
                {
                    reason = "Lifecycle announcement fqdn is required.";
                    return false;
                }

                if (!TryReadString(root, "state", out var stateText, out reason)
                    || !TryParseState(stateText, out var state))
                {
                    reason = "Lifecycle announcement state must be Draining.";
                    return false;
                }

                if (!TryReadTimestamp(root, "timestamp", out var timestamp, out reason))
                {
                    return false;
                }

                if (!TryReadInt32(root, "vatpPort", out var vatpPort, out reason) || vatpPort is < 1 or > 65535)
                {
                    reason = vatpPort is < 1 or > 65535 && string.IsNullOrEmpty(reason)
                        ? "Lifecycle announcement vatpPort must be in the range 1–65535."
                        : reason;
                    return false;
                }

                announcement = new StorageServerLifecycleAnnouncement(
                    version,
                    serverId,
                    fqdn.Trim(),
                    state,
                    timestamp,
                    vatpPort);
                reason = string.Empty;
                return true;
            }
        }

        /// <summary>Accepts only the ordinal text <c>Draining</c>.</summary>
        /// <param name="text">JSON <c>state</c> value.</param>
        /// <param name="state"><see cref="StorageServerLifecycleState.Draining"/> on success; otherwise the default value.</param>
        /// <returns><see langword="false"/> for every other string, including <c>Ready</c>.</returns>
        private static bool TryParseState(string text, out StorageServerLifecycleState state)
        {
            if (string.Equals(text, nameof(StorageServerLifecycleState.Draining), StringComparison.Ordinal))
            {
                state = StorageServerLifecycleState.Draining;
                return true;
            }

            state = default;
            return false;
        }

        /// <summary>Reads <paramref name="name"/> as a JSON number that fits in <see cref="int"/>.</summary>
        /// <param name="root">Lifecycle announcement object.</param>
        /// <param name="name">Required property name.</param>
        /// <param name="value">Parsed integer on success; 0 on failure.</param>
        /// <param name="reason">Empty on success; otherwise a lifecycle missing-property or type error.</param>
        /// <returns><see langword="false"/> when the property is absent or not an <see cref="int"/> JSON number.</returns>
        private static bool TryReadInt32(JsonElement root, string name, out int value, out string reason)
        {
            value = 0;
            reason = string.Empty;
            if (!root.TryGetProperty(name, out var property))
            {
                reason = $"Lifecycle announcement is missing '{name}'.";
                return false;
            }

            if (property.ValueKind != JsonValueKind.Number || !property.TryGetInt32(out value))
            {
                reason = $"Lifecycle announcement '{name}' must be an integer.";
                return false;
            }

            return true;
        }

        /// <summary>Reads <paramref name="name"/> as a JSON string. A JSON null string becomes empty.</summary>
        /// <param name="root">Lifecycle announcement object.</param>
        /// <param name="name">Required property name.</param>
        /// <param name="value">Property text on success; empty on failure.</param>
        /// <param name="reason">Empty on success; otherwise a lifecycle missing-property or type error.</param>
        /// <returns><see langword="false"/> when the property is absent or not a string.</returns>
        private static bool TryReadString(JsonElement root, string name, out string value, out string reason)
        {
            value = string.Empty;
            reason = string.Empty;
            if (!root.TryGetProperty(name, out var property))
            {
                reason = $"Lifecycle announcement is missing '{name}'.";
                return false;
            }

            if (property.ValueKind != JsonValueKind.String)
            {
                reason = $"Lifecycle announcement '{name}' must be a string.";
                return false;
            }

            value = property.GetString() ?? string.Empty;
            return true;
        }

        /// <summary>Reads <paramref name="name"/> as a round-trip ISO-8601 timestamp and converts it to UTC.</summary>
        /// <param name="root">Lifecycle announcement object.</param>
        /// <param name="name">Required property name, normally <c>timestamp</c>.</param>
        /// <param name="value">UTC timestamp on success; otherwise the default.</param>
        /// <param name="reason">Empty on success; otherwise a missing, non-string, or unparseable timestamp error.</param>
        /// <returns><see langword="true"/> when invariant round-trip parsing succeeds.</returns>
        private static bool TryReadTimestamp(
            JsonElement root,
            string name,
            out DateTimeOffset value,
            out string reason)
        {
            value = default;
            reason = string.Empty;
            if (!root.TryGetProperty(name, out var property))
            {
                reason = $"Lifecycle announcement is missing '{name}'.";
                return false;
            }

            if (property.ValueKind != JsonValueKind.String)
            {
                reason = $"Lifecycle announcement '{name}' must be an ISO-8601 string.";
                return false;
            }

            var text = property.GetString();
            if (string.IsNullOrWhiteSpace(text)
                || !DateTimeOffset.TryParse(
                    text,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out value))
            {
                reason = $"Lifecycle announcement '{name}' was not a valid ISO-8601 timestamp.";
                return false;
            }

            value = value.ToUniversalTime();
            return true;
        }
    }
}
