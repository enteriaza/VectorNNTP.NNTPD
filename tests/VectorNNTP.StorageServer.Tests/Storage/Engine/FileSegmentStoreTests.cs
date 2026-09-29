using System.Buffers.Binary;
using System.Text;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Articles.Parsing;
using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Storage.Engine;
using VectorNNTP.StorageServer.Storage.Engine.FileSegments;

namespace VectorNNTP.StorageServer.Tests.Storage.Engine;

public sealed class FileSegmentStoreTests
{
    [Fact]
    public async Task A_CreateFirstSegment()
    {
        using var dir = TempSegmentDir.Create();
        using var store = FileSegmentStore.Open(dir.Options);
        var appender = await store.GetActiveAppenderAsync(CancellationToken.None);
        Assert.Equal(1UL, appender.SegmentId.Value);
        Assert.Equal(0, appender.SizeBytes);
        Assert.True(store.TryGetSegmentInfo(appender.SegmentId, out var info));
        Assert.Equal(SegmentState.Active, info.State);
    }

    [Fact]
    public async Task B_AppendOneArticle()
    {
        using var dir = TempSegmentDir.Create();
        using var store = FileSegmentStore.Open(dir.Options);
        var article = CreateArtData("<b-append@example.test>", "body\r\n");
        var appender = await store.GetActiveAppenderAsync(CancellationToken.None);
        var location = await appender.AppendAsync(article, CancellationToken.None);
        Assert.Equal(1UL, location.SegmentId.Value);
        Assert.Equal(0, location.Offset);
        Assert.Equal(SegmentRecordCodec.RecordLengthForArtSize(article.Length), location.Length);
        Assert.True(appender.SizeBytes > 0);
    }

    [Fact]
    public async Task C_ReadBackByLocation()
    {
        using var dir = TempSegmentDir.Create();
        using var store = FileSegmentStore.Open(dir.Options);
        var article = CreateArtData("<c-read@example.test>");
        var location = await (await store.GetActiveAppenderAsync(CancellationToken.None))
            .AppendAsync(article, CancellationToken.None);
        Assert.True(store.TryRead(location, out var read));
        Assert.True(read.Span.SequenceEqual(article));
    }

    [Fact]
    public async Task D_LocationCoversPhysicalRecord()
    {
        using var dir = TempSegmentDir.Create();
        using var store = FileSegmentStore.Open(dir.Options);
        var article = CreateArtData("<d-loc@example.test>", "payload\r\n");
        var location = await (await store.GetActiveAppenderAsync(CancellationToken.None))
            .AppendAsync(article, CancellationToken.None);
        Assert.Equal(SegmentRecordCodec.RecordLengthForArtSize(article.Length), location.Length);
        Assert.Equal(location.Length, store.GetActiveSizeBytes());
    }

    [Fact]
    public async Task E_MultipleSequentialAppends()
    {
        using var dir = TempSegmentDir.Create();
        using var store = FileSegmentStore.Open(dir.Options);
        var appender = await store.GetActiveAppenderAsync(CancellationToken.None);
        var locations = new List<StoredArticleLocation>();
        for (var i = 0; i < 5; i++)
        {
            var article = CreateArtData($"<e-{i}@example.test>", $"b{i}\r\n");
            locations.Add(await appender.AppendAsync(article, CancellationToken.None));
        }

        for (var i = 1; i < locations.Count; i++)
        {
            Assert.Equal(locations[i - 1].Offset + locations[i - 1].Length, locations[i].Offset);
        }
    }

    [Fact]
    public async Task F_MultipleArticlesReadIndependently()
    {
        using var dir = TempSegmentDir.Create();
        using var store = FileSegmentStore.Open(dir.Options);
        var appender = await store.GetActiveAppenderAsync(CancellationToken.None);
        var a = CreateArtData("<f-1@example.test>", "one\r\n");
        var b = CreateArtData("<f-2@example.test>", "two\r\n");
        var locA = await appender.AppendAsync(a, CancellationToken.None);
        var locB = await appender.AppendAsync(b, CancellationToken.None);
        Assert.True(store.TryRead(locB, out var readB));
        Assert.True(store.TryRead(locA, out var readA));
        Assert.True(readA.Span.SequenceEqual(a));
        Assert.True(readB.Span.SequenceEqual(b));
    }

    [Fact]
    public async Task G_SegmentRotationAtTargetSize()
    {
        using var dir = TempSegmentDir.Create(targetSegmentBytes: 512);
        using var store = FileSegmentStore.Open(dir.Options);
        var appender = await store.GetActiveAppenderAsync(CancellationToken.None);
        var firstId = appender.SegmentId;
        StoredArticleLocation? secondLoc = null;
        for (var i = 0; i < 20; i++)
        {
            var article = CreateArtData($"<g-{i}@example.test>", new string('x', 40) + "\r\n");
            var loc = await appender.AppendAsync(article, CancellationToken.None);
            if (loc.SegmentId.Value != firstId.Value)
            {
                secondLoc = loc;
                break;
            }
        }

        Assert.NotNull(secondLoc);
        Assert.True(secondLoc!.Value.SegmentId.Value > firstId.Value);
        Assert.True(store.TryGetSegmentInfo(firstId, out var closed));
        Assert.Equal(SegmentState.Closed, closed.State);
    }

    [Fact]
    public async Task H_ArticleLargerThanTarget_FitsInOneSegment()
    {
        using var dir = TempSegmentDir.Create(targetSegmentBytes: 256);
        using var store = FileSegmentStore.Open(dir.Options);
        var large = CreateExactSizeArtData("<h-large@example.test>", 2000);
        var loc = await (await store.GetActiveAppenderAsync(CancellationToken.None))
            .AppendAsync(large, CancellationToken.None);
        Assert.Equal(1UL, loc.SegmentId.Value);
        Assert.True(loc.Length > 256);
        Assert.True(store.TryRead(loc, out var read));
        Assert.Equal(large.Length, read.Length);
    }

    [Fact]
    public async Task I_MaximumArticleSize()
    {
        using var dir = TempSegmentDir.Create();
        using var store = FileSegmentStore.Open(dir.Options);
        var max = CreateExactSizeArtData("<i-max@example.test>", ArticleResourceLimits.MaxArticleBytes);
        var loc = await (await store.GetActiveAppenderAsync(CancellationToken.None))
            .AppendAsync(max, CancellationToken.None);
        Assert.True(store.TryRead(loc, out var read));
        Assert.Equal(ArticleResourceLimits.MaxArticleBytes, read.Length);
    }

    [Fact]
    public async Task J_ArticleLargerThanMax_Rejected()
    {
        using var dir = TempSegmentDir.Create();
        using var store = FileSegmentStore.Open(dir.Options);
        var over = new byte[ArticleResourceLimits.MaxArticleBytes + 1];
        over.AsSpan().Fill((byte)'x');
        var appender = await store.GetActiveAppenderAsync(CancellationToken.None);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await appender.AppendAsync(over, CancellationToken.None));
    }

    [Fact]
    public async Task K_ConcurrentAppends_DoNotCorrupt()
    {
        using var dir = TempSegmentDir.Create();
        using var store = FileSegmentStore.Open(dir.Options);
        var appender = await store.GetActiveAppenderAsync(CancellationToken.None);
        var tasks = Enumerable.Range(0, 16)
            .Select(async i =>
            {
                var article = CreateArtData($"<k-{i}@example.test>", $"body-{i}\r\n");
                var loc = await appender.AppendAsync(article, CancellationToken.None);
                return (article, loc);
            })
            .ToArray();
        var results = await Task.WhenAll(tasks);
        foreach (var (article, loc) in results)
        {
            Assert.True(store.TryRead(loc, out var read));
            Assert.True(read.Span.SequenceEqual(article));
        }

        var offsets = results.Select(static r => r.loc.Offset).OrderBy(static o => o).ToArray();
        for (var i = 1; i < offsets.Length; i++)
        {
            Assert.True(offsets[i] > offsets[i - 1]);
        }
    }

    [Fact]
    public async Task L_ConcurrentReads()
    {
        using var dir = TempSegmentDir.Create();
        using var store = FileSegmentStore.Open(dir.Options);
        var appender = await store.GetActiveAppenderAsync(CancellationToken.None);
        var article = CreateArtData("<l-read@example.test>");
        var loc = await appender.AppendAsync(article, CancellationToken.None);
        var reads = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            {
                Assert.True(store.TryRead(loc, out var data));
                return data.ToArray();
            })));
        Assert.All(reads, r => Assert.True(r.AsSpan().SequenceEqual(article)));
    }

    [Fact]
    public async Task M_ActiveSegment_RestartReopen()
    {
        using var dir = TempSegmentDir.Create();
        var article = CreateArtData("<m-restart@example.test>");
        StoredArticleLocation location;
        using (var storeA = FileSegmentStore.Open(dir.Options))
        {
            location = await (await storeA.GetActiveAppenderAsync(CancellationToken.None))
                .AppendAsync(article, CancellationToken.None);
        }

        using var storeB = FileSegmentStore.Open(dir.Options);
        Assert.True(storeB.TryRead(location, out var read));
        Assert.True(read.Span.SequenceEqual(article));
        Assert.Equal(1UL, (await storeB.GetActiveAppenderAsync(CancellationToken.None)).SegmentId.Value);
    }

    [Fact]
    public async Task N_TornFinalRecord_OnActive_TruncatedAndResumed()
    {
        using var dir = TempSegmentDir.Create();
        var first = CreateArtData("<n-keep@example.test>", "keep\r\n");
        var second = CreateArtData("<n-torn@example.test>", "torn\r\n");
        StoredArticleLocation firstLoc;
        using (var storeA = FileSegmentStore.Open(dir.Options))
        {
            var appender = await storeA.GetActiveAppenderAsync(CancellationToken.None);
            firstLoc = await appender.AppendAsync(first, CancellationToken.None);
            _ = await appender.AppendAsync(second, CancellationToken.None);
        }

        var path = Directory.EnumerateFiles(dir.SegmentDir, "seg-*.active").Single();
        var bytes = File.ReadAllBytes(path);
        File.WriteAllBytes(path, bytes.AsSpan(0, bytes.Length - 11).ToArray());

        using var storeB = FileSegmentStore.Open(dir.Options);
        Assert.True(storeB.TryRead(firstLoc, out var kept));
        Assert.True(kept.Span.SequenceEqual(first));
        Assert.False(storeB.TryRead(
            new StoredArticleLocation(firstLoc.SegmentId, firstLoc.Offset + firstLoc.Length, second.Length),
            out _));

        var third = CreateArtData("<n-after@example.test>", "after\r\n");
        var thirdLoc = await (await storeB.GetActiveAppenderAsync(CancellationToken.None))
            .AppendAsync(third, CancellationToken.None);
        Assert.True(storeB.TryRead(thirdLoc, out var after));
        Assert.True(after.Span.SequenceEqual(third));
    }

    [Fact]
    public async Task O_CorruptFinalRecord_OnClosed_FailsClosed()
    {
        using var dir = TempSegmentDir.Create(targetSegmentBytes: 200);
        using (var storeA = FileSegmentStore.Open(dir.Options))
        {
            var appender = await storeA.GetActiveAppenderAsync(CancellationToken.None);
            for (var i = 0; i < 10; i++)
            {
                _ = await appender.AppendAsync(
                    CreateArtData($"<o-{i}@example.test>", new string('y', 30) + "\r\n"),
                    CancellationToken.None);
            }

            await storeA.CloseActiveAsync(CancellationToken.None);
        }

        var closed = Directory.EnumerateFiles(dir.SegmentDir, "seg-*.closed").First();
        var bytes = File.ReadAllBytes(closed);
        bytes[^1] ^= 0xFF;
        File.WriteAllBytes(closed, bytes);

        _ = Assert.Throws<SegmentStoreCorruptException>(() => FileSegmentStore.Open(dir.Options));
    }

    [Fact]
    public async Task P_CorruptMiddleRecord_OnClosed_FailsClosed()
    {
        using var dir = TempSegmentDir.Create();
        using (var storeA = FileSegmentStore.Open(dir.Options))
        {
            var appender = await storeA.GetActiveAppenderAsync(CancellationToken.None);
            _ = await appender.AppendAsync(CreateArtData("<p-1@example.test>", "one\r\n"), CancellationToken.None);
            _ = await appender.AppendAsync(CreateArtData("<p-2@example.test>", "two\r\n"), CancellationToken.None);
            await storeA.CloseActiveAsync(CancellationToken.None);
        }

        var closed = Directory.EnumerateFiles(dir.SegmentDir, "seg-*.closed").Single();
        var bytes = File.ReadAllBytes(closed);
        var firstLen = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0, 4));
        Assert.True(firstLen < bytes.Length);
        bytes[(int)firstLen - 1] ^= 0xFF;
        File.WriteAllBytes(closed, bytes);

        _ = Assert.Throws<SegmentStoreCorruptException>(() => FileSegmentStore.Open(dir.Options));
    }

    [Fact]
    public async Task Q_WrongArticleId_Rejected()
    {
        using var dir = TempSegmentDir.Create();
        using var store = FileSegmentStore.Open(dir.Options);
        var article = CreateArtData("<q-id@example.test>");
        var loc = await (await store.GetActiveAppenderAsync(CancellationToken.None))
            .AppendAsync(article, CancellationToken.None);
        var artId = ArticleId.FromMessageId("<q-id@example.test>"u8);
        var wrong = ArticleId.FromMessageId("<q-other@example.test>"u8);
        var hash = System.IO.Hashing.XxHash3.HashToUInt64(article);
        Assert.False(store.TryReadProven(loc, wrong, hash, article.Length, out _));
        Assert.True(store.TryReadProven(loc, artId, hash, article.Length, out _));
    }

    [Fact]
    public async Task R_WrongArtHash_Rejected()
    {
        using var dir = TempSegmentDir.Create();
        using var store = FileSegmentStore.Open(dir.Options);
        var article = CreateArtData("<r-hash@example.test>");
        var loc = await (await store.GetActiveAppenderAsync(CancellationToken.None))
            .AppendAsync(article, CancellationToken.None);
        var artId = ArticleId.FromMessageId("<r-hash@example.test>"u8);
        Assert.False(store.TryReadProven(loc, artId, expectedArtHash: 1, article.Length, out _));
    }

    [Fact]
    public async Task S_WrongArtSize_Rejected()
    {
        using var dir = TempSegmentDir.Create();
        using var store = FileSegmentStore.Open(dir.Options);
        var article = CreateArtData("<s-size@example.test>");
        var loc = await (await store.GetActiveAppenderAsync(CancellationToken.None))
            .AppendAsync(article, CancellationToken.None);
        var artId = ArticleId.FromMessageId("<s-size@example.test>"u8);
        var hash = System.IO.Hashing.XxHash3.HashToUInt64(article);
        Assert.False(store.TryReadProven(loc, artId, hash, article.Length + 1, out _));
    }

    [Fact]
    public async Task T_TruncatedPhysicalRecord_Rejected()
    {
        using var dir = TempSegmentDir.Create();
        using var store = FileSegmentStore.Open(dir.Options);
        var article = CreateArtData("<t-trunc@example.test>");
        var loc = await (await store.GetActiveAppenderAsync(CancellationToken.None))
            .AppendAsync(article, CancellationToken.None);
        var truncated = loc with { Length = loc.Length - 5 };
        Assert.False(store.TryRead(truncated, out _));
    }

    [Fact]
    public async Task U_InvalidPhysicalRecordLength_Rejected()
    {
        using var dir = TempSegmentDir.Create();
        using var store = FileSegmentStore.Open(dir.Options);
        var article = CreateArtData("<u-len@example.test>");
        var loc = await (await store.GetActiveAppenderAsync(CancellationToken.None))
            .AppendAsync(article, CancellationToken.None);
        Assert.False(store.TryRead(loc with { Length = 3 }, out _));
        Assert.False(store.TryRead(loc with { Offset = loc.Offset + 1 }, out _));
    }

    [Fact]
    public async Task V_ClosedSegment_DoesNotReceiveFurtherAppends()
    {
        using var dir = TempSegmentDir.Create();
        using var store = FileSegmentStore.Open(dir.Options);
        var appender = await store.GetActiveAppenderAsync(CancellationToken.None);
        var firstId = appender.SegmentId;
        _ = await appender.AppendAsync(CreateArtData("<v-1@example.test>"), CancellationToken.None);
        var closedSize = appender.SizeBytes;
        await store.CloseActiveAsync(CancellationToken.None);

        var next = await (await store.GetActiveAppenderAsync(CancellationToken.None))
            .AppendAsync(CreateArtData("<v-2@example.test>"), CancellationToken.None);
        Assert.NotEqual(firstId, next.SegmentId);
        Assert.True(store.TryGetSegmentInfo(firstId, out var closed));
        Assert.Equal(SegmentState.Closed, closed.State);
        Assert.Equal(closedSize, closed.SizeBytes);
    }

    [Fact]
    public async Task W_RetiredSegment_CannotBecomeActive()
    {
        using var dir = TempSegmentDir.Create();
        using var store = FileSegmentStore.Open(dir.Options);
        var appender = await store.GetActiveAppenderAsync(CancellationToken.None);
        var id = appender.SegmentId;
        _ = await appender.AppendAsync(CreateArtData("<w-1@example.test>"), CancellationToken.None);
        await store.CloseActiveAsync(CancellationToken.None);
        Assert.True(store.TryGetSegmentInfo(id, out var closed));
        Assert.True(store.Catalogue.TryRetire(id, closed.Generation, DateTimeOffset.UtcNow));
        Assert.True(store.TryGetSegmentInfo(id, out var retired));
        Assert.Equal(SegmentState.Retired, retired.State);
        Assert.Throws<InvalidOperationException>(() =>
            store.Catalogue.Upsert(retired with { State = SegmentState.Active }));
    }

    [Fact]
    public async Task X_SegmentId_NeverReused()
    {
        using var dir = TempSegmentDir.Create();
        using var store = FileSegmentStore.Open(dir.Options);
        var a = (await store.GetActiveAppenderAsync(CancellationToken.None)).SegmentId;
        await store.CloseActiveAsync(CancellationToken.None);
        var b = (await store.GetActiveAppenderAsync(CancellationToken.None)).SegmentId;
        await store.CloseActiveAsync(CancellationToken.None);
        var c = (await store.GetActiveAppenderAsync(CancellationToken.None)).SegmentId;
        Assert.True(b.Value > a.Value);
        Assert.True(c.Value > b.Value);
    }

    [Fact]
    public async Task Y_Restart_DiscoversHighestSegmentIdAndContinues()
    {
        using var dir = TempSegmentDir.Create();
        ulong lastId;
        using (var storeA = FileSegmentStore.Open(dir.Options))
        {
            _ = await (await storeA.GetActiveAppenderAsync(CancellationToken.None))
                .AppendAsync(CreateArtData("<y-1@example.test>"), CancellationToken.None);
            await storeA.CloseActiveAsync(CancellationToken.None);
            lastId = (await storeA.GetActiveAppenderAsync(CancellationToken.None)).SegmentId.Value;
            _ = await (await storeA.GetActiveAppenderAsync(CancellationToken.None))
                .AppendAsync(CreateArtData("<y-2@example.test>"), CancellationToken.None);
        }

        using var storeB = FileSegmentStore.Open(dir.Options);
        Assert.Equal(lastId, (await storeB.GetActiveAppenderAsync(CancellationToken.None)).SegmentId.Value);
        Assert.Equal(lastId + 1, storeB.NextSegmentId);
    }

    [Fact]
    public async Task Z_NoHoleReuse_AppendRemainsAtEof()
    {
        using var dir = TempSegmentDir.Create();
        using var store = FileSegmentStore.Open(dir.Options);
        var appender = await store.GetActiveAppenderAsync(CancellationToken.None);
        var first = await appender.AppendAsync(CreateArtData("<z-1@example.test>", "aaaa\r\n"), CancellationToken.None);
        store.Catalogue.ApplyLiveDeadDelta(first.SegmentId, liveDelta: -first.Length, deadDelta: first.Length);
        Assert.True(store.TryGetSegmentInfo(first.SegmentId, out var afterDead));
        Assert.True(afterDead.DeadBytes >= first.Length);
        var second = await appender.AppendAsync(CreateArtData("<z-2@example.test>", "bbbb\r\n"), CancellationToken.None);
        Assert.Equal(first.Offset + first.Length, second.Offset);
    }

    [Fact]
    public async Task AA_LargeArticle_TargetOverflow_ThenNextRotates()
    {
        using var dir = TempSegmentDir.Create(targetSegmentBytes: 1024);
        using var store = FileSegmentStore.Open(dir.Options);
        var large = CreateExactSizeArtData("<aa-large@example.test>", 1500);
        var loc1 = await (await store.GetActiveAppenderAsync(CancellationToken.None))
            .AppendAsync(large, CancellationToken.None);
        Assert.Equal(1UL, loc1.SegmentId.Value);
        Assert.True(loc1.Length > 1024);

        var small = CreateArtData("<aa-next@example.test>", "next\r\n");
        var loc2 = await (await store.GetActiveAppenderAsync(CancellationToken.None))
            .AppendAsync(small, CancellationToken.None);
        Assert.Equal(2UL, loc2.SegmentId.Value);
        Assert.True(store.TryGetSegmentInfo(loc1.SegmentId, out var closed));
        Assert.Equal(SegmentState.Closed, closed.State);
    }

    [Fact]
    public async Task AB_AppendCompletesOnlyAfterFlushBoundary()
    {
        using var dir = TempSegmentDir.Create();
        using var store = FileSegmentStore.Open(dir.Options);
        var article = CreateArtData("<ab-flush@example.test>");
        var loc = await (await store.GetActiveAppenderAsync(CancellationToken.None))
            .AppendAsync(article, CancellationToken.None);

        // After successful append, dispose without further writes; bytes must be durable on reopen.
        store.Dispose();
        using var storeB = FileSegmentStore.Open(dir.Options);
        Assert.True(storeB.TryRead(loc, out var read));
        Assert.True(read.Span.SequenceEqual(article));
    }

    private static byte[] CreateArtData(string messageId, string body = "line1\r\nline2\r\n")
    {
        var parser = new NntpArticleParser("cache01.usenet.ninja");
        var builder = new StringBuilder();
        _ = builder.Append("Path: peer.example\r\n");
        _ = builder.Append("Date: Fri, 23 Aug 2024 07:30:10 +0000\r\n");
        _ = builder.Append("Message-ID: ").Append(messageId).Append("\r\n");
        _ = builder.Append("Newsgroups: alt.test\r\n");
        _ = builder.Append("From: user@example.test\r\n");
        _ = builder.Append("Subject: segment\r\n");
        _ = builder.Append("\r\n").Append(body);
        var created = ArticleRecordFactory.TryCreate(parser, Encoding.ASCII.GetBytes(builder.ToString()));
        Assert.True(created.IsAccepted, created.ParseFailure.ToString());
        return created.Record.ArtData.ToArray();
    }

    private static byte[] CreateExactSizeArtData(string messageId, int size)
    {
        var prefix = Encoding.ASCII.GetBytes(
            "Path: peer.example\r\nDate: Fri, 23 Aug 2024 07:30:10 +0000\r\nMessage-ID: "
            + messageId
            + "\r\nNewsgroups: alt.test\r\nFrom: user@example.test\r\nSubject: max\r\n\r\n");
        if (prefix.Length > size)
        {
            throw new InvalidOperationException("Prefix larger than requested size.");
        }

        var data = new byte[size];
        prefix.CopyTo(data.AsSpan());
        data.AsSpan(prefix.Length).Fill((byte)'x');
        return data;
    }

    private sealed class TempSegmentDir : IDisposable
    {
        private TempSegmentDir(string root, ArticleStorageRuntimeOptions options)
        {
            Root = root;
            Options = options;
        }

        public string Root { get; }

        public string SegmentDir => Options.SegmentDir;

        public ArticleStorageRuntimeOptions Options { get; }

        public static TempSegmentDir Create(long? targetSegmentBytes = null)
        {
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-segments-" + Guid.NewGuid().ToString("N"));
            var segment = Path.Combine(root, "cache");
            Directory.CreateDirectory(segment);
            var options = new ArticleStorageRuntimeOptions(
                ControlDir: Path.Combine(root, "control"),
                SegmentDir: segment,
                JournalSoftLimitBytes: ArticleStorageOptions.DefaultJournalSoftLimitBytes,
                JournalHardLimitBytes: ArticleStorageOptions.DefaultJournalHardLimitBytes,
                SegmentTargetSizeBytes: targetSegmentBytes ?? ArticleStorageOptions.DefaultSegmentTargetSizeBytes);
            return new TempSegmentDir(root, options);
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
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
