using System.IO.Compression;
using System.Text;
using Serilog;
using Serilog.Sinks.File;
using VectorNNTP.Common.Logging;

namespace VectorNNTP.Common.Tests.Logging
{
    public sealed class GzipRollingFileSinkTests
    {
        [Fact]
        public void DailyRoll_CompressesTheCompletedDay_AndLeavesTheActiveLog()
        {
            using var dir = new TempDir();
            var time = new ManualTime(new DateTime(2026, 10, 1, 12, 0, 0));
            byte[]? completed = null;
            using (var logger = Create(dir.Path, time, retention: 14, onCompleted: path => completed = File.ReadAllBytes(path)))
            {
                logger.Information("day-one");
                time.AdvanceDays(1);
                logger.Information("day-two");
            }

            var dayOne = Path.Combine(dir.Path, "app-20261001.log");
            var dayTwo = Path.Combine(dir.Path, "app-20261002.log");
            Assert.False(File.Exists(dayOne));
            Assert.True(File.Exists(dayOne + ".gz"));
            Assert.True(File.Exists(dayTwo));
            Assert.False(File.Exists(dayTwo + ".gz"));
            Assert.NotNull(completed);
            Assert.Equal(completed, Inflate(dayOne + ".gz"));
            Assert.Contains("day-one", Encoding.UTF8.GetString(completed), StringComparison.Ordinal);
            Assert.Contains("day-two", File.ReadAllText(dayTwo), StringComparison.Ordinal);
            Assert.DoesNotContain("day-two", Encoding.UTF8.GetString(completed), StringComparison.Ordinal);
        }

        [Fact]
        public void Retention_CountsLogAndGzipDays_IncludingTheActiveFile()
        {
            using var dir = new TempDir();
            var time = new ManualTime(new DateTime(2026, 10, 1, 8, 0, 0));
            using (var logger = Create(dir.Path, time, retention: 3))
            {
                for (var day = 0; day < 5; day++)
                {
                    logger.Information("day-{Day}", time.Local.ToString("yyyyMMdd"));
                    if (day < 4)
                    {
                        time.AdvanceDays(1);
                    }
                }
            }

            var files = Directory.GetFiles(dir.Path).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
            Assert.Equal(
                new[] { "app-20261003.log.gz", "app-20261004.log.gz", "app-20261005.log" },
                files);
        }

        [Fact]
        public void Restart_RecognizesExistingLogAndGzip_AndAppliesRetention()
        {
            using var dir = new TempDir();
            File.WriteAllBytes(Path.Combine(dir.Path, "app-20261001.log.gz"), Gzip("already"));
            File.WriteAllText(Path.Combine(dir.Path, "app-20261002.log"), "plain-yesterday");
            var time = new ManualTime(new DateTime(2026, 10, 4, 8, 0, 0));
            using (var logger = Create(dir.Path, time, retention: 3))
            {
                logger.Information("today");
            }

            Assert.False(File.Exists(Path.Combine(dir.Path, "app-20261002.log")));
            Assert.Equal("plain-yesterday", Encoding.UTF8.GetString(Inflate(Path.Combine(dir.Path, "app-20261002.log.gz"))));
            Assert.True(File.Exists(Path.Combine(dir.Path, "app-20261001.log.gz")));
            Assert.True(File.Exists(Path.Combine(dir.Path, "app-20261004.log")));
            Assert.False(File.Exists(Path.Combine(dir.Path, "app-20261004.log.gz")));

            time.AdvanceDays(2);
            using (var logger = Create(dir.Path, time, retention: 3))
            {
                logger.Information("later");
            }

            var files = Directory.GetFiles(dir.Path).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
            Assert.Equal(
                new[] { "app-20261002.log.gz", "app-20261004.log.gz", "app-20261006.log" },
                files);
        }

        [Fact]
        public void CompressionFailure_KeepsTheOriginalLog()
        {
            using var dir = new TempDir();
            var time = new ManualTime(new DateTime(2026, 10, 1, 8, 0, 0));
            using (var logger = Create(dir.Path, time, retention: 14, compressor: new FailingCompressor()))
            {
                logger.Information("keep-me");
                time.AdvanceDays(1);
                logger.Information("next");
            }

            var failed = Path.Combine(dir.Path, "app-20261001.log");
            Assert.True(File.Exists(failed));
            Assert.False(File.Exists(failed + ".gz"));
            Assert.False(File.Exists(failed + GzipLogFileCompressor.PartialSuffix));
            Assert.Contains("keep-me", File.ReadAllText(failed), StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(dir.Path, "app-20261002.log")));
        }

        [Fact]
        public void ActiveFile_IsNotCompressedOrDeleted()
        {
            using var dir = new TempDir();
            var time = new ManualTime(new DateTime(2026, 10, 5, 8, 0, 0));
            string? handed = null;
            using (var logger = Create(dir.Path, time, retention: 1, onCompleted: path => handed = path))
            {
                logger.Information("only-today");
            }

            var active = Path.Combine(dir.Path, "app-20261005.log");
            Assert.True(File.Exists(active));
            Assert.False(File.Exists(active + ".gz"));
            Assert.Null(handed);
            Assert.Single(Directory.GetFiles(dir.Path));
        }

        [Fact]
        public void AsyncFlush_ThenNextDay_CompressesTheCompletedFile()
        {
            using var dir = new TempDir();
            var time = new ManualTime(new DateTime(2026, 10, 1, 8, 0, 0));
            byte[]? completed = null;
            using (var logger = Create(dir.Path, time, retention: 14, async: true, onCompleted: path => completed = File.ReadAllBytes(path)))
            {
                logger.Information("buffered-day");
            }

            var dayOne = Path.Combine(dir.Path, "app-20261001.log");
            Assert.True(File.Exists(dayOne));
            Assert.Contains("buffered-day", File.ReadAllText(dayOne), StringComparison.Ordinal);
            Assert.Null(completed);

            time.AdvanceDays(1);
            using (var logger = Create(dir.Path, time, retention: 14, async: true, onCompleted: path => completed = File.ReadAllBytes(path)))
            {
                logger.Information("next-buffered-day");
            }

            Assert.False(File.Exists(dayOne));
            Assert.NotNull(completed);
            Assert.Equal(completed, Inflate(dayOne + ".gz"));
            Assert.Contains("buffered-day", Encoding.UTF8.GetString(completed), StringComparison.Ordinal);
            Assert.Contains("next-buffered-day", File.ReadAllText(Path.Combine(dir.Path, "app-20261002.log")), StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(dir.Path, "app-20261002.log.gz")));
        }

        [Fact]
        public void InterruptedGzip_IsDiscarded_AndTheSourceIsCompressedOnRestart()
        {
            using var dir = new TempDir();
            var source = Path.Combine(dir.Path, "app-20261001.log");
            File.WriteAllText(source, "survived-the-crash");
            File.WriteAllBytes(source + GzipLogFileCompressor.PartialSuffix, [1, 2, 3, 4]);
            var time = new ManualTime(new DateTime(2026, 10, 2, 8, 0, 0));
            using (var logger = Create(dir.Path, time, retention: 14))
            {
                logger.Information("today");
            }

            Assert.False(File.Exists(source));
            Assert.False(File.Exists(source + GzipLogFileCompressor.PartialSuffix));
            Assert.Equal("survived-the-crash", Encoding.UTF8.GetString(Inflate(source + ".gz")));
            Assert.True(File.Exists(Path.Combine(dir.Path, "app-20261002.log")));
            Assert.False(File.Exists(Path.Combine(dir.Path, "app-20261002.log.gz")));
        }

        [Fact]
        public void ExistingGzip_IsNotRecompressed_AndLeftoverLogIsRemoved()
        {
            using var dir = new TempDir();
            var gzip = Gzip("stable");
            var path = Path.Combine(dir.Path, "app-20261001.log");
            File.WriteAllBytes(path + ".gz", gzip);
            File.WriteAllText(path, "leftover");
            var time = new ManualTime(new DateTime(2026, 10, 2, 8, 0, 0));
            using (var logger = Create(dir.Path, time, retention: 14))
            {
                logger.Information("today");
            }

            Assert.Equal(gzip, File.ReadAllBytes(path + ".gz"));
            Assert.False(File.Exists(path));
        }

        private static Serilog.Core.Logger Create(
            string directory,
            ManualTime time,
            int retention,
            bool async = false,
            ILogFileCompressor? compressor = null,
            Action<string>? onCompleted = null)
        {
            var path = Path.Combine(directory, "app-.log");
            var configuration = new LoggerConfiguration().MinimumLevel.Debug();
            if (async)
            {
                configuration.WriteTo.Async(
                    sink => sink.DailyGzipFile(
                        path,
                        buffered: true,
                        flushToDiskInterval: TimeSpan.FromSeconds(1),
                        rollingInterval: RollingInterval.Day,
                        rollOnFileSizeLimit: false,
                        retainedFileCountLimit: retention,
                        onCompletedFile: onCompleted,
                        compressor: compressor,
                        timeProvider: time),
                    bufferSize: 10,
                    blockWhenFull: true);
            }
            else
            {
                configuration.WriteTo.DailyGzipFile(
                    path,
                    buffered: false,
                    rollingInterval: RollingInterval.Day,
                    rollOnFileSizeLimit: false,
                    retainedFileCountLimit: retention,
                    onCompletedFile: onCompleted,
                    compressor: compressor,
                    timeProvider: time);
            }

            return configuration.CreateLogger();
        }

        private static byte[] Inflate(string path)
        {
            using var input = File.OpenRead(path);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            gzip.CopyTo(output);
            return output.ToArray();
        }

        private static byte[] Gzip(string text)
        {
            using var output = new MemoryStream();
            using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
            {
                gzip.Write(Encoding.UTF8.GetBytes(text));
            }

            return output.ToArray();
        }

        private sealed class ManualTime : TimeProvider
        {
            public ManualTime(DateTime local) => Local = local;

            public DateTime Local { get; private set; }

            public void AdvanceDays(int days) => Local = Local.AddDays(days);

            public override DateTimeOffset GetUtcNow() => new(Local, TimeSpan.Zero);

            public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
        }

        private sealed class FailingCompressor : ILogFileCompressor
        {
            public bool TryCompressAndReplace(string sourceLogPath) => false;
        }

        private sealed class TempDir : IDisposable
        {
            public TempDir()
            {
                Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vectornntp-gzip-roll-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path);
            }

            public string Path { get; }

            public void Dispose()
            {
                try
                {
                    Directory.Delete(Path, recursive: true);
                }
                catch (IOException)
                {
                }
            }
        }
    }
}
