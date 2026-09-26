using System.Text;
using System.Text.Json;
using VectorNNTP.BackFiller.ArticleWork;
using VectorNNTP.BackFiller.Tests.Fixtures;

namespace VectorNNTP.BackFiller.Tests.ArticleWork;

public sealed class ArticleWorkResponseWireProtocolTests
{
    [Fact]
    public void Success_json_matches_the_canonical_protocol_example()
    {
        var json = ArticleWorkResponseWireProtocol.SerializeV1(SuccessIntent());

        Assert.Equal(ArticleWorkTestDeliveries.CanonicalSuccessResponseJson, Encoding.UTF8.GetString(json));
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal(JsonValueKind.Object, root.ValueKind);
        Assert.Equal(1, root.GetProperty("version").GetInt32());
        Assert.Equal(ArticleWorkTestDeliveries.CanonicalRequestId, root.GetProperty("requestId").GetString());
        Assert.Equal(ArticleWorkTestDeliveries.CanonicalMessageId, root.GetProperty("messageId").GetString());
        Assert.Equal("Giganews", root.GetProperty("backbone").GetString());
        Assert.Equal("Success", root.GetProperty("outcome").GetString());
        Assert.Equal(ArticleWorkTestDeliveries.CanonicalCacheUri, root.GetProperty("uri").GetString());
        Assert.False(root.TryGetProperty("error", out _));
        Assert.DoesNotContain("article", Encoding.UTF8.GetString(json), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("payload", Encoding.UTF8.GetString(json), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Terminal_failure_json_matches_the_canonical_protocol_example()
    {
        var json = ArticleWorkResponseWireProtocol.SerializeV1(new ArticleWorkResponseIntent(
            ArticleWorkOutcome.ArticleNotFound,
            Guid.Parse(ArticleWorkTestDeliveries.CanonicalRequestId),
            ArticleWorkTestDeliveries.CanonicalMessageId,
            "Giganews",
            ArticleWorkTestDeliveries.CanonicalCorrelationId,
            ArticleWorkTestDeliveries.CanonicalReplyTo,
            "No article with that message-id"));

        Assert.Equal(ArticleWorkTestDeliveries.CanonicalNotFoundResponseJson, Encoding.UTF8.GetString(json));
        using var document = JsonDocument.Parse(json);
        Assert.False(document.RootElement.TryGetProperty("uri", out _));
        Assert.Equal("No article with that message-id", document.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public void InvalidRequest_may_emit_null_identities_and_never_includes_uri()
    {
        var json = ArticleWorkResponseWireProtocol.SerializeV1(new ArticleWorkResponseIntent(
            ArticleWorkOutcome.InvalidRequest,
            null,
            null,
            null,
            ArticleWorkTestDeliveries.CanonicalCorrelationId,
            ArticleWorkTestDeliveries.CanonicalReplyTo,
            "Malformed JSON."));

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal(JsonValueKind.Null, root.GetProperty("requestId").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("messageId").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("backbone").ValueKind);
        Assert.Equal("InvalidRequest", root.GetProperty("outcome").GetString());
        Assert.False(root.TryGetProperty("uri", out _));
        Assert.Equal("Malformed JSON.", root.GetProperty("error").GetString());
    }

    [Fact]
    public void Property_names_are_exact_camelCase_protocol_fields()
    {
        using var document = JsonDocument.Parse(ArticleWorkResponseWireProtocol.SerializeV1(SuccessIntent()));
        var names = document.RootElement.EnumerateObject().Select(static property => property.Name).ToArray();
        Assert.Equal(["version", "requestId", "messageId", "backbone", "outcome", "uri"], names);
    }

    [Fact]
    public void Success_rejects_a_reconstructed_uri_that_does_not_bind_the_message_id()
    {
        var intent = SuccessIntent() with
        {
            Uri = "cache://backfiller01.usenet.ninja:119/ffffffffffffffffffffffffffffffff",
        };

        Assert.Throws<InvalidOperationException>(() => ArticleWorkResponseWireProtocol.SerializeV1(intent));
    }

    [Theory]
    [InlineData(ArticleWorkOutcome.ProviderFailure)]
    [InlineData(ArticleWorkOutcome.Cancelled)]
    [InlineData(ArticleWorkOutcome.UnexpectedFailure)]
    [InlineData(ArticleWorkOutcome.RetentionRejected)]
    public void Retryable_outcomes_are_not_serializable_terminal_responses(ArticleWorkOutcome outcome)
    {
        var intent = new ArticleWorkResponseIntent(
            outcome,
            Guid.Parse(ArticleWorkTestDeliveries.CanonicalRequestId),
            ArticleWorkTestDeliveries.CanonicalMessageId,
            "Giganews",
            ArticleWorkTestDeliveries.CanonicalCorrelationId,
            ArticleWorkTestDeliveries.CanonicalReplyTo,
            "retry");

        Assert.Throws<InvalidOperationException>(() => ArticleWorkResponseWireProtocol.SerializeV1(intent));
    }

    private static ArticleWorkResponseIntent SuccessIntent() =>
        new(
            ArticleWorkOutcome.Success,
            Guid.Parse(ArticleWorkTestDeliveries.CanonicalRequestId),
            ArticleWorkTestDeliveries.CanonicalMessageId,
            "Giganews",
            ArticleWorkTestDeliveries.CanonicalCorrelationId,
            ArticleWorkTestDeliveries.CanonicalReplyTo,
            Error: null,
            ArticleWorkTestDeliveries.CanonicalCacheUri);
}
