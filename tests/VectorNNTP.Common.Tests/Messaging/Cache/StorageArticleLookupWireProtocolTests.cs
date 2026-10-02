using System.Text;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Messaging.Cache;

namespace VectorNNTP.Common.Tests.Messaging.Cache;

public sealed class StorageArticleLookupWireProtocolTests
{
    [Fact]
    public void RequestAndResponse_RoundTrip()
    {
        var articleId = ArticleId.FromMessageId("<lookup@example.com>"u8);
        var request = new StorageArticleLookupRequest(1, Guid.NewGuid(), articleId);
        var requestBytes = StorageArticleLookupWireProtocol.SerializeRequestV1(request);
        Assert.True(StorageArticleLookupWireProtocol.TryParseRequestV1(requestBytes, out var parsedRequest, out _));
        Assert.NotNull(parsedRequest);
        Assert.Equal(request.RequestId, parsedRequest.RequestId);
        Assert.Equal(articleId, parsedRequest.ArticleId);

        var response = new StorageArticleLookupResponse(
            1,
            request.RequestId,
            2,
            "cache02.usenet.ninja",
            articleId,
            1191);
        var responseBytes = StorageArticleLookupWireProtocol.SerializeResponseV1(response);
        Assert.True(StorageArticleLookupWireProtocol.TryParseResponseV1(responseBytes, out var parsedResponse, out _));
        Assert.NotNull(parsedResponse);
        Assert.Equal(response.Fqdn, parsedResponse.Fqdn);
        Assert.Equal(response.VatpPort, parsedResponse.VatpPort);
        Assert.Equal(response.ArticleId, parsedResponse.ArticleId);
        Assert.DoesNotContain("uri", Encoding.UTF8.GetString(responseBytes), StringComparison.Ordinal);
        Assert.DoesNotContain("targetStorage", Encoding.UTF8.GetString(requestBytes), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryParseResponseV1_RejectsLegacyUriField()
    {
        var articleId = ArticleId.FromMessageId("<lookup@example.com>"u8);
        var legacy = "vatp" + "://" + "cache02.usenet.ninja:1191/" + articleId.ToLowerHexString();
        var json = Encoding.UTF8.GetBytes(
            $$"""{"version":1,"requestId":"{{Guid.NewGuid()}}","serverId":2,"fqdn":"cache02.usenet.ninja","articleId":"{{articleId.ToLowerHexString()}}","vatpPort":1191,"uri":"{{legacy}}"}""");
        Assert.False(StorageArticleLookupWireProtocol.TryParseResponseV1(json, out var response, out var reason));
        Assert.Null(response);
        Assert.Contains("uri", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TopologyConstants_DescribeFleetLookupBus()
    {
        Assert.Equal("cache.requests", CacheFleetTopology.RequestsExchangeName);
        Assert.Equal("fanout", CacheFleetTopology.FanoutExchangeType);
        Assert.Equal("1000", CacheFleetTopology.LookupExpirationMilliseconds);
        Assert.Equal(TimeSpan.FromSeconds(1), CacheFleetTopology.LookupTimeout);
        Assert.Equal(
            "cache.cache01.usenet.ninja",
            CacheFleetTopology.BuildStorageServerRequestQueueName("Cache01.Usenet.Ninja"));
    }
}
