using VectorNNTP.Common.Articles;

namespace VectorNNTP.Common.Tests.Articles
{
    public sealed class ArticleTypeClassifierTests
    {
        [Fact]
        public void PlainText_IsDefault()
        {
            var type = ArticleTypeClassifier.Classify("From: a@b\r\nSubject: hi\r\n"u8, "hello\r\n"u8);
            Assert.Equal(ArticleType.Default, type);
        }

        [Fact]
        public void ControlHeader_IsControl()
        {
            var type = ArticleTypeClassifier.Classify("Control: newgroup alt.test\r\n"u8, default);
            Assert.True(type.HasFlag(ArticleType.Control));
            Assert.False(type.HasFlag(ArticleType.Cancel));
        }

        [Fact]
        public void ControlCancel_IsControlAndCancel()
        {
            var type = ArticleTypeClassifier.Classify("Control: cancel <m@example.test>\r\n"u8, default);
            Assert.True(type.HasFlag(ArticleType.Control));
            Assert.True(type.HasFlag(ArticleType.Cancel));
        }

        [Fact]
        public void MimeVersion_IsMime()
        {
            var type = ArticleTypeClassifier.Classify("Mime-Version: 1.0\r\n"u8, default);
            Assert.True(type.HasFlag(ArticleType.Mime));
        }

        [Fact]
        public void ContentTypePlain_IsMime()
        {
            var type = ArticleTypeClassifier.Classify("Content-Type: text/plain\r\n"u8, default);
            Assert.True(type.HasFlag(ArticleType.Mime));
        }

        [Fact]
        public void HtmlContentType_IsHtmlAndMime()
        {
            var type = ArticleTypeClassifier.Classify("Content-Type: text/html; charset=utf-8\r\n"u8, default);
            Assert.True(type.HasFlag(ArticleType.Html));
            Assert.True(type.HasFlag(ArticleType.Mime));
        }

        [Fact]
        public void MultipartContentType_IsMultipartAndMime()
        {
            var type = ArticleTypeClassifier.Classify("Content-Type: multipart/mixed; boundary=x\r\n"u8, default);
            Assert.True(type.HasFlag(ArticleType.Multipart));
            Assert.True(type.HasFlag(ArticleType.Mime));
        }

        [Fact]
        public void PostScriptContentType_IsPostScriptAndMime()
        {
            var type = ArticleTypeClassifier.Classify("Content-Type: application/postscript\r\n"u8, default);
            Assert.True(type.HasFlag(ArticleType.PostScript));
            Assert.True(type.HasFlag(ArticleType.Mime));
        }

        [Fact]
        public void BinHexContentType_IsBinHexAndMime()
        {
            var type = ArticleTypeClassifier.Classify("Content-Type: application/mac-binhex40\r\n"u8, default);
            Assert.True(type.HasFlag(ArticleType.BinHex));
            Assert.True(type.HasFlag(ArticleType.Mime));
        }

        [Fact]
        public void OctetStream_IsBinaryAndMime()
        {
            var type = ArticleTypeClassifier.Classify("Content-Type: application/octet-stream\r\n"u8, default);
            Assert.True(type.HasFlag(ArticleType.Binary));
            Assert.True(type.HasFlag(ArticleType.Mime));
        }

        [Fact]
        public void MessagePartial_IsPartialAndMime()
        {
            var type = ArticleTypeClassifier.Classify("Content-Type: message/partial; id=a; number=1; total=2\r\n"u8, default);
            Assert.True(type.HasFlag(ArticleType.Partial));
            Assert.True(type.HasFlag(ArticleType.Mime));
        }

        [Fact]
        public void MimeAndBase64Headers_ClassifyWithoutBodyScan()
        {
            var type = ArticleTypeClassifier.Classify(
                "Mime-Version: 1.0\r\nContent-Type: text/plain\r\nContent-Transfer-Encoding: base64\r\n"u8,
                default);
            Assert.True(type.HasFlag(ArticleType.Mime));
            Assert.True(type.HasFlag(ArticleType.Base64));
            Assert.True(type.HasFlag(ArticleType.Binary));
        }

        [Fact]
        public void BommaNewsTransferEncoding_IsBommaNewsAndBinary()
        {
            var type = ArticleTypeClassifier.Classify(
                "Content-Transfer-Encoding: X-Bommanews\r\n"u8,
                default);
            Assert.True(type.HasFlag(ArticleType.BommaNews));
            Assert.True(type.HasFlag(ArticleType.Binary));
        }

        [Fact]
        public void UniDataTransferEncoding_IsUniDataAndBinary()
        {
            var type = ArticleTypeClassifier.Classify(
                "Content-Transfer-Encoding: X-UnidataEncoding\r\n"u8,
                default);
            Assert.True(type.HasFlag(ArticleType.UniData));
            Assert.True(type.HasFlag(ArticleType.Binary));
        }

        [Fact]
        public void YbeginLine_IsYEncodedAndBinary()
        {
            var type = ArticleTypeClassifier.Classify(
                "Subject: part\r\n"u8,
                "=ybegin line=128 size=12 name=a.bin\r\n"u8);
            Assert.True(type.HasFlag(ArticleType.YEncoded));
            Assert.True(type.HasFlag(ArticleType.Binary));
        }

        [Fact]
        public void YbeginPart_IsYEncodedPartial()
        {
            var type = ArticleTypeClassifier.Classify(
                default,
                "=ybegin part=1 line=128 size=99 name=a.bin\r\n"u8);
            Assert.True(type.HasFlag(ArticleType.YEncoded));
            Assert.True(type.HasFlag(ArticleType.Partial));
            Assert.True(type.HasFlag(ArticleType.Binary));
        }

        [Fact]
        public void UuencodeBegin_IsUuEncode()
        {
            var type = ArticleTypeClassifier.Classify(default, "begin 644 file.dat\r\n"u8);
            Assert.True(type.HasFlag(ArticleType.UuEncode));
            Assert.True(type.HasFlag(ArticleType.Binary));
        }

        [Fact]
        public void PgpBegin_IsPgpMessage()
        {
            var type = ArticleTypeClassifier.Classify(default, "-----BEGIN PGP MESSAGE-----\r\n"u8);
            Assert.True(type.HasFlag(ArticleType.PgpMessage));
        }

        [Fact]
        public void BinaryShortCircuit_SkipsLaterBodyMarkersExceptYenc()
        {
            var type = ArticleType.Binary;
            ArticleTypeClassifier.ObserveLine("begin 644 later.dat"u8, inHeader: false, ref type);
            Assert.False(type.HasFlag(ArticleType.UuEncode));
            Assert.False(type.HasFlag(ArticleType.PgpMessage));

            ArticleTypeClassifier.ObserveLine("-----BEGIN PGP MESSAGE-----"u8, inHeader: false, ref type);
            Assert.False(type.HasFlag(ArticleType.PgpMessage));

            ArticleTypeClassifier.ObserveLine("=ybegin line=128 size=1 name=a.bin"u8, inHeader: false, ref type);
            Assert.True(type.HasFlag(ArticleType.YEncoded));
            Assert.True(type.HasFlag(ArticleType.Binary));
        }

        [Fact]
        public void HeaderControlStillObserved_WhenBinaryAlreadySet()
        {
            var type = ArticleType.Binary;
            ArticleTypeClassifier.ObserveLine("Control: cancel <m@example.test>"u8, inHeader: true, ref type);
            Assert.True(type.HasFlag(ArticleType.Control));
            Assert.True(type.HasFlag(ArticleType.Cancel));
        }

        [Fact]
        public void ContentLength_ParsesDestuffedHint()
        {
            Assert.Equal(12, ArticleTypeClassifier.TryParseContentLength("Content-Length: 12"u8));
            Assert.Equal(-1, ArticleTypeClassifier.TryParseContentLength("Subject: 12"u8));
        }

        [Fact]
        public void YencSize_IsDecodedSizeNotWire()
        {
            Assert.Equal(12345, ArticleTypeClassifier.TryParseYencSize("=ybegin line=128 size=12345 name=a.bin"u8));
            Assert.Equal(-1, ArticleTypeClassifier.TryParseYencSize("=ybegin line=128 name=a.bin"u8));
        }

        [Fact]
        public void FlagBits_MatchHistoricalDiabloMapping()
        {
            Assert.Equal(0, (int)ArticleType.None);
            Assert.Equal(1 << 0, (int)ArticleType.Default);
            Assert.Equal(1 << 1, (int)ArticleType.Control);
            Assert.Equal(1 << 2, (int)ArticleType.Cancel);
            Assert.Equal(1 << 3, (int)ArticleType.Mime);
            Assert.Equal(1 << 4, (int)ArticleType.Binary);
            Assert.Equal(1 << 5, (int)ArticleType.UuEncode);
            Assert.Equal(1 << 6, (int)ArticleType.Base64);
            Assert.Equal(1 << 7, (int)ArticleType.YEncoded);
            Assert.Equal(1 << 8, (int)ArticleType.BommaNews);
            Assert.Equal(1 << 9, (int)ArticleType.UniData);
            Assert.Equal(1 << 10, (int)ArticleType.Multipart);
            Assert.Equal(1 << 11, (int)ArticleType.Html);
            Assert.Equal(1 << 12, (int)ArticleType.PostScript);
            Assert.Equal(1 << 13, (int)ArticleType.BinHex);
            Assert.Equal(1 << 14, (int)ArticleType.Partial);
            Assert.Equal(1 << 15, (int)ArticleType.PgpMessage);
        }
    }
}
