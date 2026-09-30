using System.Buffers;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace VectorNNTP.Common.Messaging.Cache;

/// <summary>
/// Canonical compact System.Text.Json contract for StorageServer cache-fleet advertisement v1 bodies.
/// </summary>
/// <remarks>
/// Property names are <c>version</c>, <c>serverId</c>, <c>fqdn</c>, <c>totalBytes</c>,
/// <c>usedBytes</c>, <c>availableBytes</c>, and <c>timestamp</c> (UTC ISO-8601 round-trip).
/// Optional <c>vatpPort</c> (<c>1</c>–<c>65535</c>) is written when present. Unknown
/// properties are ignored. A missing <c>vatpPort</c> parses as no placement port.
/// AMQP <c>Expiration</c>, <c>AppId</c>, and <c>MessageId</c> are never JSON fields.
/// Serialization writes compact UTF-8 with no indentation.
/// </remarks>
public static class StorageServerAdvertisementWireProtocol
{
    /// <summary>Current application protocol version.</summary>
    public const int CurrentVersion = 1;

    /// <summary>AMQP <c>ContentType</c> for advertisement bodies.</summary>
    public const string JsonContentType = "application/json";

    /// <summary>Serializes a version-1 advertisement into compact UTF-8 JSON.</summary>
    /// <param name="advertisement">Concrete valid v1 advertisement.</param>
    /// <returns>UTF-8 bytes containing only the seven canonical properties.</returns>
    public static byte[] SerializeV1(StorageServerAdvertisement advertisement)
    {
        ArgumentNullException.ThrowIfNull(advertisement);
        if (advertisement.Version != CurrentVersion)
        {
            throw new InvalidOperationException(
                $"Unsupported StorageServer advertisement version '{advertisement.Version}'.");
        }

        if (advertisement.ServerId <= 0)
        {
            throw new InvalidOperationException("StorageServer advertisement requires a positive serverId.");
        }

        if (string.IsNullOrWhiteSpace(advertisement.Fqdn))
        {
            throw new InvalidOperationException("StorageServer advertisement requires a non-empty fqdn.");
        }

        if (advertisement.TotalBytes < 0
            || advertisement.UsedBytes < 0
            || advertisement.AvailableBytes < 0)
        {
            throw new InvalidOperationException("StorageServer advertisement byte counts must be non-negative.");
        }

        if (advertisement.VatpPort is < 1 or > 65535)
        {
            throw new InvalidOperationException("StorageServer advertisement vatpPort must be in the range 1–65535.");
        }

        var writer = new ArrayBufferWriter<byte>();
        using var jsonWriter = new Utf8JsonWriter(writer, new JsonWriterOptions
        {
            Indented = false,
            SkipValidation = false,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });

        jsonWriter.WriteStartObject();
        jsonWriter.WriteNumber("version", advertisement.Version);
        jsonWriter.WriteNumber("serverId", advertisement.ServerId);
        jsonWriter.WriteString("fqdn", advertisement.Fqdn);
        jsonWriter.WriteNumber("totalBytes", advertisement.TotalBytes);
        jsonWriter.WriteNumber("usedBytes", advertisement.UsedBytes);
        jsonWriter.WriteNumber("availableBytes", advertisement.AvailableBytes);
        jsonWriter.WriteString("timestamp", advertisement.Timestamp.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        if (advertisement.VatpPort is int vatpPort)
        {
            jsonWriter.WriteNumber("vatpPort", vatpPort);
        }

        jsonWriter.WriteEndObject();
        jsonWriter.Flush();
        return writer.WrittenSpan.ToArray();
    }

    /// <summary>Attempts to parse and validate a version-1 advertisement payload.</summary>
    /// <param name="payload">UTF-8 JSON body.</param>
    /// <param name="advertisement">Parsed advertisement when validation succeeds.</param>
    /// <param name="reason">Rejection reason when parsing fails.</param>
    /// <returns><see langword="true"/> when the payload is a valid v1 advertisement.</returns>
    public static bool TryParseV1(
        ReadOnlySpan<byte> payload,
        out StorageServerAdvertisement? advertisement,
        out string reason)
    {
        advertisement = null;
        reason = "Advertisement payload is empty.";
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
            reason = "Advertisement payload was not valid JSON.";
            return false;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                reason = "Advertisement payload root must be a JSON object.";
                return false;
            }

            var root = document.RootElement;
            if (!TryReadInt32(root, "version", out var version, out reason)
                || version != CurrentVersion)
            {
                reason = version == 0 && reason.Length > 0
                    ? reason
                    : $"Unsupported StorageServer advertisement version '{version}'.";
                return false;
            }

            if (!TryReadInt32(root, "serverId", out var serverId, out reason) || serverId <= 0)
            {
                reason = serverId <= 0 && string.IsNullOrEmpty(reason)
                    ? "Advertisement serverId must be a positive integer."
                    : reason;
                return false;
            }

            if (!TryReadString(root, "fqdn", out var fqdn, out reason) || string.IsNullOrWhiteSpace(fqdn))
            {
                reason = "Advertisement fqdn is required.";
                return false;
            }

            if (!TryReadInt64(root, "totalBytes", out var totalBytes, out reason) || totalBytes < 0
                || !TryReadInt64(root, "usedBytes", out var usedBytes, out reason) || usedBytes < 0
                || !TryReadInt64(root, "availableBytes", out var availableBytes, out reason) || availableBytes < 0)
            {
                reason = "Advertisement byte counts must be non-negative integers.";
                return false;
            }

            if (!TryReadTimestamp(root, "timestamp", out var timestamp, out reason))
            {
                return false;
            }

            int? vatpPort = null;
            if (root.TryGetProperty("vatpPort", out var portProperty))
            {
                if (portProperty.ValueKind != JsonValueKind.Number || !portProperty.TryGetInt32(out var parsedPort))
                {
                    reason = "Advertisement vatpPort must be an integer.";
                    return false;
                }

                if (parsedPort is < 1 or > 65535)
                {
                    reason = "Advertisement vatpPort must be in the range 1–65535.";
                    return false;
                }

                vatpPort = parsedPort;
            }

            advertisement = new StorageServerAdvertisement(
                version,
                serverId,
                fqdn.Trim(),
                totalBytes,
                usedBytes,
                availableBytes,
                timestamp,
                vatpPort);
            reason = string.Empty;
            return true;
        }
    }

    private static bool TryReadInt32(JsonElement root, string name, out int value, out string reason)
    {
        value = 0;
        reason = string.Empty;
        if (!root.TryGetProperty(name, out var property))
        {
            reason = $"Advertisement is missing '{name}'.";
            return false;
        }

        if (property.ValueKind != JsonValueKind.Number || !property.TryGetInt32(out value))
        {
            reason = $"Advertisement '{name}' must be an integer.";
            return false;
        }

        return true;
    }

    private static bool TryReadInt64(JsonElement root, string name, out long value, out string reason)
    {
        value = 0;
        reason = string.Empty;
        if (!root.TryGetProperty(name, out var property))
        {
            reason = $"Advertisement is missing '{name}'.";
            return false;
        }

        if (property.ValueKind != JsonValueKind.Number || !property.TryGetInt64(out value))
        {
            reason = $"Advertisement '{name}' must be an integer.";
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
            reason = $"Advertisement is missing '{name}'.";
            return false;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            reason = $"Advertisement '{name}' must be a string.";
            return false;
        }

        value = property.GetString() ?? string.Empty;
        return true;
    }

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
            reason = $"Advertisement is missing '{name}'.";
            return false;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            reason = $"Advertisement '{name}' must be an ISO-8601 string.";
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
            reason = $"Advertisement '{name}' was not a valid ISO-8601 timestamp.";
            return false;
        }

        value = value.ToUniversalTime();
        return true;
    }
}
