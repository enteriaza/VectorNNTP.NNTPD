using System.Text;
using Microsoft.Extensions.Logging;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Articles.Parsing;
using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Storage.Engine;
using VectorNNTP.StorageServer.Storage.Engine.Durable;

namespace VectorNNTP.StorageServer.Tests.Storage.Engine;

/// <summary>
/// IndexCommitted completion logs the canonical ArticleId hex, not the struct type name.
/// </summary>
public sealed class IndexCommittedLogTests
{
    [Fact]
    public async Task Recovery_IndexCommitted_logs_the_article_id_hex()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<recover-ic@example.test>");
        await using (var setup = FileArticleStorageEngine.Open(dir.Options))
        {
            setup.SuspendBackgroundPersist = true;
            Assert.Equal(
                ArticleAcceptOutcome.Accepted,
                (await setup.AcceptAsync(record, CancellationToken.None)).Outcome);
        }

        var logs = new CaptureLogger();
        await using var recovered = FileArticleStorageEngine.Open(dir.Options, logs);
        recovered.SuspendBackgroundPersist = true;
        await recovered.RecoverAsync(CancellationToken.None);

        Assert.Equal(0, recovered.PersistBatchCount);
        AssertLoggedArticleId(logs, record.ArtId);
    }

    [Fact]
    public async Task Live_IndexCommitted_batch_logs_the_article_id_hex()
    {
        using var dir = TempStorageDir.Create();
        var logs = new CaptureLogger();
        await using var engine = FileArticleStorageEngine.Open(dir.Options, logs);
        engine.SuspendBackgroundPersist = true;
        var record = CreateRecord("<live-ic@example.test>");
        Assert.Equal(
            ArticleAcceptOutcome.Accepted,
            (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);

        engine.TestEnqueueIncompleteWork();
        await engine.DrainPendingAsync(CancellationToken.None);

        Assert.Equal(1, engine.PersistBatchCount);
        AssertLoggedArticleId(logs, record.ArtId);
    }

    private static void AssertLoggedArticleId(CaptureLogger logs, ArticleId artId)
    {
        var message = Assert.Single(logs.IndexCommitted);
        var hex = artId.ToLowerHexString();
        Assert.Contains(hex, message, StringComparison.Ordinal);
        Assert.Contains("artId=" + hex, message, StringComparison.Ordinal);
        Assert.DoesNotContain("VectorNNTP.Common.Articles.ArticleId", message, StringComparison.Ordinal);
    }

    private static ArticleRecord CreateRecord(string messageId)
    {
        var parser = new NntpArticleParser("cache01.usenet.ninja");
        var builder = new StringBuilder();
        _ = builder.Append("Path: peer.example\r\n");
        _ = builder.Append("Date: Fri, 23 Aug 2024 07:30:10 +0000\r\n");
        _ = builder.Append("Message-ID: ").Append(messageId).Append("\r\n");
        _ = builder.Append("Newsgroups: alt.test\r\n");
        _ = builder.Append("From: user@example.test\r\n");
        _ = builder.Append("Subject: index-committed-log\r\n");
        _ = builder.Append("\r\n").Append("line1\r\nline2\r\n");
        var created = ArticleRecordFactory.TryCreate(parser, Encoding.ASCII.GetBytes(builder.ToString()));
        Assert.True(created.IsAccepted, created.ParseFailure.ToString());
        return created.Record;
    }

    private sealed class CaptureLogger : ILogger
    {
        public List<string> IndexCommitted { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (eventId.Id == 3407)
            {
                IndexCommitted.Add(formatter(state, exception));
            }
        }
    }

    private sealed class TempStorageDir : IDisposable
    {
        private TempStorageDir(string root, ArticleStorageRuntimeOptions options)
        {
            Root = root;
            Options = options;
        }

        public string Root { get; }

        public ArticleStorageRuntimeOptions Options { get; }

        public static TempStorageDir Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-ic-log-" + Guid.NewGuid().ToString("N"));
            var control = Path.Combine(root, "control");
            var cache = Path.Combine(root, "cache");
            Directory.CreateDirectory(control);
            Directory.CreateDirectory(cache);
            return new TempStorageDir(
                root,
                new ArticleStorageRuntimeOptions(
                    ControlDir: control,
                    SegmentDir: cache,
                    JournalSoftLimitBytes: ArticleStorageOptions.DefaultJournalSoftLimitBytes,
                    JournalHardLimitBytes: ArticleStorageOptions.DefaultJournalHardLimitBytes,
                    SegmentTargetSizeBytes: ArticleStorageOptions.DefaultSegmentTargetSizeBytes));
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }
            }
            catch (IOException)
            {
            }
        }
    }
}
