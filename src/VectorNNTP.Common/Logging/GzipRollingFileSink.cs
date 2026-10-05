using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Serilog;
using Serilog.Core;
using Serilog.Debugging;
using Serilog.Events;
using Serilog.Formatting;
using Serilog.Sinks.File;

namespace VectorNNTP.Common.Logging
{
    /// <summary>
    /// Daily (or other interval) rolling file sink that gzip-compresses a file when it becomes inactive
    /// and applies <c>retainedFileCountLimit</c> to both <c>.log</c> and <c>.log.gz</c> days.
    /// </summary>
    /// <remarks>
    /// When the interval rolls, this sink closes the active file, compresses that path, then opens the next file.
    /// The active path is never compressed or deleted. One lock covers roll, compression, and retention.
    /// </remarks>
    public sealed class GzipRollingFileSink : ILogEventSink, IFlushableFileSink, IDisposable, ISetLoggingFailureListener
    {
        private readonly object _sync = new();
        private readonly string _directory;
        private readonly string _filenamePrefix;
        private readonly string _filenameSuffix;
        private readonly Regex _filenameMatcher;
        private readonly RollingInterval _interval;
        private readonly string _periodFormat;
        private readonly ITextFormatter _textFormatter;
        private readonly long? _fileSizeLimitBytes;
        private readonly int? _retainedFileCountLimit;
        private readonly bool _buffered;
        private readonly bool _rollOnFileSizeLimit;
        private readonly Encoding? _encoding;
        private readonly ILogFileCompressor _compressor;
        private readonly Action<string>? _onCompletedFile;
        private readonly TimeProvider _time;
        private ILoggingFailureListener? _failureListener;
        private ActiveLogFileWriter? _current;
        private string? _currentPath;
        private DateTime? _nextCheckpoint;
        private int? _currentSequence;
        private bool _disposed;

        /// <summary>Initializes a rolling gzip file sink.</summary>
        /// <param name="path">Serilog rolling path such as <c>logs/app-.log</c>.</param>
        /// <param name="textFormatter">Formatter for the active file. Not used to rewrite archived files.</param>
        /// <param name="fileSizeLimitBytes">Active-file size cap. <see langword="null"/> is unlimited.</param>
        /// <param name="retainedFileCountLimit">
        /// Maximum daily files including the active file. <see langword="null"/> keeps every day.
        /// A <c>.log</c> and <c>.log.gz</c> for the same period count as one file.
        /// </param>
        /// <param name="encoding">File encoding. <see langword="null"/> uses the File sink default.</param>
        /// <param name="buffered">Whether the active file buffers writes.</param>
        /// <param name="rollingInterval">How the path period is chosen.</param>
        /// <param name="rollOnFileSizeLimit">Whether a full active file rolls to the next sequence.</param>
        /// <param name="compressor">Compression used when a file becomes inactive. Defaults to gzip.</param>
        /// <param name="onCompletedFile">
        /// Invoked with the uncompressed path after it is closed and before compression.
        /// Exceptions are logged and do not skip compression.
        /// </param>
        /// <param name="timeProvider">Clock for roll checkpoints. Defaults to <see cref="TimeProvider.System"/>.</param>
        public GzipRollingFileSink(
            string path,
            ITextFormatter textFormatter,
            long? fileSizeLimitBytes,
            int? retainedFileCountLimit,
            Encoding? encoding,
            bool buffered,
            RollingInterval rollingInterval,
            bool rollOnFileSizeLimit,
            ILogFileCompressor? compressor = null,
            Action<string>? onCompletedFile = null,
            TimeProvider? timeProvider = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            ArgumentNullException.ThrowIfNull(textFormatter);
            if (fileSizeLimitBytes is < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(fileSizeLimitBytes));
            }

            if (retainedFileCountLimit is < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(retainedFileCountLimit));
            }

            _textFormatter = textFormatter;
            _fileSizeLimitBytes = fileSizeLimitBytes;
            _retainedFileCountLimit = retainedFileCountLimit;
            _encoding = encoding;
            _buffered = buffered;
            _interval = rollingInterval;
            _rollOnFileSizeLimit = rollOnFileSizeLimit;
            _compressor = compressor ?? new GzipLogFileCompressor();
            _onCompletedFile = onCompletedFile;
            _time = timeProvider ?? TimeProvider.System;
            _periodFormat = PeriodFormat(rollingInterval);

            var pathDirectory = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(pathDirectory))
            {
                pathDirectory = Directory.GetCurrentDirectory();
            }

            _directory = Path.GetFullPath(pathDirectory);
            _filenamePrefix = Path.GetFileNameWithoutExtension(path);
            _filenameSuffix = Path.GetExtension(path);
            _filenameMatcher = new Regex(
                "^" +
                Regex.Escape(_filenamePrefix) +
                "(?<period>\\d{" + _periodFormat.Length + "})" +
                "(?<sequence>_[0-9]{3,})?" +
                Regex.Escape(_filenameSuffix) +
                "(\\." + "gz" + ")?$",
                RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        }

        /// <inheritdoc />
        public void Emit(LogEvent logEvent)
        {
            ArgumentNullException.ThrowIfNull(logEvent);
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                var now = _time.GetLocalNow().DateTime;
                Align(now, nextSequence: false);
                while (_current is { } file
                    && !file.TryEmit(logEvent)
                    && _rollOnFileSizeLimit)
                {
                    Align(now, nextSequence: true);
                }
            }
        }

        /// <inheritdoc />
        public void FlushToDisk()
        {
            lock (_sync)
            {
                _current?.FlushToDisk();
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed)
                {
                    return;
                }

                _current?.Dispose();
                _current = null;
                _currentPath = null;
                _disposed = true;
            }
        }

        /// <inheritdoc />
        public void SetFailureListener(ILoggingFailureListener failureListener)
        {
            ArgumentNullException.ThrowIfNull(failureListener);
            lock (_sync)
            {
                _failureListener = failureListener;
            }
        }

        private void Align(DateTime now, bool nextSequence)
        {
            if (_current is null || nextSequence || (_nextCheckpoint.HasValue && now >= _nextCheckpoint.Value))
            {
                var sequence = _currentSequence;
                if (nextSequence)
                {
                    sequence = sequence is null ? 1 : sequence.Value + 1;
                }
                else if (_current is not null)
                {
                    sequence = null;
                }

                CloseCurrent();
                Open(now, sequence);
            }
        }

        private void CloseCurrent()
        {
            _current?.Dispose();
            _current = null;
            _currentPath = null;
            _nextCheckpoint = null;
        }

        private void Open(DateTime now, int? sequence)
        {
            Directory.CreateDirectory(_directory);
            var checkpoint = Checkpoint(now);
            _nextCheckpoint = NextCheckpoint(now);
            _currentSequence = sequence;
            _currentPath = LogPath(checkpoint, sequence);
            _current = new ActiveLogFileWriter(_currentPath, _textFormatter, _fileSizeLimitBytes, _encoding, _buffered);
            ArchiveInactive();
            ApplyRetention();
        }

        private void ArchiveInactive()
        {
            if (Directory.Exists(_directory))
            {
                foreach (var partial in Directory.GetFiles(_directory, _filenamePrefix + "*.gz.partial"))
                {
                    TryDelete(partial);
                }
            }

            foreach (var path in MatchingFiles(".log"))
            {
                if (IsCurrent(path) || path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var gzip = path + ".gz";
                if (File.Exists(gzip))
                {
                    TryDelete(path);
                    continue;
                }

                try
                {
                    _onCompletedFile?.Invoke(path);
                }
                catch (Exception ex)
                {
                    SelfLog.WriteLine("Completed-log handler failed for {0}: {1}", path, ex);
                }

                var compressed = false;
                try
                {
                    compressed = _compressor.TryCompressAndReplace(path);
                }
                catch (Exception ex)
                {
                    SelfLog.WriteLine("Log compression threw for {0}: {1}", path, ex);
                }

                if (!compressed)
                {
                    Report($"log compression failed for {path}; the uncompressed file was kept");
                }
            }
        }

        private void ApplyRetention()
        {
            if (_retainedFileCountLimit is null)
            {
                return;
            }

            var slots = new Dictionary<string, Slot>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in MatchingFiles(".log").Concat(MatchingFiles(".log.gz")))
            {
                var name = Path.GetFileName(path);
                var match = _filenameMatcher.Match(name);
                if (!match.Success)
                {
                    continue;
                }

                var period = match.Groups["period"].Value;
                var sequenceText = match.Groups["sequence"].Value;
                var sequence = sequenceText.Length == 0
                    ? -1
                    : int.Parse(sequenceText[1..], CultureInfo.InvariantCulture);
                var key = period + "\n" + sequence.ToString(CultureInfo.InvariantCulture);
                if (!slots.TryGetValue(key, out var slot))
                {
                    slot = new Slot(period, sequence);
                    slots.Add(key, slot);
                }

                if (!slot.Paths.Contains(path, StringComparer.OrdinalIgnoreCase))
                {
                    slot.Paths.Add(path);
                }
            }

            var ranked = slots.Values
                .OrderByDescending(static slot => slot.Period, StringComparer.Ordinal)
                .ThenByDescending(static slot => slot.Sequence)
                .ToList();
            var currentIndex = ranked.FindIndex(slot => slot.Paths.Exists(IsCurrent));
            if (currentIndex > 0)
            {
                var current = ranked[currentIndex];
                ranked.RemoveAt(currentIndex);
                ranked.Insert(0, current);
            }

            for (var index = _retainedFileCountLimit.Value; index < ranked.Count; index++)
            {
                foreach (var path in ranked[index].Paths)
                {
                    if (!IsCurrent(path))
                    {
                        TryDelete(path);
                    }
                }
            }
        }

        private IEnumerable<string> MatchingFiles(string suffix)
        {
            if (!Directory.Exists(_directory))
            {
                return [];
            }

            return Directory.GetFiles(_directory, _filenamePrefix + "*" + suffix)
                .Where(path => _filenameMatcher.IsMatch(Path.GetFileName(path)));
        }

        private string LogPath(DateTime? checkpoint, int? sequence)
        {
            var token = checkpoint?.ToString(_periodFormat, CultureInfo.InvariantCulture) ?? string.Empty;
            if (sequence is not null)
            {
                token += "_" + sequence.Value.ToString("000", CultureInfo.InvariantCulture);
            }

            return Path.Combine(_directory, _filenamePrefix + token + _filenameSuffix);
        }

        private bool IsCurrent(string path) =>
            _currentPath is not null && string.Equals(path, _currentPath, StringComparison.OrdinalIgnoreCase);

        private DateTime? Checkpoint(DateTime instant) => _interval switch
        {
            RollingInterval.Infinite => null,
            RollingInterval.Year => new DateTime(instant.Year, 1, 1, 0, 0, 0, instant.Kind),
            RollingInterval.Month => new DateTime(instant.Year, instant.Month, 1, 0, 0, 0, instant.Kind),
            RollingInterval.Day => instant.Date,
            RollingInterval.Hour => new DateTime(instant.Year, instant.Month, instant.Day, instant.Hour, 0, 0, instant.Kind),
            RollingInterval.Minute => new DateTime(
                instant.Year,
                instant.Month,
                instant.Day,
                instant.Hour,
                instant.Minute,
                0,
                instant.Kind),
            _ => throw new InvalidOperationException($"Unsupported rolling interval {_interval}."),
        };

        private DateTime? NextCheckpoint(DateTime instant) => _interval switch
        {
            RollingInterval.Infinite => null,
            RollingInterval.Year => new DateTime(instant.Year + 1, 1, 1, 0, 0, 0, instant.Kind),
            RollingInterval.Month => instant.Month == 12
                ? new DateTime(instant.Year + 1, 1, 1, 0, 0, 0, instant.Kind)
                : new DateTime(instant.Year, instant.Month + 1, 1, 0, 0, 0, instant.Kind),
            RollingInterval.Day => instant.Date.AddDays(1),
            RollingInterval.Hour => new DateTime(instant.Year, instant.Month, instant.Day, instant.Hour, 0, 0, instant.Kind).AddHours(1),
            RollingInterval.Minute => new DateTime(
                instant.Year,
                instant.Month,
                instant.Day,
                instant.Hour,
                instant.Minute,
                0,
                instant.Kind).AddMinutes(1),
            _ => throw new InvalidOperationException($"Unsupported rolling interval {_interval}."),
        };

        private void Report(string message)
        {
            SelfLog.WriteLine(message);
            _failureListener?.OnLoggingFailed(
                this,
                LoggingFailureKind.Permanent,
                message,
                events: null!,
                exception: null!);
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                SelfLog.WriteLine("Could not delete log file {0}: {1}", path, ex);
            }
        }

        private static string PeriodFormat(RollingInterval interval) => interval switch
        {
            RollingInterval.Infinite => string.Empty,
            RollingInterval.Year => "yyyy",
            RollingInterval.Month => "yyyyMM",
            RollingInterval.Day => "yyyyMMdd",
            RollingInterval.Hour => "yyyyMMddHH",
            RollingInterval.Minute => "yyyyMMddHHmm",
            _ => throw new ArgumentOutOfRangeException(nameof(interval)),
        };

        private sealed class Slot(string period, int sequence)
        {
            public string Period { get; } = period;

            public int Sequence { get; } = sequence;

            public List<string> Paths { get; } = [];
        }
    }
}
