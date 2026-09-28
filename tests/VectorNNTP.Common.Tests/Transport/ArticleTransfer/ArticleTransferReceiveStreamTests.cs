using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Transport.ArticleTransfer;

namespace VectorNNTP.Common.Tests.Transport.ArticleTransfer;

public sealed class ArticleTransferReceiveStreamTests
{
    [Fact]
    public void OpenMetaDataFinEnd_ValidArticle_ProducesOneConsumableRecord()
    {
        var (record, selected, artData) = VatpTestArticles.CreateCanonical();
        var meta = ArticleCanonicalTransferMeta.FromRecord(record, selected);
        var stream = new ArticleTransferReceiveStream(1, Guid.NewGuid(), record.ArtId);

        Assert.True(stream.TryAcceptMeta(VatpMetaCodec.Encode(in meta)).Success);
        Assert.Equal(ArticleTransferPhase.ReceivingData, stream.Phase);

        Assert.True(stream.TryAcceptData(artData, fin: true).Success);
        Assert.Equal(ArticleTransferPhase.AwaitingEnd, stream.Phase);
        Assert.False(stream.HasConsumableRecord);

        Assert.True(stream.TryAcceptEnd().Success);
        Assert.Equal(ArticleTransferPhase.Completed, stream.Phase);
        Assert.True(stream.HasConsumableRecord);
        Assert.Equal(record.ArtId, stream.ConsumableRecord.ArtId);
        Assert.True(stream.TryTakeRecord(out var taken));
        Assert.Equal(ArticleParseStatus.CanonicalV1, taken.ParseStatus);
        Assert.False(stream.TryTakeRecord(out _));
    }

    [Fact]
    public void FinAlone_DoesNotProduceConsumableRecord()
    {
        var (record, selected, artData) = VatpTestArticles.CreateCanonical();
        var stream = new ArticleTransferReceiveStream(11, Guid.NewGuid(), record.ArtId);
        Assert.True(stream.TryAcceptMeta(VatpMetaCodec.Encode(ArticleCanonicalTransferMeta.FromRecord(record, selected))).Success);
        Assert.True(stream.TryAcceptData(artData, fin: true).Success);
        Assert.Equal(ArticleTransferPhase.AwaitingEnd, stream.Phase);
        Assert.False(stream.HasConsumableRecord);
    }

    [Fact]
    public void EndWithoutFin_FailsIncomplete()
    {
        var (record, selected, artData) = VatpTestArticles.CreateCanonical();
        var stream = new ArticleTransferReceiveStream(2, Guid.NewGuid(), record.ArtId);
        Assert.True(stream.TryAcceptMeta(VatpMetaCodec.Encode(ArticleCanonicalTransferMeta.FromRecord(record, selected))).Success);
        Assert.True(stream.TryAcceptData(artData, fin: false).Success);
        Assert.Equal(ArticleTransferPhase.ReceivingData, stream.Phase);
        Assert.False(stream.HasConsumableRecord);
        var end = stream.TryAcceptEnd();
        Assert.False(end.Success);
        Assert.Equal(VatpErrorCode.IncompleteTransfer, end.Error);
        Assert.Equal(ArticleTransferPhase.Failed, stream.Phase);
    }

    [Fact]
    public void ExactBytesThenEmptyFinThenEnd_Completes()
    {
        var (record, selected, artData) = VatpTestArticles.CreateCanonical();
        var stream = new ArticleTransferReceiveStream(12, Guid.NewGuid(), record.ArtId);
        Assert.True(stream.TryAcceptMeta(VatpMetaCodec.Encode(ArticleCanonicalTransferMeta.FromRecord(record, selected))).Success);
        Assert.True(stream.TryAcceptData(artData, fin: false).Success);
        Assert.Equal(ArticleTransferPhase.ReceivingData, stream.Phase);
        Assert.True(stream.TryAcceptData(ReadOnlySpan<byte>.Empty, fin: true).Success);
        Assert.Equal(ArticleTransferPhase.AwaitingEnd, stream.Phase);
        Assert.True(stream.TryAcceptEnd().Success);
        Assert.True(stream.HasConsumableRecord);
    }

    [Fact]
    public void EndBeforeFinalData_FailsIncomplete()
    {
        var (record, selected, artData) = VatpTestArticles.CreateCanonical();
        var stream = new ArticleTransferReceiveStream(13, Guid.NewGuid(), record.ArtId);
        Assert.True(stream.TryAcceptMeta(VatpMetaCodec.Encode(ArticleCanonicalTransferMeta.FromRecord(record, selected))).Success);
        Assert.True(stream.TryAcceptData(artData.AsSpan(0, Math.Max(1, artData.Length / 2)), fin: false).Success);
        var end = stream.TryAcceptEnd();
        Assert.False(end.Success);
        Assert.Equal(VatpErrorCode.IncompleteTransfer, end.Error);
    }

    [Fact]
    public void DataAfterFin_Fails()
    {
        var (record, selected, artData) = VatpTestArticles.CreateCanonical();
        var stream = new ArticleTransferReceiveStream(14, Guid.NewGuid(), record.ArtId);
        Assert.True(stream.TryAcceptMeta(VatpMetaCodec.Encode(ArticleCanonicalTransferMeta.FromRecord(record, selected))).Success);
        Assert.True(stream.TryAcceptData(artData, fin: true).Success);
        Assert.Equal(ArticleTransferPhase.AwaitingEnd, stream.Phase);
        var again = stream.TryAcceptData(ReadOnlySpan<byte>.Empty, fin: false);
        Assert.False(again.Success);
        Assert.Equal(VatpErrorCode.InvalidStateTransition, again.Error);
    }

    [Fact]
    public void DuplicateEnd_Fails()
    {
        var (record, selected, artData) = VatpTestArticles.CreateCanonical();
        var stream = new ArticleTransferReceiveStream(15, Guid.NewGuid(), record.ArtId);
        Assert.True(stream.TryAcceptMeta(VatpMetaCodec.Encode(ArticleCanonicalTransferMeta.FromRecord(record, selected))).Success);
        Assert.True(stream.TryAcceptData(artData, fin: true).Success);
        Assert.True(stream.TryAcceptEnd().Success);
        var second = stream.TryAcceptEnd();
        Assert.False(second.Success);
        Assert.Equal(VatpErrorCode.InvalidStateTransition, second.Error);
    }

    [Fact]
    public void ExactDataPlusFinPlusEndPlusInvalidHash_DoesNotProduceConsumableRecord()
    {
        var (record, selected, artData) = VatpTestArticles.CreateCanonical();
        var meta = new ArticleCanonicalTransferMeta(
            record.ArtHash ^ 0xDEADUL,
            record.ArtLines,
            record.ArtSize,
            selected,
            record.Fields);
        var stream = new ArticleTransferReceiveStream(3, Guid.NewGuid(), record.ArtId);
        Assert.True(stream.TryAcceptMeta(VatpMetaCodec.Encode(in meta)).Success);
        Assert.True(stream.TryAcceptData(artData, fin: true).Success);
        Assert.Equal(ArticleTransferPhase.AwaitingEnd, stream.Phase);
        var result = stream.TryAcceptEnd();
        Assert.False(result.Success);
        Assert.Equal(VatpErrorCode.ArtHashMismatch, result.Error);
        Assert.Equal(ArticleTransferPhase.Failed, stream.Phase);
        Assert.False(stream.HasConsumableRecord);
        Assert.Throws<InvalidOperationException>(() => _ = stream.ConsumableRecord);
    }

    [Fact]
    public void DataBeforeMeta_Fails()
    {
        var stream = new ArticleTransferReceiveStream(4, Guid.NewGuid(), ArticleId.FromMessageId("<a@b>"u8));
        var result = stream.TryAcceptData("abc"u8, fin: false);
        Assert.False(result.Success);
        Assert.Equal(VatpErrorCode.InvalidStateTransition, result.Error);
    }

    [Fact]
    public void DuplicateMeta_Fails()
    {
        var (record, selected, _) = VatpTestArticles.CreateCanonical();
        var metaBytes = VatpMetaCodec.Encode(ArticleCanonicalTransferMeta.FromRecord(record, selected));
        var stream = new ArticleTransferReceiveStream(5, Guid.NewGuid(), record.ArtId);
        Assert.True(stream.TryAcceptMeta(metaBytes).Success);
        var second = stream.TryAcceptMeta(metaBytes);
        Assert.False(second.Success);
        Assert.Equal(VatpErrorCode.InvalidStateTransition, second.Error);
    }

    [Fact]
    public void EndBeforeMeta_Fails()
    {
        var stream = new ArticleTransferReceiveStream(6, Guid.NewGuid(), ArticleId.FromMessageId("<a@b>"u8));
        var result = stream.TryAcceptEnd();
        Assert.False(result.Success);
        Assert.Equal(VatpErrorCode.IncompleteTransfer, result.Error);
    }

    [Fact]
    public void DataAfterCompleted_Fails()
    {
        var (record, selected, artData) = VatpTestArticles.CreateCanonical();
        var stream = new ArticleTransferReceiveStream(7, Guid.NewGuid(), record.ArtId);
        Assert.True(stream.TryAcceptMeta(VatpMetaCodec.Encode(ArticleCanonicalTransferMeta.FromRecord(record, selected))).Success);
        Assert.True(stream.TryAcceptData(artData, fin: true).Success);
        Assert.True(stream.TryAcceptEnd().Success);
        var again = stream.TryAcceptData("x"u8, fin: false);
        Assert.False(again.Success);
        Assert.Equal(VatpErrorCode.InvalidStateTransition, again.Error);
    }

    [Fact]
    public void Cancel_TerminatesWithoutRecord()
    {
        var (record, selected, _) = VatpTestArticles.CreateCanonical();
        var stream = new ArticleTransferReceiveStream(8, Guid.NewGuid(), record.ArtId);
        Assert.True(stream.TryAcceptMeta(VatpMetaCodec.Encode(ArticleCanonicalTransferMeta.FromRecord(record, selected))).Success);
        Assert.True(stream.TryCancel().Success);
        Assert.Equal(ArticleTransferPhase.Cancelled, stream.Phase);
        Assert.False(stream.HasConsumableRecord);
    }

    [Fact]
    public void ChunkedData_AssemblesExactBytes()
    {
        var (record, selected, artData) = VatpTestArticles.CreateCanonical();
        var stream = new ArticleTransferReceiveStream(9, Guid.NewGuid(), record.ArtId);
        Assert.True(stream.TryAcceptMeta(VatpMetaCodec.Encode(ArticleCanonicalTransferMeta.FromRecord(record, selected))).Success);

        var mid = artData.Length / 2;
        Assert.True(stream.TryAcceptData(artData.AsSpan(0, mid), fin: false).Success);
        Assert.True(stream.TryAcceptData(artData.AsSpan(mid), fin: true).Success);
        Assert.Equal(ArticleTransferPhase.AwaitingEnd, stream.Phase);
        Assert.False(stream.HasConsumableRecord);
        Assert.True(stream.TryAcceptEnd().Success);
        Assert.True(stream.HasConsumableRecord);
        Assert.True(stream.ConsumableRecord.ArtData.Span.SequenceEqual(artData));
    }

    [Fact]
    public void OversizedArticleMeta_Rejected()
    {
        var fields = new ArticleFieldTable(
            new ArticleByteRange(0, 1),
            ArticleByteRange.Absent,
            ArticleByteRange.Absent,
            ArticleByteRange.Absent,
            ArticleByteRange.Absent,
            ArticleByteRange.Absent,
            ArticleByteRange.Absent);
        var meta = new ArticleCanonicalTransferMeta(
            0,
            0,
            ArticleResourceLimits.MaxArticleBytes + 1,
            VectorNNTP.Common.Articles.Parsing.NntpArticleHeaderName.Date,
            fields);
        var stream = new ArticleTransferReceiveStream(10, Guid.NewGuid(), ArticleId.FromMessageId("<a@b>"u8));
        var result = stream.TryAcceptMeta(VatpMetaCodec.Encode(in meta));
        Assert.False(result.Success);
        Assert.Equal(VatpErrorCode.ArticleTooLarge, result.Error);
    }
}
