using System.Text;
using System.Text.Json;
using VectorNNTP.NNTPD.RabbitMq.ArticleWork;

namespace VectorNNTP.NNTPD.Tests.RabbitMq.ArticleWork;

public sealed class ArticleWorkWireProtocolTests
{
    private static readonly Guid RequestId = Guid.Parse("7c1cb8a0-95f9-4c13-8e53-339773e3afaa");

    [Fact]
    public void SerializeRequestV1_IsCompactCanonicalJson_WithoutTransportFields()
    {
        var request = new ArticleWorkRequest(1, RequestId, "<12345@example.invalid>", "Giganews");
        var bytes = ArticleWorkWireProtocol.SerializeRequestV1(request);
        var json = Encoding.UTF8.GetString(bytes);

        Assert.Equal(
            """{"version":1,"requestId":"7c1cb8a0-95f9-4c13-8e53-339773e3afaa","messageId":"<12345@example.invalid>","backbone":"Giganews"}""",
            json);
        Assert.DoesNotContain('\n', json);
        Assert.DoesNotContain("CorrelationId", json, StringComparison.Ordinal);
        Assert.DoesNotContain("ReplyTo", json, StringComparison.Ordinal);
        Assert.DoesNotContain("correlationId", json, StringComparison.Ordinal);
        Assert.DoesNotContain("replyTo", json, StringComparison.Ordinal);

        using var document = JsonDocument.Parse(bytes);
        Assert.Equal(1, document.RootElement.GetProperty("version").GetInt32());
        Assert.Equal(RequestId, document.RootElement.GetProperty("requestId").GetGuid());
        Assert.Equal("<12345@example.invalid>", document.RootElement.GetProperty("messageId").GetString());
        Assert.Equal("Giganews", document.RootElement.GetProperty("backbone").GetString());
        Assert.Equal(4, document.RootElement.EnumerateObject().Count());
    }

    [Fact]
    public void TryParseResponseV1_AcceptsCanonicalSuccess()
    {
        var json = """{"version":1,"requestId":"7c1cb8a0-95f9-4c13-8e53-339773e3afaa","messageId":"<12345@example.invalid>","backbone":"Giganews","outcome":"Success","uri":"cache://backfiller01.usenet.ninja:119/30edc94157aa16fe644a45a1f1ffe160"}"""u8;
        Assert.True(ArticleWorkWireProtocol.TryParseResponseV1(json, out var response, out var reason));
        Assert.Equal(string.Empty, reason);
        Assert.NotNull(response);
        Assert.Equal(ArticleWorkOutcome.Success, response.Outcome);
        Assert.Equal(RequestId, response.RequestId);
        Assert.Equal("<12345@example.invalid>", response.MessageId);
        Assert.Equal("Giganews", response.Backbone);
        Assert.Equal("cache://backfiller01.usenet.ninja:119/30edc94157aa16fe644a45a1f1ffe160", response.Uri);
        Assert.Null(response.Error);
    }

    [Fact]
    public void TryParseResponseV1_AcceptsCanonicalArticleNotFound()
    {
        var json = """{"version":1,"requestId":"7c1cb8a0-95f9-4c13-8e53-339773e3afaa","messageId":"<12345@example.invalid>","backbone":"Giganews","outcome":"ArticleNotFound","error":"No article with that message-id"}"""u8;
        Assert.True(ArticleWorkWireProtocol.TryParseResponseV1(json, out var response, out _));
        Assert.Equal(ArticleWorkOutcome.ArticleNotFound, response!.Outcome);
        Assert.Null(response.Uri);
        Assert.Equal("No article with that message-id", response.Error);
    }

    [Fact]
    public void TryParseResponseV1_RejectsSuccessWithError()
    {
        var json = """{"version":1,"requestId":"7c1cb8a0-95f9-4c13-8e53-339773e3afaa","messageId":"<12345@example.invalid>","backbone":"Giganews","outcome":"Success","uri":"cache://backfiller01.usenet.ninja:119/30edc94157aa16fe644a45a1f1ffe160","error":"nope"}"""u8;
        Assert.False(ArticleWorkWireProtocol.TryParseResponseV1(json, out var response, out var reason));
        Assert.Null(response);
        Assert.Contains("error", reason, StringComparison.Ordinal);
    }
}
