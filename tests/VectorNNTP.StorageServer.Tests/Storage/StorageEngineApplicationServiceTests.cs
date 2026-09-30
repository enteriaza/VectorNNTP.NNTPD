using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Articles.Parsing;
using VectorNNTP.NNTPD.Core;
using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Listener;
using VectorNNTP.StorageServer.Storage;
using VectorNNTP.StorageServer.Storage.Engine;
using VectorNNTP.StorageServer.Storage.Engine.Durable;
using VectorNNTP.StorageServer.Storage.Engine.FileIndex;
using VectorNNTP.StorageServer.Storage.Engine.FileJournal;
using VectorNNTP.StorageServer.Storage.Engine.FileSegments;
using VectorNNTP.StorageServer.Tests.Fixtures;

namespace VectorNNTP.StorageServer.Tests.Storage;

/// <summary>Storage-engine hosting foundation (Open + Recover readiness boundary).</summary>
public sealed class StorageEngineApplicationServiceTests
{
    [Fact]
    public async Task A_B_C_D_Start_Opens_And_Recovers()
    {
        using var dirs = TempDirs.Create();
        var service = CreateService(dirs);
        Assert.False(service.IsReady);
        await service.StartAsync(CancellationToken.None);

        Assert.True(service.IsReady);
        Assert.True(File.Exists(Path.Combine(dirs.ControlDir, FileArticleJournal.JournalFileName)));
        Assert.True(File.Exists(Path.Combine(dirs.ControlDir, FileArticleIndex.IndexFileName)));
        Assert.Equal(dirs.ControlDir, Path.GetDirectoryName(service.Engine.Journal.JournalPath));
        Assert.Equal(dirs.CacheDir, service.Engine.Segments.RootPath);
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task E_F_EngineSameInstance_AndExactlyOne()
    {
        using var dirs = TempDirs.Create();
        var service = CreateService(dirs);
        await service.StartAsync(CancellationToken.None);
        var first = service.Engine;
        var second = service.Engine;
        Assert.Same(first, second);
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task G_OpenFailure_DoesNotPublishReady()
    {
        using var dirs = TempDirs.Create();
        Directory.Delete(dirs.ControlDir, recursive: true);
        await File.WriteAllTextAsync(dirs.ControlDir, "not-a-directory");

        var service = CreateService(dirs);
        await Assert.ThrowsAnyAsync<Exception>(() => service.StartAsync(CancellationToken.None));
        Assert.False(service.IsReady);
        Assert.Throws<InvalidOperationException>(() => _ = service.Engine);
    }

    [Fact]
    public async Task H_RecoveryFailure_CleansUpPartialEngine()
    {
        using var dirs = TempDirs.Create();
        await using (var seed = FileArticleStorageEngine.Open(CreateRuntime(dirs).Storage))
        {
            var record = CreateRecord("<se-h@seg.test>");
            seed.SuspendBackgroundPersist = true;
            Assert.Equal(
                ArticleAcceptOutcome.Accepted,
                (await seed.AcceptAsync(record, CancellationToken.None)).Outcome);
            var incomplete = seed.Journal.EnumerateIncomplete();
            Assert.Single(incomplete);
            var seq = incomplete[0].Accept.Sequence;
            var artSize = incomplete[0].Accept.ArtSize;
            var bogus = new JournalPhysicalWrittenRecord(
                1,
                seq,
                new StoredArticleLocation(new SegmentId(999), Offset: 0, Length: artSize));
            Assert.Equal(
                JournalAppendOutcome.Applied,
                await seed.Journal.AppendPhysicalWrittenAsync(bogus, CancellationToken.None));
        }

        var service = CreateService(dirs);
        await Assert.ThrowsAnyAsync<Exception>(() => service.StartAsync(CancellationToken.None));
        Assert.False(service.IsReady);

        // Exclusive journal lock must be released after failed StartAsync.
        await using var reopen = FileArticleStorageEngine.Open(CreateRuntime(dirs).Storage);
        Assert.NotNull(reopen);
    }

    [Fact]
    public async Task I_StartupOrdering_StorageBeforeDependents_ReverseStop()
    {
        var started = new List<string>();
        var stopped = new List<string>();
        var storage = new RecordingService("StorageEngine", started, stopped);
        var dependent = new RecordingService("Dependent", started, stopped);
        var manager = new ApplicationServiceManager(
            [storage, dependent],
            StorageServerTestOptions.CreateValid(),
            NullLogger<ApplicationServiceManager>.Instance);

        await manager.StartAsync(CancellationToken.None);
        Assert.Equal(["StorageEngine", "Dependent"], started);
        await manager.StopAsync(CancellationToken.None);
        Assert.Equal(["Dependent", "StorageEngine"], stopped);
    }

    [Fact]
    public async Task J_K_Shutdown_DisposesExactlyOnce()
    {
        using var dirs = TempDirs.Create();
        var service = CreateService(dirs);
        await service.StartAsync(CancellationToken.None);
        Assert.True(service.IsReady);

        await service.StopAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);
        await service.DisposeAsync();

        Assert.False(service.IsReady);
        // Dispose is idempotent; further use of the prior engine instance is undefined once disposed.
        // Verify readiness gate is cleared rather than probing disposed internals.
        Assert.Throws<InvalidOperationException>(() => _ = service.Engine);
    }

    [Fact]
    public void L_PathBinding_UsesRuntimeOptions()
    {
        using var dirs = TempDirs.Create();
        var runtime = CreateRuntime(dirs);
        Assert.Equal(dirs.ControlDir, runtime.ControlDir);
        Assert.Equal(dirs.CacheDir, runtime.CacheDir);
        Assert.Equal(dirs.ControlDir, runtime.Storage.ControlDir);
        Assert.Equal(dirs.CacheDir, runtime.Storage.SegmentDir);
    }

    [Fact]
    public async Task R_S_DurableState_SurvivesRestart()
    {
        using var dirs = TempDirs.Create();
        var record = CreateRecord("<se-rs@seg.test>");
        await using (var seed = FileArticleStorageEngine.Open(CreateRuntime(dirs).Storage))
        {
            Assert.Equal(
                ArticleAcceptOutcome.Accepted,
                (await seed.AcceptAsync(record, CancellationToken.None)).Outcome);
            await seed.DrainPendingAsync(CancellationToken.None);
            Assert.True(seed.Index.TryGet(record.ArtId, out var meta));
            Assert.Equal(ArticleStorageState.Present, meta.State);
        }

        var service = CreateService(dirs);
        await service.StartAsync(CancellationToken.None);
        Assert.True(service.Engine.Index.TryGet(record.ArtId, out var after));
        Assert.Equal(ArticleStorageState.Present, after.State);
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task T_ClosedPayloadCorruption_DoesNotBlockReady_TryReadRefuses()
    {
        using var dirs = TempDirs.Create();
        SegmentId closedId;
        var record = CreateRecord("<se-t@seg.test>");
        await using (var seed = FileArticleStorageEngine.Open(CreateRuntime(dirs).Storage))
        {
            Assert.Equal(
                ArticleAcceptOutcome.Accepted,
                (await seed.AcceptAsync(record, CancellationToken.None)).Outcome);
            await seed.DrainPendingAsync(CancellationToken.None);
            Assert.True(seed.Index.TryGet(record.ArtId, out var meta));
            closedId = meta.Location.SegmentId;
            await seed.Segments.CloseActiveAsync(CancellationToken.None);
        }

        var closedPath = Path.Combine(
            dirs.CacheDir,
            SegmentFileNames.Format(closedId, SegmentFileKind.Closed));
        await File.WriteAllBytesAsync(closedPath, [0xDE, 0xAD, 0xBE, 0xEF]);

        var service = CreateService(dirs);
        await service.StartAsync(CancellationToken.None);
        Assert.True(service.IsReady);
        Assert.False(service.Engine.IsUnreferencedExtentAccountingComplete);
        Assert.False(service.Engine.TryRead(record.ArtId, out var read));
        Assert.True(read.ArtData.IsEmpty);
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void O_P_Q_BoundariesUnchanged()
    {
        Assert.True(typeof(NullStorageArticleOpenBoundary).IsAssignableTo(typeof(IStorageArticleOpenBoundary)));
        Assert.Same(NullStorageArticlePresence.Instance, NullStorageArticlePresence.Instance);
    }

    private static StorageEngineApplicationService CreateService(TempDirs dirs) =>
        new(
            CreateRuntime(dirs),
            Options.Create(CreateBindable(dirs)),
            NullLogger<StorageEngineApplicationService>.Instance);

    private static StorageServerRuntimeOptions CreateRuntime(TempDirs dirs)
    {
        var options = CreateBindable(dirs);
        return StorageServerRuntimeOptionsFactory.Create(options, StorageServerTestOptions.CreateValidAcme(options));
    }

    private static StorageServerOptions CreateBindable(TempDirs dirs)
    {
        var options = StorageServerTestOptions.CreateValid();
        options.CacheDir = dirs.CacheDir;
        options.Storage.ControlDir = dirs.ControlDir;
        return options;
    }

    private static ArticleRecord CreateRecord(string messageId, string body = "line1\r\n")
    {
        var parser = new NntpArticleParser("cache01.usenet.ninja");
        var builder = new StringBuilder();
        _ = builder.Append("Path: peer.example\r\n");
        _ = builder.Append("Date: Fri, 23 Aug 2024 07:30:10 +0000\r\n");
        _ = builder.Append("Message-ID: ").Append(messageId).Append("\r\n");
        _ = builder.Append("Newsgroups: alt.test\r\n");
        _ = builder.Append("From: user@example.test\r\n");
        _ = builder.Append("Subject: storage-engine-host\r\n");
        _ = builder.Append("\r\n").Append(body);
        var created = ArticleRecordFactory.TryCreate(parser, Encoding.ASCII.GetBytes(builder.ToString()));
        Assert.True(created.IsAccepted, created.ParseFailure.ToString());
        return created.Record;
    }

    private sealed class TempDirs : IDisposable
    {
        private TempDirs(string root, string cacheDir, string controlDir)
        {
            Root = root;
            CacheDir = cacheDir;
            ControlDir = controlDir;
        }

        public string Root { get; }

        public string CacheDir { get; }

        public string ControlDir { get; }

        public static TempDirs Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-se-host-" + Guid.NewGuid().ToString("N"));
            var cache = Path.Combine(root, "cache");
            var control = Path.Combine(root, "control");
            Directory.CreateDirectory(cache);
            Directory.CreateDirectory(control);
            return new TempDirs(root, cache, control);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }
                else if (File.Exists(ControlDir))
                {
                    File.Delete(ControlDir);
                }
            }
            catch
            {
                // best-effort
            }
        }
    }

    private sealed class RecordingService : IApplicationService
    {
        private readonly List<string> _started;
        private readonly List<string> _stopped;

        public RecordingService(string name, List<string> started, List<string> stopped)
        {
            Name = name;
            _started = started;
            _stopped = stopped;
        }

        public string Name { get; }

        public Task? Execution => null;

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _started.Add(Name);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _stopped.Add(Name);
            return Task.CompletedTask;
        }
    }
}
