using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTP.Common.Articles;
using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Storage.ArticleIndex;
using VectorNNTP.StorageServer.Storage.SegmentPools;
using VectorNNTP.StorageServer.Storage.SpoolShards;

namespace VectorNNTP.StorageServer.Tests.Storage;

internal static class ArticleIndexTestSupport
{
    internal static ArticleId Id(int value) =>
        ArticleId.FromMessageId(Encoding.ASCII.GetBytes($"<{value}@index.test>"));

    internal static string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "vectornntp-article-index-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    internal static string PlacePayload(string spool, ArticleId articleId, string contents = "payload")
    {
        var path = SpoolArticlePath.CombineArticle(spool, articleId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        return path;
    }

    internal static ArticleIndexService Create(
        string databaseDirectory,
        SegmentPoolCatalog? catalog = null,
        ArticleIndexSettings? settings = null)
    {
        return new ArticleIndexService(
            new StorageServerOptions { DbDir = databaseDirectory },
            catalog ?? new SegmentPoolCatalog(),
            NullLogger<ArticleIndexService>.Instance,
            TimeProvider.System,
            settings ?? FastSettings());
    }

    internal static SegmentPoolCatalog Catalog(params string[] spools)
    {
        var catalog = new SegmentPoolCatalog();
        catalog.Publish([new SegmentPoolDevice("block:test", spools)]);
        return catalog;
    }

    internal static Task<ArticleIndexService> StartSpoolAsync(
        string databaseDirectory,
        string spool,
        ArticleIndexSettings? settings = null) =>
        StartAsync(databaseDirectory, Catalog(spool), settings);

    internal static async Task<ArticleIndexService> StartAsync(
        string databaseDirectory,
        SegmentPoolCatalog? catalog = null,
        ArticleIndexSettings? settings = null)
    {
        var service = Create(databaseDirectory, catalog, settings);
        await service.StartAsync(CancellationToken.None);
        return service;
    }

    internal static ArticleIndexSettings FastSettings() => new()
    {
        BatchSize = 32,
        FlushIntervalMilliseconds = 1,
        QueueCapacity = 1024,
        CommitAttempts = 4,
        BusyTimeoutMilliseconds = 200,
        SummaryIntervalMilliseconds = 60_000,
    };

    internal static async Task StopAsync(ArticleIndexService service)
    {
        await service.StopAsync(CancellationToken.None);
    }

    internal static void Delete(string path)
    {
        SqliteConnection.ClearAllPools();
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
                else if (File.Exists(path))
                {
                    File.Delete(path);
                }

                return;
            }
            catch (Exception ex) when (attempt < 9 && ex is IOException or UnauthorizedAccessException)
            {
                SqliteConnection.ClearAllPools();
                Thread.Sleep(50);
            }
        }
    }
}
