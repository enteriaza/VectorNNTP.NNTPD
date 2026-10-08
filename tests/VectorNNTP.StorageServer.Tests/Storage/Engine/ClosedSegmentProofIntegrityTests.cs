using System.Buffers.Binary;
using System.IO.Hashing;
using System.Text;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Articles.Parsing;
using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Storage.Engine;
using VectorNNTP.StorageServer.Storage.Engine.Durable;
using VectorNNTP.StorageServer.Storage.Engine.FileSegments;

namespace VectorNNTP.StorageServer.Tests.Storage.Engine;

/// <summary>
/// Phase 35: optimized <see cref="FileSegmentStore.TryReadProvenAt"/> must reject every
/// corruption the Phase 34 reference path rejected, and must not allocate a second ArtData copy.
/// </summary>
public sealed class ClosedSegmentProofIntegrityTests
{
    [Fact]
    public void TryProveFramedRecord_agrees_with_TryDecode_on_valid_and_corrupt_corpus()
    {
        var record = Article("<p35-agree@seg.test>", bodyLength: 4096);
        var framed = Frame(record);

        AssertAgree(framed);

        AssertAgree(Corrupt(framed, static b => b[^1] ^= 0xFF));
        AssertAgree(Corrupt(framed, static b =>
        {
            b[4] ^= 0x01;
            RewriteCrc(b);
        }));
        AssertAgree(Corrupt(framed, static b =>
        {
            b[SegmentRecordCodec.FixedHeaderLength] ^= 0x01;
            RewriteCrc(b);
        }));
        AssertAgree(Corrupt(framed, static b =>
        {
            BinaryPrimitives.WriteUInt64LittleEndian(
                b.AsSpan(8 + ArticleId.Length, 8),
                BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(8 + ArticleId.Length, 8)) ^ 1UL);
            RewriteCrc(b);
        }));
        AssertAgree(Corrupt(framed, static b =>
        {
            BinaryPrimitives.WriteInt32LittleEndian(
                b.AsSpan(8 + ArticleId.Length + 8, 4),
                BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(8 + ArticleId.Length + 8, 4)) + 1);
            RewriteCrc(b);
        }));
        AssertAgree(Corrupt(framed, static b =>
        {
            var other = ArticleId.FromMessageId("<other@seg.test>"u8);
            other.CopyTo(b.AsSpan(8, ArticleId.Length));
            RewriteCrc(b);
        }));
        AssertAgree(framed.AsSpan(0, framed.Length - 1).ToArray());
        AssertAgree(framed.AsSpan(0, 3).ToArray());
        AssertAgree(Array.Empty<byte>());

        var oversizeLength = framed.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(oversizeLength.AsSpan(0, 4), (uint)(framed.Length + 64));
        AssertAgree(oversizeLength);

        var undersizeLength = framed.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(
            undersizeLength.AsSpan(0, 4),
            (uint)(SegmentRecordCodec.MinimumRecordLength - 1));
        AssertAgree(undersizeLength);
    }

    [Fact]
    public void Stream_proof_matches_reference_for_valid_and_corrupt_records()
    {
        var record = Article("<p35-stream@seg.test>", bodyLength: 8192);
        var framed = Frame(record);
        var segmentId = new SegmentId(1);

        AssertStreamAgree(framed, segmentId, sizeBytes: framed.Length, offset: 0, expectSuccess: true);

        AssertStreamAgree(
            Corrupt(framed, static b => b[^1] ^= 0xFF),
            segmentId,
            framed.Length,
            0,
            expectSuccess: false);
        AssertStreamAgree(
            Corrupt(framed, static b =>
            {
                b[SegmentRecordCodec.FixedHeaderLength + 10] ^= 0xFF;
                RewriteCrc(b);
            }),
            segmentId,
            framed.Length,
            0,
            expectSuccess: false);

        // Truncated file: declared length extends past sizeBytes.
        AssertStreamAgree(framed, segmentId, sizeBytes: framed.Length - 1, offset: 0, expectSuccess: false);

        // Invalid offset past EOF.
        AssertStreamAgree(framed, segmentId, sizeBytes: framed.Length, offset: framed.Length, expectSuccess: false);
        AssertStreamAgree(framed, segmentId, sizeBytes: framed.Length, offset: -1, expectSuccess: false);

        // Short physical read: length claims full record but stream ends early.
        var shortFile = framed.AsSpan(0, framed.Length / 2).ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(shortFile.AsSpan(0, 4), (uint)framed.Length);
        AssertStreamAgree(shortFile, segmentId, sizeBytes: shortFile.Length, offset: 0, expectSuccess: false);
    }

    [Fact]
    public void Large_article_proof_succeeds_via_TryProveFramedRecord()
    {
        const int body = 256 * 1024;
        var record = Article("<p35-large@seg.test>", bodyLength: body);
        var framed = Frame(record);
        Assert.True(framed.Length > 200_000);

        Assert.True(SegmentRecordCodec.TryProveFramedRecord(
            framed,
            out var length,
            out var artId,
            out var artHash,
            out var artSize,
            out var error));

        Assert.Equal(SegmentRecordCodec.DecodeError.None, error);
        Assert.Equal(framed.Length, length);
        Assert.Equal(record.ArtId, artId);
        Assert.Equal(record.ArtHash, artHash);
        Assert.Equal(record.ArtSize, artSize);

        // Reference decode still succeeds and returns an owned payload copy of ArtSize.
        Assert.True(SegmentRecordCodec.TryDecode(
            framed,
            out _,
            out _,
            out _,
            out _,
            out var payload,
            out _));
        Assert.Equal(record.ArtSize, payload.Length);
    }

    [Fact]
    public async Task Accounting_rejects_corrupt_closed_extent_and_does_not_mark_complete()
    {
        using var dir = TempDir.Create();
        var record = Article("<p35-acct-crc@seg.test>", bodyLength: 2048);
        StoredArticleLocation location;
        await using (var engine = FileArticleStorageEngine.Open(dir.Options))
        {
            Assert.Equal(
                ArticleAcceptOutcome.Accepted,
                (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
            await engine.DrainPendingAsync(CancellationToken.None);
            Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
            location = meta.Location;
            await engine.Segments.CloseActiveAsync(CancellationToken.None);
            Assert.False(engine.IsUnreferencedExtentAccountingComplete);

            var path = Path.Combine(
                dir.SegmentDir,
                SegmentFileNames.Format(location.SegmentId, SegmentFileKind.Closed));
            await using (var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
            {
                stream.Position = location.Offset + location.Length - 1;
                var value = stream.ReadByte();
                stream.Position = location.Offset + location.Length - 1;
                stream.WriteByte((byte)(value ^ 0xFF));
            }

            engine.CompleteUnreferencedExtentAccounting();
            Assert.False(engine.IsUnreferencedExtentAccountingComplete);
            Assert.True(engine.Catalogue.TryGet(location.SegmentId, out var info));
            Assert.False(info.ExtentAccountingComplete);
        }
    }

    [Fact]
    public async Task Accounting_completes_for_multi_article_closed_segment()
    {
        using var dir = TempDir.Create();
        const int count = 48;
        const int body = 4096;
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        for (var i = 0; i < count; i++)
        {
            Assert.Equal(
                ArticleAcceptOutcome.Accepted,
                (await engine.AcceptAsync(Article($"<p35-alloc-{i}@seg.test>", body), CancellationToken.None))
                    .Outcome);
        }

        await engine.DrainPendingAsync(CancellationToken.None);
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        Assert.False(engine.IsUnreferencedExtentAccountingComplete);
        engine.CompleteUnreferencedExtentAccounting();
        Assert.True(engine.IsUnreferencedExtentAccountingComplete);
        Assert.True(engine.TryRead(Article($"<p35-alloc-0@seg.test>", body).ArtId, out _));
    }

    private static void AssertAgree(byte[] bytes)
    {
        var decodeOk = SegmentRecordCodec.TryDecode(
            bytes,
            out var decodeLength,
            out var decodeId,
            out var decodeHash,
            out var decodeSize,
            out var decodePayload,
            out var decodeError);
        var proveOk = SegmentRecordCodec.TryProveFramedRecord(
            bytes,
            out var proveLength,
            out var proveId,
            out var proveHash,
            out var proveSize,
            out var proveError);

        Assert.Equal(decodeOk, proveOk);
        Assert.Equal(decodeError, proveError);
        Assert.Equal(decodeLength, proveLength);
        if (!decodeOk)
        {
            return;
        }

        Assert.Equal(decodeId, proveId);
        Assert.Equal(decodeHash, proveHash);
        Assert.Equal(decodeSize, proveSize);
        Assert.Equal(decodeSize, decodePayload.Length);
    }

    private static void AssertStreamAgree(
        byte[] fileBytes,
        SegmentId segmentId,
        long sizeBytes,
        long offset,
        bool expectSuccess)
    {
        var path = Path.Combine(Path.GetTempPath(), "vectornntp-p35-" + Guid.NewGuid().ToString("N") + ".seg");
        try
        {
            File.WriteAllBytes(path, fileBytes);
            using var optStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var refStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

            var optOk = FileSegmentStore.TryReadProvenAt(
                optStream,
                segmentId,
                sizeBytes,
                offset,
                out var optExtent,
                out var optPayload,
                out var optConsumed);
            var refOk = FileSegmentStore.TryReadProvenAtReference(
                refStream,
                segmentId,
                sizeBytes,
                offset,
                out var refExtent,
                out var refPayload,
                out var refConsumed);

            Assert.Equal(expectSuccess, optOk);
            Assert.Equal(refOk, optOk);
            if (!optOk)
            {
                return;
            }

            Assert.Equal(refExtent, optExtent);
            Assert.Equal(refConsumed, optConsumed);
            Assert.True(optPayload.Span.SequenceEqual(refPayload.Span));
        }
        finally
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
            }
        }
    }

    private static byte[] Frame(ArticleRecord record)
        => SegmentRecordCodec.Encode(record.ArtId, record.ArtHash, record.ArtData.Span);

    private static byte[] Corrupt(byte[] framed, Action<byte[]> mutate)
    {
        var copy = framed.ToArray();
        mutate(copy);
        return copy;
    }

    private static void RewriteCrc(byte[] record)
    {
        var crc = Crc32.HashToUInt32(record.AsSpan(0, record.Length - 4));
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(record.Length - 4), crc);
    }

    private static ArticleRecord Article(string messageId, int bodyLength = 256)
    {
        var builder = new StringBuilder();
        _ = builder.Append("Path: peer.example\r\n");
        _ = builder.Append("Date: Fri, 23 Aug 2024 07:30:10 +0000\r\n");
        _ = builder.Append("Message-ID: ").Append(messageId).Append("\r\n");
        _ = builder.Append("Newsgroups: alt.test\r\n");
        _ = builder.Append("From: user@example.test\r\n");
        _ = builder.Append("Subject: phase35\r\n\r\n");
        var remaining = bodyLength;
        while (remaining > 0)
        {
            var take = Math.Min(64, remaining);
            _ = builder.Append(new string('x', take)).Append("\r\n");
            remaining -= take;
        }

        var created = ArticleRecordFactory.TryCreate(
            new NntpArticleParser("cache01.usenet.ninja"),
            Encoding.ASCII.GetBytes(builder.ToString()));
        Assert.True(created.IsAccepted, created.ParseFailure.ToString());
        return created.Record;
    }

    private sealed class TempDir : IDisposable
    {
        private TempDir(string root, string segmentDir, ArticleStorageRuntimeOptions options)
        {
            Root = root;
            SegmentDir = segmentDir;
            Options = options;
        }

        public string SegmentDir { get; }

        public ArticleStorageRuntimeOptions Options { get; }

        public static TempDir Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-phase35-" + Guid.NewGuid().ToString("N"));
            var control = Path.Combine(root, "control");
            var cache = Path.Combine(root, "cache");
            Directory.CreateDirectory(control);
            Directory.CreateDirectory(cache);
            return new TempDir(
                root,
                cache,
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

        private string Root { get; }
    }
}
