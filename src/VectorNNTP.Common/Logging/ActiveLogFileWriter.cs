using System.Text;
using Serilog.Events;
using Serilog.Formatting;

namespace VectorNNTP.Common.Logging
{
    /// <summary>
    /// Appends formatted events to one active log file.
    /// </summary>
    /// <remarks>
    /// This writer appends formatted events to one active file and applies the file-size check.
    /// <see cref="GzipRollingFileSink"/> owns rolling, compression, and retention.
    /// Callers serialize access; this type has no lock of its own.
    /// </remarks>
    internal sealed class ActiveLogFileWriter : IDisposable
    {
        private readonly TextWriter _output;
        private readonly FileStream _file;
        private readonly CountingStream? _counted;
        private readonly ITextFormatter _formatter;
        private readonly long? _fileSizeLimitBytes;
        private readonly bool _buffered;

        /// <summary>Opens <paramref name="path"/> for append.</summary>
        /// <param name="path">Active log path. Created if it does not exist.</param>
        /// <param name="formatter">Formatter for this file only. Archived files are not rewritten.</param>
        /// <param name="fileSizeLimitBytes">Byte cap. <see langword="null"/> does not overflow.</param>
        /// <param name="encoding">Text encoding. <see langword="null"/> is UTF-8 without a BOM.</param>
        /// <param name="buffered">
        /// When <see langword="false"/>, each accepted event is flushed to the file stream
        /// so the next size check sees it. When <see langword="true"/>, bytes stay in the writer
        /// until <see cref="FlushToDisk"/> or dispose.
        /// </param>
        public ActiveLogFileWriter(
            string path,
            ITextFormatter formatter,
            long? fileSizeLimitBytes,
            Encoding? encoding,
            bool buffered)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            _formatter = formatter ?? throw new ArgumentNullException(nameof(formatter));
            if (fileSizeLimitBytes is < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(fileSizeLimitBytes));
            }

            _fileSizeLimitBytes = fileSizeLimitBytes;
            _buffered = buffered;
            _file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
            _file.Seek(0, SeekOrigin.End);
            Stream output = _file;
            if (fileSizeLimitBytes is not null)
            {
                _counted = new CountingStream(_file);
                output = _counted;
            }

            _output = new StreamWriter(output, encoding ?? new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        /// <summary>
        /// Writes <paramref name="logEvent"/> when the file is under the size cap.
        /// An event that begins under the cap is written in full.
        /// </summary>
        /// <returns><see langword="false"/> when the file was already at the cap and nothing was written.</returns>
        public bool TryEmit(LogEvent logEvent)
        {
            ArgumentNullException.ThrowIfNull(logEvent);
            if (_fileSizeLimitBytes is { } limit && _counted!.CountedLength >= limit)
            {
                return false;
            }

            _formatter.Format(logEvent, _output);
            if (!_buffered)
            {
                _output.Flush();
            }

            return true;
        }

        /// <summary>Flushes the writer and asks the operating system to persist the file.</summary>
        public void FlushToDisk()
        {
            _output.Flush();
            _file.Flush(flushToDisk: true);
        }

        /// <inheritdoc />
        public void Dispose() => _output.Dispose();

        /// <summary>Counts bytes written through to the file, including bytes already present at open.</summary>
        private sealed class CountingStream : Stream
        {
            private readonly Stream _stream;

            public CountingStream(Stream stream)
            {
                _stream = stream;
                CountedLength = stream.Length;
            }

            public long CountedLength { get; private set; }

            public override bool CanRead => false;

            public override bool CanSeek => _stream.CanSeek;

            public override bool CanWrite => true;

            public override long Length => _stream.Length;

            public override long Position
            {
                get => _stream.Position;
                set => throw new NotSupportedException();
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                _stream.Write(buffer, offset, count);
                CountedLength += count;
            }

            public override void Write(ReadOnlySpan<byte> buffer)
            {
                _stream.Write(buffer);
                CountedLength += buffer.Length;
            }

            public override void Flush() => _stream.Flush();

            public override long Seek(long offset, SeekOrigin origin) =>
                throw new NotSupportedException();

            public override void SetLength(long value) => _stream.SetLength(value);

            public override int Read(byte[] buffer, int offset, int count) =>
                throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    _stream.Dispose();
                }

                base.Dispose(disposing);
            }
        }
    }
}
