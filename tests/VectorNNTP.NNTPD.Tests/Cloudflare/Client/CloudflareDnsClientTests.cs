using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VectorNNTP.NNTPD.Cloudflare;
using VectorNNTP.NNTPD.Configuration;

namespace VectorNNTP.NNTPD.Tests.Cloudflare.Client;

public sealed class CloudflareDnsClientTests
{
    [Fact]
    public async Task ListRecordsAsync_FollowsPagination_AndFiltersExactName()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueJson(
            HttpStatusCode.OK,
            """
            {
              "success": true,
              "result": [
                { "id": "1", "type": "A", "name": "nntpd01.usenet.ninja", "content": "198.18.0.1", "ttl": 1, "proxied": false },
                { "id": "x", "type": "A", "name": "other.usenet.ninja", "content": "198.18.0.2", "ttl": 1, "proxied": false }
              ],
              "result_info": { "page": 1, "per_page": 1, "total_pages": 2, "count": 1, "total_count": 2 }
            }
            """);
        handler.EnqueueJson(
            HttpStatusCode.OK,
            """
            {
              "success": true,
              "result": [
                { "id": "2", "type": "A", "name": "nntpd01.usenet.ninja", "content": "198.18.0.3", "ttl": 1, "proxied": false }
              ],
              "result_info": { "page": 2, "per_page": 1, "total_pages": 2, "count": 1, "total_count": 2 }
            }
            """);

        var client = CreateClient(handler);
        var records = await client.ListRecordsAsync(
            "zone",
            "nntpd01.usenet.ninja",
            "A",
            CancellationToken.None);

        Assert.Equal(2, records.Count);
        Assert.All(records, r => Assert.Equal("nntpd01.usenet.ninja", r.Name));
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("page=2", handler.Requests[1].RequestUri!.Query, StringComparison.Ordinal);
        Assert.DoesNotContain(TestHostFactory.TestCloudFlareApiKey, handler.Requests[0].RequestUri!.ToString());
    }

    [Fact]
    public async Task Send_AddsBearerAuthorization_WithoutExposingKeyInExceptions()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueJson(
            HttpStatusCode.Unauthorized,
            """
            { "success": false, "errors": [ { "code": 10000, "message": "Authentication error" } ], "result": null }
            """);

        var client = CreateClient(handler);
        var ex = await Assert.ThrowsAsync<CloudflareDnsException>(() =>
            client.ListRecordsAsync("zone", "nntpd01.usenet.ninja", "A", CancellationToken.None));

        Assert.Equal(401, ex.StatusCode);
        Assert.True(ex.IsPermanentFailure);
        Assert.Contains("Authentication error", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(TestHostFactory.TestCloudFlareApiKey, ex.Message, StringComparison.Ordinal);
        Assert.Equal(
            "Bearer",
            handler.Requests[0].Headers.Authorization!.Scheme);
        Assert.Equal(
            TestHostFactory.TestCloudFlareApiKey,
            handler.Requests[0].Headers.Authorization!.Parameter);
    }

    [Fact]
    public async Task Send_UnsuccessfulSuccessFlag_IsFailure()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueJson(
            HttpStatusCode.OK,
            """
            { "success": false, "errors": [ { "code": 9109, "message": "Unauthorized to access requested resource" } ], "result": [] }
            """);

        var client = CreateClient(handler);
        var ex = await Assert.ThrowsAsync<CloudflareDnsException>(() =>
            client.ListRecordsAsync("zone", "nntpd01.usenet.ninja", "A", CancellationToken.None));

        Assert.Contains("Unauthorized to access requested resource", ex.Message, StringComparison.Ordinal);
        Assert.Contains(9109, ex.CloudflareErrorCodes);
        Assert.True(ex.IsPermanentFailure);
    }

    [Fact]
    public async Task Send_RateLimit_RetriesThenSucceeds()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(
            new HttpResponseMessage((HttpStatusCode)429)
            {
                Headers = { RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMilliseconds(1)) },
                Content = new StringContent("""{"success":false,"errors":[{"code":1,"message":"rate"}]}""", Encoding.UTF8, "application/json"),
            });
        handler.EnqueueJson(
            HttpStatusCode.OK,
            """
            { "success": true, "result": [], "result_info": { "page": 1, "per_page": 100, "total_pages": 1, "count": 0, "total_count": 0 } }
            """);

        var client = CreateClient(handler);
        var records = await client.ListRecordsAsync("zone", "nntpd01.usenet.ninja", "A", CancellationToken.None);

        Assert.Empty(records);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task CreateUpdateDelete_UseExpectedPaths()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueJson(
            HttpStatusCode.OK,
            """{ "success": true, "result": { "id": "new", "type": "A", "name": "nntpd01.usenet.ninja", "content": "198.18.0.1", "ttl": 1, "proxied": false } }""");
        handler.EnqueueJson(
            HttpStatusCode.OK,
            """{ "success": true, "result": { "id": "new", "type": "A", "name": "nntpd01.usenet.ninja", "content": "198.18.0.2", "ttl": 1, "proxied": false } }""");
        handler.EnqueueJson(
            HttpStatusCode.OK,
            """{ "success": true, "result": { "id": "new" } }""");

        var client = CreateClient(handler);
        var created = await client.CreateRecordAsync(
            "zone",
            new CloudflareDnsRecordWriteRequest
            {
                Type = "A",
                Name = "nntpd01.usenet.ninja",
                Content = "198.18.0.1",
            },
            CancellationToken.None);
        Assert.Equal("new", created.Id);

        await client.UpdateRecordAsync(
            "zone",
            "new",
            new CloudflareDnsRecordWriteRequest
            {
                Type = "A",
                Name = "nntpd01.usenet.ninja",
                Content = "198.18.0.2",
            },
            CancellationToken.None);

        await client.DeleteRecordAsync("zone", "new", CancellationToken.None);

        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.Equal(HttpMethod.Put, handler.Requests[1].Method);
        Assert.Equal(HttpMethod.Delete, handler.Requests[2].Method);
        Assert.EndsWith("/zones/zone/dns_records", handler.Requests[0].RequestUri!.AbsolutePath, StringComparison.Ordinal);
        Assert.EndsWith("/zones/zone/dns_records/new", handler.Requests[2].RequestUri!.AbsolutePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Send_Cancellation_Propagates()
    {
        var handler = new ScriptedHttpMessageHandler
        {
            Delay = TimeSpan.FromSeconds(30),
        };
        handler.EnqueueJson(HttpStatusCode.OK, """{ "success": true, "result": [], "result_info": { "page": 1, "per_page": 100, "total_pages": 1 } }""");

        var client = CreateClient(handler);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.ListRecordsAsync("zone", "nntpd01.usenet.ninja", "A", cts.Token));
    }

    private static CloudflareDnsClient CreateClient(ScriptedHttpMessageHandler handler)
    {
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.cloudflare.com/client/v4/"),
        };

        var options = Options.Create(TestHostFactory.CreateValidOptions());
        return new CloudflareDnsClient(httpClient, options, NullLogger<CloudflareDnsClient>.Instance);
    }

    private sealed class ScriptedHttpMessageHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new();

        public List<HttpRequestMessage> Requests { get; } = [];

        public TimeSpan Delay { get; set; }

        public void EnqueueJson(HttpStatusCode statusCode, string json) =>
            Enqueue(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });

        public void Enqueue(HttpResponseMessage response) => _responses.Enqueue(response);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            // Clone request metadata we need after the call returns.
            var copy = new HttpRequestMessage(request.Method, request.RequestUri);
            if (request.Headers.Authorization is { } auth)
            {
                copy.Headers.Authorization = auth;
            }

            Requests.Add(copy);

            if (Delay > TimeSpan.Zero)
            {
                await Task.Delay(Delay, cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (_responses.Count == 0)
            {
                throw new InvalidOperationException("No scripted HTTP response remaining.");
            }

            return _responses.Dequeue();
        }
    }
}
