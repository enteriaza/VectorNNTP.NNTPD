using System.IO.Compression;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTP.NNTPD.Logging;

namespace VectorNNTP.NNTPD.Tests.Logging;

public sealed class PathSurveyArchiveHooksTests
{
    [Fact]
    public void CompletedFile_IsExposedToHandler_BeforeGzip_AndUncompressedIsNotDeletedByTheHook()
    {
        var dir = CreateTempLogDir();
        try
        {
            var rolled = Path.Combine(dir, "inpaths-20260101.log");
            File.WriteAllText(rolled, "Path: hop.example\n");
            var handler = new RecordingCompletedPathSurveyFileHandler();
            var hooks = NntpdSerilogHooks.CreatePathSurveyHooks(
                handler,
                NullLogger.Instance);

            hooks.OnFileDeleting(rolled);

            Assert.Equal(rolled, Assert.Single(handler.Completed));
            Assert.False(Assert.Single(handler.GzipExistedAtHandoff));
            Assert.True(File.Exists(rolled));
            var archive = Path.Combine(dir, NntpdFileLogging.GzipArchiveFileName(rolled));
            Assert.True(File.Exists(archive));
            using var gz = new GZipStream(File.OpenRead(archive), CompressionMode.Decompress);
            using var reader = new StreamReader(gz);
            Assert.Equal("Path: hop.example\n", reader.ReadToEnd());
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public void HandlerFailure_DoesNotSkipGzip_AndDoesNotDeleteUncompressedFile()
    {
        var dir = CreateTempLogDir();
        try
        {
            var rolled = Path.Combine(dir, "inpaths-20260102.log");
            File.WriteAllText(rolled, "Path: still.archive\n");
            var handler = new ThrowingCompletedPathSurveyFileHandler();
            var hooks = NntpdSerilogHooks.CreatePathSurveyHooks(
                handler,
                NullLogger.Instance);

            hooks.OnFileDeleting(rolled);

            Assert.Equal(rolled, handler.Path);
            Assert.True(File.Exists(rolled));
            Assert.True(File.Exists(Path.Combine(dir, NntpdFileLogging.GzipArchiveFileName(rolled))));
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public void NewsGzipHook_RemainsDailyGzipFastest_WithoutPathSurveyHandler()
    {
        Assert.Same(
            NntpdSerilogHooks.DailyGzipFastest,
            NntpdNewsLogging.ResolveArchiveHooks(null));
        var dir = CreateTempLogDir();
        try
        {
            var rolled = Path.Combine(dir, "news-20260101.log");
            File.WriteAllText(rolled, "Sep  1 00:00:00.000 + ? <a@example> 1 ?\n");
            NntpdSerilogHooks.DailyGzipFastest.OnFileDeleting(rolled);
            Assert.True(File.Exists(rolled));
            Assert.True(File.Exists(Path.Combine(dir, NntpdFileLogging.GzipArchiveFileName(rolled))));
        }
        finally
        {
            TryDelete(dir);
        }
    }

    private static string CreateTempLogDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vectornntp-inpaths-hooks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDelete(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch
        {
            // best-effort
        }
    }
}
