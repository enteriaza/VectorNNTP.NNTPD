using Serilog;
using Serilog.Configuration;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting;
using Serilog.Formatting.Display;
using Serilog.Sinks.File;

namespace VectorNNTP.Common.Logging
{
    /// <summary>
    /// Registers <see cref="GzipRollingFileSink"/> with the same flush wrapping as Serilog's File sink.
    /// </summary>
    public static class DailyGzipFileLoggerConfigurationExtensions
    {
        /// <summary>
        /// Writes a rolling file that gzip-compresses completed periods and retains <c>.log</c> and <c>.log.gz</c> together.
        /// </summary>
        /// <param name="sinkConfiguration">Serilog sink configuration, including the delegate inside <c>WriteTo.Async</c>.</param>
        /// <param name="path">Rolling path such as <c>logs/app-.log</c>.</param>
        /// <param name="restrictedToMinimumLevel">Minimum level for this sink.</param>
        /// <param name="outputTemplate">Serilog output template for the active file.</param>
        /// <param name="fileSizeLimitBytes">Active-file size cap. <see langword="null"/> is unlimited.</param>
        /// <param name="buffered">Whether the active file buffers writes.</param>
        /// <param name="flushToDiskInterval">
        /// When set, wraps the rolling sink with <see cref="PeriodicFlushToDiskSink"/>.
        /// </param>
        /// <param name="rollingInterval">Roll period. Production file targets use <see cref="RollingInterval.Day"/>.</param>
        /// <param name="rollOnFileSizeLimit">Whether a full active file rolls to the next sequence.</param>
        /// <param name="retainedFileCountLimit">Daily files to keep, including the active file.</param>
        /// <param name="onCompletedFile">Called with the closed uncompressed path before compression.</param>
        /// <param name="compressor">Compression implementation. Defaults to <see cref="GzipLogFileCompressor"/>.</param>
        /// <param name="timeProvider">Clock used for roll checkpoints.</param>
        /// <returns>The logger configuration, so calls can be chained.</returns>
        public static LoggerConfiguration DailyGzipFile(
            this LoggerSinkConfiguration sinkConfiguration,
            string path,
            LogEventLevel restrictedToMinimumLevel = LogEventLevel.Verbose,
            string outputTemplate = "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}",
            long? fileSizeLimitBytes = null,
            bool buffered = false,
            TimeSpan? flushToDiskInterval = null,
            RollingInterval rollingInterval = RollingInterval.Infinite,
            bool rollOnFileSizeLimit = false,
            int? retainedFileCountLimit = 31,
            Action<string>? onCompletedFile = null,
            ILogFileCompressor? compressor = null,
            TimeProvider? timeProvider = null)
        {
            ArgumentNullException.ThrowIfNull(sinkConfiguration);
            return sinkConfiguration.DailyGzipFile(
                new MessageTemplateTextFormatter(outputTemplate),
                path,
                restrictedToMinimumLevel,
                fileSizeLimitBytes,
                buffered,
                flushToDiskInterval,
                rollingInterval,
                rollOnFileSizeLimit,
                retainedFileCountLimit,
                onCompletedFile,
                compressor,
                timeProvider);
        }

        /// <summary>
        /// Writes a rolling file using <paramref name="formatter"/> instead of an output template.
        /// </summary>
        /// <param name="sinkConfiguration">Serilog sink configuration.</param>
        /// <param name="formatter">Formatter owned by the caller. News and Path-survey pass their own.</param>
        /// <param name="path">Rolling path such as <c>logs/news-.log</c>.</param>
        /// <param name="restrictedToMinimumLevel">Minimum level for this sink.</param>
        /// <param name="fileSizeLimitBytes">Active-file size cap. <see langword="null"/> is unlimited.</param>
        /// <param name="buffered">Whether the active file buffers writes.</param>
        /// <param name="flushToDiskInterval">Periodic flush interval. <see langword="null"/> flushes on dispose.</param>
        /// <param name="rollingInterval">Roll period.</param>
        /// <param name="rollOnFileSizeLimit">Whether a full active file rolls to the next sequence.</param>
        /// <param name="retainedFileCountLimit">Daily files to keep, including the active file.</param>
        /// <param name="onCompletedFile">Called with the closed uncompressed path before compression.</param>
        /// <param name="compressor">Compression implementation. Defaults to <see cref="GzipLogFileCompressor"/>.</param>
        /// <param name="timeProvider">Clock used for roll checkpoints.</param>
        /// <returns>The logger configuration, so calls can be chained.</returns>
        public static LoggerConfiguration DailyGzipFile(
            this LoggerSinkConfiguration sinkConfiguration,
            ITextFormatter formatter,
            string path,
            LogEventLevel restrictedToMinimumLevel = LogEventLevel.Verbose,
            long? fileSizeLimitBytes = null,
            bool buffered = false,
            TimeSpan? flushToDiskInterval = null,
            RollingInterval rollingInterval = RollingInterval.Infinite,
            bool rollOnFileSizeLimit = false,
            int? retainedFileCountLimit = 31,
            Action<string>? onCompletedFile = null,
            ILogFileCompressor? compressor = null,
            TimeProvider? timeProvider = null)
        {
            ArgumentNullException.ThrowIfNull(sinkConfiguration);
            ArgumentNullException.ThrowIfNull(formatter);
            var rolling = new GzipRollingFileSink(
                path,
                formatter,
                fileSizeLimitBytes,
                retainedFileCountLimit,
                encoding: null,
                buffered,
                rollingInterval,
                rollOnFileSizeLimit,
                compressor,
                onCompletedFile,
                timeProvider);
            ILogEventSink sink = rolling;
            if (flushToDiskInterval is { } interval)
            {
#pragma warning disable CS0618 // Same one-second flush wrapper Serilog.Sinks.File 7.0.0 applies.
                sink = new PeriodicFlushToDiskSink(rolling, interval);
#pragma warning restore CS0618
            }

            return sinkConfiguration.Sink(sink, restrictedToMinimumLevel);
        }
    }
}
