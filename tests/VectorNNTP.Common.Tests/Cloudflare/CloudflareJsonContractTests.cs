using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VectorNNTP.NNTPD.Cloudflare;
using VectorNNTP.NNTPD.Configuration;

namespace VectorNNTP.Common.Tests.Cloudflare;

/// <summary>
/// Locks Cloudflare DNS JSON request/response contracts used by <see cref="CloudflareDnsClient"/>.
/// </summary>
public sealed class CloudflareJsonContractTests
{
    [Fact]
    public void WriteRequest_Serialize_OmitsNoFields_AndUsesSnakeNames()
    {
        var json = JsonSerializer.Serialize(
            new CloudflareDnsRecordWriteRequest
            {
                Type = "A",
                Name = "nntpd01.usenet.ninja",
                Content = "1.2.3.4",
                Ttl = 120,
                Proxied = false,
            },
            CloudflareJsonSerializerContext.Default.CloudflareDnsRecordWriteRequest);

        Assert.Equal(
            """{"type":"A","name":"nntpd01.usenet.ninja","content":"1.2.3.4","ttl":120,"proxied":false}""",
            json);
    }

    [Fact]
    public void ListResponse_Deserialize_ClosedGeneric()
    {
        const string payload =
            """
            {
              "success": true,
              "errors": [],
              "messages": [],
              "result": [
                {
                  "id": "rec-1",
                  "type": "A",
                  "name": "nntpd01.usenet.ninja",
                  "content": "1.2.3.4",
                  "ttl": 120,
                  "proxied": false,
                  "zone_id": "zone-1"
                }
              ],
              "result_info": {
                "page": 1,
                "per_page": 100,
                "total_pages": 1,
                "count": 1,
                "total_count": 1
              }
            }
            """;

        var envelope = JsonSerializer.Deserialize(
            payload,
            CloudflareJsonSerializerContext.Default.CloudflareApiResponseListCloudflareDnsRecord);

        Assert.NotNull(envelope);
        Assert.True(envelope.Success);
        var record = Assert.Single(envelope.Result!);
        Assert.Equal("rec-1", record.Id);
        Assert.Equal(120, record.Ttl);
        Assert.False(record.Proxied);
        Assert.Equal(1, envelope.ResultInfo!.TotalPages);
    }

    [Fact]
    public void RecordResponse_Deserialize_ClosedGeneric()
    {
        const string payload =
            """
            {
              "Success": true,
              "errors": [],
              "messages": [],
              "result": {
                "id": "rec-2",
                "TYPE": "TXT",
                "name": "_acme-challenge.example",
                "content": "token",
                "ttl": 60
              }
            }
            """;

        var envelope = JsonSerializer.Deserialize(
            payload,
            CloudflareJsonSerializerContext.Default.CloudflareApiResponseCloudflareDnsRecord);

        Assert.NotNull(envelope);
        Assert.True(envelope.Success);
        Assert.NotNull(envelope.Result);
        Assert.Equal("rec-2", envelope.Result.Id);
        Assert.Equal("TXT", envelope.Result.Type);
        Assert.Null(envelope.Result.Proxied);
    }

    [Fact]
    public void DeleteResponse_Deserialize_ClosedGeneric()
    {
        const string payload =
            """
            {
              "success": true,
              "errors": [],
              "messages": [],
              "result": { "id": "rec-del" }
            }
            """;

        var envelope = JsonSerializer.Deserialize(
            payload,
            CloudflareJsonSerializerContext.Default.CloudflareApiResponseCloudflareDeleteResult);

        Assert.NotNull(envelope);
        Assert.True(envelope.Success);
        Assert.Equal("rec-del", envelope.Result!.Id);
    }

    [Fact]
    public void ErrorResponse_Deserialize_PreservesCodes()
    {
        const string payload =
            """
            {
              "success": false,
              "errors": [ { "code": 10000, "message": "Authentication error" } ],
              "messages": [],
              "result": null
            }
            """;

        var envelope = JsonSerializer.Deserialize(
            payload,
            CloudflareJsonSerializerContext.Default.CloudflareApiResponseCloudflareDnsRecord);

        Assert.NotNull(envelope);
        Assert.False(envelope.Success);
        Assert.Null(envelope.Result);
        var error = Assert.Single(envelope.Errors);
        Assert.Equal(10000, error.Code);
        Assert.Equal("Authentication error", error.Message);
    }

    [Fact]
    public async Task Client_CreateRecord_UsesAotSafeRequestBody()
    {
        string? body = null;
        var handler = new CaptureHandler(async (request, cancellationToken) =>
        {
            body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "success": true,
                      "errors": [],
                      "messages": [],
                      "result": {
                        "id": "created",
                        "type": "A",
                        "name": "nntpd01.usenet.ninja",
                        "content": "1.2.3.4",
                        "ttl": 120,
                        "proxied": false,
                        "zone_id": "zone-1"
                      }
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"),
            };
        });

        var client = CreateClient(handler);
        var created = await client.CreateRecordAsync(
            "zone-1",
            new CloudflareDnsRecordWriteRequest
            {
                Type = CloudflareDnsRecordTypes.A,
                Name = "nntpd01.usenet.ninja",
                Content = "1.2.3.4",
                Ttl = 120,
                Proxied = false,
            },
            CancellationToken.None);

        Assert.Equal("created", created.Id);
        Assert.Equal(
            """{"type":"A","name":"nntpd01.usenet.ninja","content":"1.2.3.4","ttl":120,"proxied":false}""",
            body);
    }

    [Fact]
    public async Task Client_ListAndDelete_DeserializeClosedResponses()
    {
        var handler = new ScriptedHandler(
        [
            """
            {
              "success": true,
              "errors": [],
              "messages": [],
              "result": [
                {
                  "id": "txt-1",
                  "type": "TXT",
                  "name": "_acme-challenge.nntpd01.usenet.ninja",
                  "content": "v",
                  "ttl": 60,
                  "proxied": false,
                  "zone_id": "zone-1"
                }
              ],
              "result_info": {
                "page": 1,
                "per_page": 100,
                "total_pages": 1,
                "count": 1,
                "total_count": 1
              }
            }
            """,
            """
            {
              "success": true,
              "errors": [],
              "messages": [],
              "result": { "id": "txt-1" }
            }
            """,
        ]);

        var client = CreateClient(handler);
        var listed = await client.ListRecordsAsync(
            "zone-1",
            "_acme-challenge.nntpd01.usenet.ninja",
            CloudflareDnsRecordTypes.TXT,
            CancellationToken.None);
        Assert.Equal("txt-1", Assert.Single(listed).Id);

        await client.DeleteRecordAsync("zone-1", "txt-1", CancellationToken.None);
        Assert.Equal(2, handler.RequestCount);
    }

    private static CloudflareDnsClient CreateClient(HttpMessageHandler handler)
    {
        var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.cloudflare.com/client/v4/"),
        };
        return new CloudflareDnsClient(
            http,
            Options.Create(new AcmeCloudflareOptions { CloudFlareApiKey = "test-token" }),
            NullLogger<CloudflareDnsClient>.Instance);
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

        public CaptureHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) =>
            _handler = handler;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            _handler(request, cancellationToken);
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<string> _payloads;

        public ScriptedHandler(IEnumerable<string> payloads) =>
            _payloads = new Queue<string>(payloads);

        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            var payload = _payloads.Dequeue();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            });
        }
    }
}
