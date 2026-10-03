using System.Text.Json;
using VectorNNTP.Common.Messaging.RabbitMq;

namespace VectorNNTP.BackFiller.ArticleWork;

/// <summary>
/// Parses one consumed delivery into a validated Article Work request.
/// </summary>
/// <remarks>
/// Property names are case-sensitive. Unknown JSON fields are ignored. Identities are
/// never synthesized. AMQP <c>RequestId</c> is not a JSON field: when present, it must
/// match JSON <c>requestId</c>; when absent, the protocol still accepts the delivery
/// (required AMQP fields are only <c>CorrelationId</c> and <c>ReplyTo</c>).
/// </remarks>
internal static class ArticleWorkRequestParser
{
    /// <summary>Supported application protocol versions.</summary>
    private const int CurrentVersion = 1;

    /// <summary>Required AMQP content type when the property is present.</summary>
    internal const string JsonContentType = "application/json";

    /// <summary>
    /// Parses <paramref name="delivery"/> against the consuming backbone context.
    /// </summary>
    /// <param name="delivery">Consumed AMQP delivery.</param>
    /// <param name="consumingBackbone">Queue/session backbone context.</param>
    /// <param name="maxPayloadBytes">Maximum accepted application body size. Must be at least 1.</param>
    /// <returns>A valid request or an <see cref="ArticleWorkOutcome.InvalidRequest"/> failure. Does not throw for a rejected payload.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="consumingBackbone"/> is null or whitespace.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="maxPayloadBytes"/> is less than 1.</exception>
    internal static ArticleWorkParseResult Parse(
        in RabbitMqManualAckDelivery delivery,
        string consumingBackbone,
        int maxPayloadBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(consumingBackbone);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPayloadBytes, 1);

        if (delivery.Body.Length > maxPayloadBytes)
        {
            return ArticleWorkParseResult.Invalid(
                "RabbitMQ article-work payload exceeds WorkRequestMaxPayloadBytes.",
                default);
        }

        if (!string.IsNullOrEmpty(delivery.ContentType)
            && !string.Equals(delivery.ContentType, JsonContentType, StringComparison.Ordinal))
        {
            return ArticleWorkParseResult.Invalid(
                "RabbitMQ delivery ContentType must be application/json when present.",
                default);
        }

        if (delivery.Body.IsEmpty)
        {
            return ArticleWorkParseResult.Invalid("RabbitMQ article-work payload was empty.", default);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(delivery.Body);
        }
        catch (JsonException)
        {
            return ArticleWorkParseResult.Invalid("RabbitMQ article-work payload was not valid JSON.", default);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return ArticleWorkParseResult.Invalid("RabbitMQ article-work payload must be a JSON object.", default);
            }

            _ = TryReadGuid(root, "requestId", out var requestId);
            _ = TryReadString(root, "messageId", out var rawMessageId);
            _ = TryReadString(root, "backbone", out var rawBackbone);

            var messageId = !string.IsNullOrWhiteSpace(rawMessageId) && ArticleWorkMessageId.IsWellFormed(rawMessageId)
                ? rawMessageId
                : null;
            var backbone = !string.IsNullOrWhiteSpace(rawBackbone) ? rawBackbone : null;
            var identities = new ArticleWorkParsedIdentities(requestId, messageId, backbone);

            if (!TryReadInt32(root, "version", out var version))
            {
                return ArticleWorkParseResult.Invalid(
                    "RabbitMQ article-work payload is missing required integer property 'version'.",
                    identities);
            }

            if (version != CurrentVersion)
            {
                return ArticleWorkParseResult.Invalid(
                    $"RabbitMQ article-work payload uses unsupported version '{version}'.",
                    identities);
            }

            if (requestId is null)
            {
                return ArticleWorkParseResult.Invalid(
                    "RabbitMQ article-work payload contains missing or invalid 'requestId'.",
                    identities);
            }

            if (string.IsNullOrWhiteSpace(rawMessageId))
            {
                return ArticleWorkParseResult.Invalid(
                    "RabbitMQ article-work payload contains missing or invalid 'messageId'.",
                    identities with { MessageId = null });
            }

            if (messageId is null)
            {
                return ArticleWorkParseResult.Invalid(
                    "RabbitMQ article-work payload 'messageId' is not a canonical NNTP Message-ID.",
                    identities with { MessageId = null });
            }

            if (backbone is null)
            {
                return ArticleWorkParseResult.Invalid(
                    "RabbitMQ article-work payload contains missing or invalid 'backbone'.",
                    identities);
            }

            if (!string.Equals(backbone, consumingBackbone, StringComparison.OrdinalIgnoreCase))
            {
                return ArticleWorkParseResult.Invalid(
                    "RabbitMQ article-work payload backbone does not match the consuming queue backbone context.",
                    identities);
            }

            if (string.IsNullOrWhiteSpace(delivery.CorrelationId))
            {
                return ArticleWorkParseResult.Invalid(
                    "RabbitMQ delivery is missing required AMQP CorrelationId property.",
                    identities);
            }

            if (string.IsNullOrWhiteSpace(delivery.ReplyTo))
            {
                return ArticleWorkParseResult.Invalid(
                    "RabbitMQ delivery is missing required AMQP ReplyTo property.",
                    identities);
            }

            if (string.IsNullOrWhiteSpace(delivery.RequestIdHeader))
                return ArticleWorkParseResult.Valid(new ArticleWorkRequest(
                    CurrentVersion,
                    requestId.Value,
                    messageId,
                    backbone));
            if (!Guid.TryParse(delivery.RequestIdHeader, out var headerId)
                || headerId == Guid.Empty
                || headerId != requestId.Value)
            {
                return ArticleWorkParseResult.Invalid(
                    "RabbitMQ AMQP RequestId header does not match JSON requestId.",
                    identities);
            }

            return ArticleWorkParseResult.Valid(new ArticleWorkRequest(
                CurrentVersion,
                requestId.Value,
                messageId,
                backbone));
        }
    }

    /// <summary>Reads a case-sensitive JSON number property as a 32-bit integer.</summary>
    /// <param name="root">Payload object.</param>
    /// <param name="name">Exact property name.</param>
    /// <param name="value">Parsed value when the result is <see langword="true"/>; otherwise zero.</param>
    /// <returns>
    /// <see langword="true"/> when <paramref name="name"/> exists, is a JSON number, and fits <see cref="int"/>.
    /// </returns>
    private static bool TryReadInt32(JsonElement root, string name, out int value)
    {
        value = 0;
        return root.TryGetProperty(name, out var property)
               && property.ValueKind == JsonValueKind.Number
               && property.TryGetInt32(out value);
    }

    /// <summary>Reads a case-sensitive JSON string property without treating empty text as missing.</summary>
    /// <param name="root">Payload object.</param>
    /// <param name="name">Exact property name.</param>
    /// <param name="value">The string when the result is <see langword="true"/>; otherwise null.</param>
    /// <returns>
    /// <see langword="true"/> when <paramref name="name"/> exists, is a JSON string, and is not JSON null.
    /// </returns>
    private static bool TryReadString(JsonElement root, string name, out string? value)
    {
        value = null;
        if (!root.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString();
        return value is not null;
    }

    /// <summary>Reads a case-sensitive JSON string property as a non-empty GUID.</summary>
    /// <param name="root">Payload object.</param>
    /// <param name="name">Exact property name.</param>
    /// <param name="value">The GUID when the result is <see langword="true"/>; otherwise null.</param>
    /// <returns>
    /// <see langword="false"/> when the property is missing, not a string, blank, not a GUID, or <see cref="Guid.Empty"/>.
    /// </returns>
    private static bool TryReadGuid(JsonElement root, string name, out Guid? value)
    {
        value = null;
        if (!TryReadString(root, name, out var text) || string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (!Guid.TryParse(text, out var parsed) || parsed == Guid.Empty)
        {
            return false;
        }

        value = parsed;
        return true;
    }
}
