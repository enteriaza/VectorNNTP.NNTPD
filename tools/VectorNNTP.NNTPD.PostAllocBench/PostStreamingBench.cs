using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Text;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Jobs;
using VectorNNTP.NNTPD.Configuration;
using VectorNNTP.NNTPD.Networking.Proxy;
using VectorNNTP.NNTPD.Session.Commands.Posting;
using VectorNNTP.NNTPD.Session.Framing;

namespace VectorNNTP.NNTPD.PostAllocBench;

[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net10_0, warmupCount: 1, iterationCount: 4)]
[MarkdownExporter]
public class PostStreamingBench
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);
    private static readonly ConnectionClientIdentity Client =
        ConnectionClientIdentity.Direct(new IPEndPoint(IPAddress.Loopback, 119));

    private byte[] _stuffed = [];
    private StreamingPostReadOptions _options = null!;

    [Params(1024, 16 * 1024, 64 * 1024, 256 * 1024, 1024 * 1024, 5 * 1024 * 1024)]
    public int Size { get; set; }

    [Params("normal", "dot-leading", "multi-header")]
    public string Shape { get; set; } = "normal";

    [GlobalSetup]
    public void Setup()
    {
        _options = new StreamingPostReadOptions
        {
            MaxArticleSize = Math.Max(Size + 4096, NntpdOptions.DefaultMaxArticleSize),
            Time = new FixedTimeProvider(Now),
            NewsgroupPolicy = SyntaxOnlyNewsgroupPostingPolicy.Instance,
            InjectionIdentity = "nntpd01.usenet.ninja",
            ClientIdentity = Client,
            MailComplaintsTo = NntpdOptions.DefaultMailComplaintsTo,
            TraceProtector = DeterministicTraceProtector.Instance,
        };
        _stuffed = Encoding.ASCII.GetBytes(BuildArticle(Size, Shape));
    }

    [Benchmark(Baseline = true)]
    public int Legacy() => LegacyPostMaterialize.Process(_stuffed, _options).Length;

    [Benchmark]
    public int Streaming()
    {
        var pipe = new Pipe();
        pipe.Writer.Write(_stuffed);
        pipe.Writer.Complete();
        var result = StreamingPostArticleReader.ReadAsync(pipe.Reader, _options, CancellationToken.None)
            .AsTask()
            .GetAwaiter()
            .GetResult();
        if (result.Status != StreamingPostReadStatus.Completed)
        {
            throw new InvalidOperationException(result.Status + " " + result.Failure.Detail);
        }

        return result.Wire.Length;
    }

    internal static string BuildArticle(int destuffedTarget, string shape)
    {
        var sb = new StringBuilder(destuffedTarget + 64);
        sb.Append("Date: ").Append(PostRfcDate.Format(Now)).Append("\r\n");
        sb.Append("From: poster@example.com\r\n");
        sb.Append("Newsgroups: misc.test\r\n");
        sb.Append("Subject: bench\r\n");
        sb.Append("Message-ID: <bench@example.com>\r\n");
        if (shape == "multi-header")
        {
            sb.Append("User-Agent: VectorNNTP-bench/1.0\r\n");
            sb.Append("Organization: bench\r\n");
            sb.Append("References: <ref1@example.com> <ref2@example.com>\r\n");
            sb.Append("Distribution: world\r\n");
            sb.Append("Summary: allocation comparison article\r\n");
        }

        sb.Append("\r\n");
        var headerBytes = Encoding.ASCII.GetByteCount(sb.ToString());
        var bodyBudget = Math.Max(2, destuffedTarget - headerBytes);
        if (shape == "dot-leading")
        {
            while (bodyBudget > 0)
            {
                const string destuffedLine = ".dotline\r\n";
                if (bodyBudget < destuffedLine.Length)
                {
                    sb.Append(new string('Z', Math.Max(0, bodyBudget - 2)));
                    sb.Append("\r\n");
                    break;
                }

                sb.Append("..dotline\r\n");
                bodyBudget -= destuffedLine.Length;
            }
        }
        else
        {
            sb.Append(new string('Z', Math.Max(0, bodyBudget - 2)));
            sb.Append("\r\n");
        }

        sb.Append(".\r\n");
        return sb.ToString();
    }

    private sealed class FixedTimeProvider(DateTimeOffset utc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utc;
    }

    internal sealed class DeterministicTraceProtector : IPostingTraceProtector
    {
        public static DeterministicTraceProtector Instance { get; } = new();

        public string Protect(PostingTracePayload payload) => "v1.bench-token";

        public bool TryUnprotect(ReadOnlySpan<char> token, out PostingTracePayload payload)
        {
            payload = default;
            return false;
        }
    }
}

internal static class LegacyPostMaterialize
{
    public static ReadOnlyMemory<byte> Process(byte[] stuffedWithTerminator, StreamingPostReadOptions options)
    {
        var pipe = new Pipe();
        pipe.Writer.Write(stuffedWithTerminator);
        pipe.Writer.Complete();
        var destuffed = NntpMultilineDataReader
            .ReadArticleAsync(pipe.Reader, options.MaxArticleSize, CancellationToken.None)
            .AsTask()
            .GetAwaiter()
            .GetResult();
        if (destuffed.Status != NntpMultilineReadStatus.Completed
            || !PostHeaderParser.TryParse(destuffed.Payload, out var parsed, out _)
            || parsed is null
            || !PostArticleValidator.TryValidate(
                parsed,
                options.Time.GetUtcNow(),
                options.NewsgroupPolicy,
                out _))
        {
            throw new InvalidOperationException("legacy POST materialize failed");
        }

        var normalized = PostHeaderNormalizer.Normalize(
            parsed,
            options.Time.GetUtcNow(),
            options.InjectionIdentity,
            options.ClientIdentity,
            options.MailComplaintsTo,
            options.TraceProtector!);
        return ArticleWireReconstructor.RestuffArticle(normalized.Span, includeTerminator: false);
    }
}
