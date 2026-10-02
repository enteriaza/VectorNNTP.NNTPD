using System.IO.Hashing;
using VectorNNTP.Common.Articles;
using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Storage.Engine;
using VectorNNTP.StorageServer.Storage.Engine.FileSegments;

namespace VectorNNTP.StorageServer.Tests.Storage.Engine;

/// <summary>
/// Active tail repair walks a long file offset and one bounded record at a time.
/// </summary>
public sealed class ActiveSegmentRepairStreamingTests
{
    [Fact]
    public void ActiveLongerThanIntMaxValue_ReplaysValidRecords()
    {
        using var dir = TempSegmentDir.Create();
        var path = PlantActive(dir);
        SyntheticActiveSegmentStream? repair = null;
        using var store = Open(dir, path, SyntheticRepairMode.Valid, stream => repair = stream);

        Assert.NotNull(repair);
        Assert.True(repair.InitialLength > int.MaxValue);
        Assert.True(store.TryGetSegmentInfo(new SegmentId(1), out var info));
        Assert.Equal(SegmentState.Active, info.State);
        Assert.Equal(repair.InitialLength, info.SizeBytes);
        Assert.True(store.TryGetActiveValidatedPrefix(out var size, out var count, out var lastOffset));
        Assert.Equal(repair.InitialLength, size);
        Assert.Equal(checked((int)repair.RecordCount), count);
        Assert.True(lastOffset > int.MaxValue);
        Assert.Equal((repair.RecordCount - 1) * repair.RecordLength, lastOffset);
        Assert.Equal(repair.InitialLength, store.DiscoveryPayloadBytesRead);
        Assert.Equal(repair.RecordLength, store.ActiveRepairMaxRecordBytes);
        Assert.True(store.ActiveRepairMaxRecordBytes <= SegmentRecordCodec.MaxRecordLength);
        Assert.True(repair.MaxRequestedRead <= SegmentRecordCodec.MaxRecordLength);
        Assert.Null(repair.TruncatedTo);
        Assert.True(new FileInfo(path).Length < 4096);
    }

    [Fact]
    public void ActiveLongerThanIntMaxValue_IncompleteTail_TruncatesLogicalEnd()
    {
        using var dir = TempSegmentDir.Create();
        var path = PlantActive(dir);
        SyntheticActiveSegmentStream? repair = null;
        using var store = Open(dir, path, SyntheticRepairMode.TornTail, stream => repair = stream);

        Assert.NotNull(repair);
        Assert.True(repair.RecordBytesEnd > int.MaxValue);
        Assert.Equal(repair.RecordBytesEnd, repair.TruncatedTo);
        Assert.True(store.TryGetSegmentInfo(new SegmentId(1), out var info));
        Assert.Equal(repair.RecordBytesEnd, info.SizeBytes);
        Assert.True(store.TryGetActiveValidatedPrefix(out var size, out var count, out var lastOffset));
        Assert.Equal(repair.RecordBytesEnd, size);
        Assert.Equal(checked((int)repair.RecordCount), count);
        Assert.True(lastOffset > int.MaxValue);
        Assert.Equal(repair.InitialLength, store.DiscoveryPayloadBytesRead);
        Assert.Equal(repair.InitialLength, repair.BytesServed);
        Assert.True(repair.MaxRequestedRead <= SegmentRecordCodec.MaxRecordLength);
        Assert.True(new FileInfo(path).Length < 4096);
    }

    [Fact]
    public void ActiveLongerThanIntMaxValue_BadCrcBeforeEof_FailsClosed()
    {
        using var dir = TempSegmentDir.Create();
        var path = PlantActive(dir);
        SyntheticActiveSegmentStream? repair = null;
        var ex = Assert.Throws<SegmentStoreCorruptException>(() =>
            Open(dir, path, SyntheticRepairMode.BadCrcBeforeEof, stream => repair = stream));

        Assert.NotNull(repair);
        Assert.True(repair.InitialLength > int.MaxValue);
        Assert.Contains("CorruptChecksum", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("exceeds supported size", ex.Message, StringComparison.Ordinal);
        Assert.Equal(repair.RecordLength, repair.BytesServed);
        Assert.True(repair.MaxRequestedRead <= SegmentRecordCodec.MaxRecordLength);
        Assert.Null(repair.TruncatedTo);
        Assert.Equal(0, new FileInfo(path).Length);
    }

    [Fact]
    public void ActiveLongerThanIntMaxValue_CorruptLength_FailsClosed()
    {
        using var dir = TempSegmentDir.Create();
        var path = PlantActive(dir);
        SyntheticActiveSegmentStream? repair = null;
        var ex = Assert.Throws<SegmentStoreCorruptException>(() =>
            Open(dir, path, SyntheticRepairMode.CorruptLength, stream => repair = stream));

        Assert.NotNull(repair);
        Assert.True(repair.InitialLength > int.MaxValue);
        Assert.Contains("CorruptLength", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("exceeds supported size", ex.Message, StringComparison.Ordinal);
        Assert.Equal(4, repair.BytesServed);
        Assert.True(repair.MaxRequestedRead <= 4);
        Assert.Null(repair.TruncatedTo);
        Assert.Equal(0, new FileInfo(path).Length);
    }

    private static FileSegmentStore Open(
        TempSegmentDir dir,
        string path,
        SyntheticRepairMode mode,
        Action<SyntheticActiveSegmentStream> observed)
    {
        return FileSegmentStore.Open(dir.Options, store =>
        {
            store.TestActiveRepairStreamFactory = opened =>
            {
                Assert.Equal(path, opened);
                var stream = new SyntheticActiveSegmentStream(opened, mode);
                observed(stream);
                return stream;
            };
        });
    }

    private static string PlantActive(TempSegmentDir dir)
    {
        var path = Path.Combine(
            dir.SegmentDir,
            SegmentFileNames.Format(new SegmentId(1), SegmentFileKind.Active));
        File.WriteAllBytes(path, []);
        return path;
    }

    private static byte[] CreateMaxRecord()
    {
        var messageId = "<stream-active@example.test>"u8;
        var artData = new byte[ArticleResourceLimits.MaxArticleBytes];
        "Message-ID: "u8.CopyTo(artData);
        messageId.CopyTo(artData.AsSpan("Message-ID: "u8.Length));
        artData["Message-ID: "u8.Length + messageId.Length] = (byte)'\r';
        artData["Message-ID: "u8.Length + messageId.Length + 1] = (byte)'\n';
        artData["Message-ID: "u8.Length + messageId.Length + 2] = (byte)'\r';
        artData["Message-ID: "u8.Length + messageId.Length + 3] = (byte)'\n';
        var artId = ArticleId.FromMessageId(messageId);
        var artHash = XxHash3.HashToUInt64(artData);
        var encoded = SegmentRecordCodec.Encode(artId, artHash, artData);
        Assert.Equal(SegmentRecordCodec.MaxRecordLength, encoded.Length);
        Assert.True(SegmentRecordCodec.TryDecode(encoded, out _, out _, out _, out _, out _, out _));
        return encoded;
    }

    private enum SyntheticRepairMode
    {
        Valid = 0,
        TornTail = 1,
        BadCrcBeforeEof = 2,
        CorruptLength = 3,
    }

    /// <summary>
    /// Presents an active segment longer than <see cref="int.MaxValue"/> as repeated records.
    /// The backing file stays empty. Record bytes are produced while they are read.
    /// </summary>
    private sealed class SyntheticActiveSegmentStream : FileStream
    {
        private readonly SyntheticRepairMode _mode;
        private readonly byte[] _template;
        private readonly byte[] _badCrc;
        private readonly byte[] _tail = [0x11, 0x22, 0x33];
        private long _position;

        public SyntheticActiveSegmentStream(string path, SyntheticRepairMode mode)
            : base(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite, bufferSize: 4096, FileOptions.None)
        {
            _mode = mode;
            _template = CreateMaxRecord();
            _badCrc = _template.ToArray();
            _badCrc[^1] ^= 0xFF;
            RecordLength = _template.Length;
            RecordCount = ((long)int.MaxValue / RecordLength) + 2;
            RecordBytesEnd = RecordCount * RecordLength;
            InitialLength = mode switch
            {
                SyntheticRepairMode.Valid => RecordBytesEnd,
                SyntheticRepairMode.TornTail => RecordBytesEnd + _tail.Length,
                SyntheticRepairMode.BadCrcBeforeEof => (long)int.MaxValue + RecordLength,
                SyntheticRepairMode.CorruptLength => (long)int.MaxValue + 8,
                _ => throw new ArgumentOutOfRangeException(nameof(mode)),
            };
            LogicalLength = InitialLength;
        }

        public int RecordLength { get; }

        public long RecordCount { get; }

        public long RecordBytesEnd { get; }

        public long InitialLength { get; }

        public long LogicalLength { get; private set; }

        public long? TruncatedTo { get; private set; }

        public int MaxRequestedRead { get; private set; }

        public long BytesServed { get; private set; }

        public override long Length => LogicalLength;

        public override long Position
        {
            get => _position;
            set => _position = value;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            var next = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                SeekOrigin.End => LogicalLength + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin)),
            };
            if (next < 0)
            {
                throw new IOException("Segment position is before the start of the stream.");
            }

            _position = next;
            return _position;
        }

        public override void SetLength(long value)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            LogicalLength = value;
            TruncatedTo = value;
        }

        public override int Read(Span<byte> buffer)
        {
            if (buffer.Length > SegmentRecordCodec.MaxRecordLength)
            {
                throw new InvalidOperationException($"Repair requested {buffer.Length} bytes in one read.");
            }

            if (buffer.Length > MaxRequestedRead)
            {
                MaxRequestedRead = buffer.Length;
            }

            if (buffer.IsEmpty || _position >= LogicalLength)
            {
                return 0;
            }

            var copied = 0;
            while (copied < buffer.Length && _position < LogicalLength)
            {
                var count = CopyAt(_position, buffer.Slice(copied));
                _position += count;
                copied += count;
                BytesServed += count;
            }

            return copied;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            return Read(buffer.AsSpan(offset, count));
        }

        private int CopyAt(long position, Span<byte> destination)
        {
            if (_mode == SyntheticRepairMode.CorruptLength)
            {
                if (position >= 4)
                {
                    throw new InvalidOperationException("Repair read past the corrupt length prefix.");
                }

                var prefix = (int)Math.Min(destination.Length, 4 - position);
                destination[..prefix].Fill(0xFF);
                return prefix;
            }

            if (_mode == SyntheticRepairMode.BadCrcBeforeEof && position >= RecordLength)
            {
                throw new InvalidOperationException("Repair read past the corrupt record.");
            }

            if (position >= RecordBytesEnd)
            {
                var tailOffset = (int)(position - RecordBytesEnd);
                var tailCount = Math.Min(destination.Length, _tail.Length - tailOffset);
                _tail.AsSpan(tailOffset, tailCount).CopyTo(destination[..tailCount]);
                return tailCount;
            }

            var source = _mode == SyntheticRepairMode.BadCrcBeforeEof ? _badCrc : _template;
            var within = (int)(position % RecordLength);
            var count = (int)Math.Min(destination.Length, (long)RecordLength - within);
            count = (int)Math.Min(count, RecordBytesEnd - position);
            source.AsSpan(within, count).CopyTo(destination[..count]);
            return count;
        }
    }

    private sealed class TempSegmentDir : IDisposable
    {
        private TempSegmentDir(string root, ArticleStorageRuntimeOptions options)
        {
            Root = root;
            Options = options;
        }

        public string SegmentDir => Options.SegmentDir;

        public ArticleStorageRuntimeOptions Options { get; }

        public static TempSegmentDir Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-segstream-" + Guid.NewGuid().ToString("N"));
            var segment = Path.Combine(root, "cache");
            Directory.CreateDirectory(segment);
            var options = new ArticleStorageRuntimeOptions(
                ControlDir: Path.Combine(root, "control"),
                SegmentDir: segment,
                JournalSoftLimitBytes: ArticleStorageOptions.DefaultJournalSoftLimitBytes,
                JournalHardLimitBytes: ArticleStorageOptions.DefaultJournalHardLimitBytes,
                SegmentTargetSizeBytes: ArticleStorageOptions.DefaultSegmentTargetSizeBytes);
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
        }

        private string Root { get; }
    }
}
