using System.Buffers;
using System.Text.Encodings.Web;
using System.Text.Json;
using VectorNNTP.BackFiller.Retention;

namespace VectorNNTP.BackFiller.ArticleWork;

/// <summary>
/// Compact UTF-8 JSON contract for Article Work v1 responses.
/// Property names are exact: version, requestId, messageId, backbone, outcome, uri, error.
/// </summary>
public static class ArticleWorkResponseWireProtocol
{
    /// <summary>Application protocol version.</summary>
    public const int CurrentVersion = 1;

    /// <summary>AMQP content type.</summary>
    public const string JsonContentType = "application/json";

    /// <summary>AMQP header carrying the logical request UUID.</summary>
    public const string RequestIdHeaderName = "RequestId";

    /// <summary>
    /// Worker response TTL in milliseconds. Locked NNTPD architecture uses the same value
    /// on requests and accepted responses. Old BackFiller publisher did not set Expiration.
    /// </summary>
    public const string ExpirationMilliseconds = "1000";

    /// <summary>Serializes one validated v1 response. Does not embed article bytes.</summary>
    /// <param name="intent">Pipeline intent, including the retention URI for Success.</param>
    /// <returns>Compact UTF-8 JSON.</returns>
    public static byte[] SerializeV1(ArticleWorkResponseIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        Validate(intent);

        var writer = new ArrayBufferWriter<byte>();
        using var json = new Utf8JsonWriter(writer, new JsonWriterOptions
        {
            Indented = false,
            SkipValidation = false,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });

        json.WriteStartObject();
        json.WriteNumber("version", CurrentVersion);
        if (intent.RequestId.HasValue)
        {
            json.WriteString("requestId", intent.RequestId.Value);
        }
        else
        {
            json.WriteNull("requestId");
        }

        if (intent.MessageId is null)
        {
            json.WriteNull("messageId");
        }
        else
        {
            json.WriteString("messageId", intent.MessageId);
        }

        if (intent.Backbone is null)
        {
            json.WriteNull("backbone");
        }
        else
        {
            json.WriteString("backbone", intent.Backbone);
        }

        json.WriteString("outcome", OutcomeName(intent.Outcome));
        if (intent.Outcome == ArticleWorkOutcome.Success)
        {
            json.WriteString("uri", intent.Uri);
        }
        else
        {
            json.WriteString("error", intent.Error);
        }

        json.WriteEndObject();
        json.Flush();
        return writer.WrittenSpan.ToArray();
    }

    /// <summary>Returns the protocol outcome name.</summary>
    public static string OutcomeName(ArticleWorkOutcome outcome) =>
        outcome switch
        {
            ArticleWorkOutcome.Success => "Success",
            ArticleWorkOutcome.ArticleNotFound => "ArticleNotFound",
            ArticleWorkOutcome.InvalidArticle => "InvalidArticle",
            ArticleWorkOutcome.InvalidRequest => "InvalidRequest",
            _ => throw new InvalidOperationException($"Outcome '{outcome}' is not a publishable terminal response."),
        };

    private static void Validate(ArticleWorkResponseIntent intent)
    {
        switch (intent.Outcome)
        {
            case ArticleWorkOutcome.Success:
                RequireIdentity(intent);
                if (string.IsNullOrWhiteSpace(intent.Uri))
                {
                    throw new InvalidOperationException("Success response requires a cache URI from retention.");
                }

                var identity = ArticleIdentity.FromExactMessageId(intent.MessageId!);
                if (!intent.Uri.EndsWith('/' + identity.Md5Hex, StringComparison.Ordinal)
                    || !intent.Uri.StartsWith("cache://", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Success uri must be the retention cache URI for the exact Message-ID.");
                }

                if (intent.Error is not null)
                {
                    throw new InvalidOperationException("Success response must not include error.");
                }

                return;

            case ArticleWorkOutcome.ArticleNotFound:
            case ArticleWorkOutcome.InvalidArticle:
                RequireIdentity(intent);
                if (intent.Uri is not null)
                {
                    throw new InvalidOperationException("Terminal failure response must not include uri.");
                }

                if (string.IsNullOrWhiteSpace(intent.Error))
                {
                    throw new InvalidOperationException("Terminal failure response requires a non-empty error.");
                }

                return;

            case ArticleWorkOutcome.InvalidRequest:
                if (intent.RequestId is { } empty && empty == Guid.Empty)
                {
                    throw new InvalidOperationException("InvalidRequest must not use Guid.Empty.");
                }

                if (intent.Uri is not null)
                {
                    throw new InvalidOperationException("InvalidRequest must not include uri.");
                }

                if (string.IsNullOrWhiteSpace(intent.Error))
                {
                    throw new InvalidOperationException("InvalidRequest requires a non-empty error.");
                }

                return;

            default:
                throw new InvalidOperationException($"Outcome '{intent.Outcome}' is not a publishable terminal response.");
        }
    }

    private static void RequireIdentity(ArticleWorkResponseIntent intent)
    {
        if (intent.RequestId is not { } requestId || requestId == Guid.Empty)
        {
            throw new InvalidOperationException("Terminal non-InvalidRequest response requires requestId.");
        }

        if (string.IsNullOrWhiteSpace(intent.MessageId))
        {
            throw new InvalidOperationException("Terminal non-InvalidRequest response requires messageId.");
        }

        if (string.IsNullOrWhiteSpace(intent.Backbone))
        {
            throw new InvalidOperationException("Terminal non-InvalidRequest response requires backbone.");
        }
    }
}
