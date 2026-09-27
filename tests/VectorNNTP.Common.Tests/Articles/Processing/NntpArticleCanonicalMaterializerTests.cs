using System.Text;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Articles.Parsing;
using VectorNNTP.Common.Articles.Processing;
using VectorNNTP.Common.Articles.YEnc;

namespace VectorNNTP.Common.Tests.Articles.Processing;

public sealed class NntpArticleCanonicalMaterializerTests
{
    private const string LocalFqdn = "backfiller01.usenet.ninja";
    private const string OrganizationalTrackerPrefix = "news.usenet.ninja!";

    [Fact]
    public void Materialize_WhenPathMissing_InsertsCanonicalPathHeaderAndRewritesDate()
    {
        var parser = new NntpArticleParser(LocalFqdn);
        var originalArticle = BuildArticle(
            [
                "Date: Tue, 10 May 2011 13:48:50 -0500",
                "Message-ID: <materialize-path-missing@example.test>",
                "Newsgroups: alt.test",
                "From: user@example.test",
                "Subject: keep",
            ],
            "body-1\r\n");

        var parse = parser.Parse(originalArticle);
        Assert.True(parse.IsAccepted);

        var materialized = MaterializeAccepted(parse);
        var headers = GetHeaderText(materialized);
        Assert.Contains("Date: Tue, 10 May 2011 18:48:50 +0000\r\n", headers, StringComparison.Ordinal);
        Assert.Contains($"Path: {OrganizationalTrackerPrefix}{LocalFqdn}", headers, StringComparison.Ordinal);
        Assert.Contains("Subject: keep\r\n", headers, StringComparison.Ordinal);
        Assert.Equal(parse.BodyBytes.ToArray(), GetBodyBytes(materialized));
    }

    [Fact]
    public void Materialize_WhenPathPresentWithoutLocalFqdn_ReplacesPathValueWithCanonicalPath()
    {
        var parser = new NntpArticleParser(LocalFqdn);
        var originalArticle = BuildArticle(
            [
                "Date: Tue, 10 May 2011 13:48:50 -0500",
                "Message-ID: <materialize-path-replace@example.test>",
                "Newsgroups: alt.test",
                "From: user@example.test",
                "Path: news.example.org!feed2",
                "X-Custom: preserve",
            ],
            "body-2\r\n");

        var parse = parser.Parse(originalArticle);
        Assert.True(parse.IsAccepted);
        Assert.Equal("news.usenet.ninja!backfiller01.usenet.ninja!news.example.org!feed2", FormatCanonicalPath(parse));

        var materialized = MaterializeAccepted(parse);
        var headers = GetHeaderText(materialized);
        Assert.Contains("Path: news.usenet.ninja!backfiller01.usenet.ninja!news.example.org!feed2", headers, StringComparison.Ordinal);
        Assert.DoesNotContain("Path: news.example.org!feed2", headers, StringComparison.Ordinal);
        Assert.Contains("X-Custom: preserve", headers, StringComparison.Ordinal);
        Assert.Equal(parse.BodyBytes.ToArray(), GetBodyBytes(materialized));
    }

    [Fact]
    public void Materialize_WhenPathAlreadyContainsLocalFqdn_DoesNotDuplicatePathComponent()
    {
        var parser = new NntpArticleParser(LocalFqdn);
        var originalArticle = BuildArticle(
            [
                "Date: Tue, 10 May 2011 13:48:50 -0500",
                "Message-ID: <materialize-path-existing-fqdn@example.test>",
                "Newsgroups: alt.test",
                "From: user@example.test",
                "Path: backfiller01.usenet.ninja!news.example.org",
            ],
            "body-3\r\n");

        var parse = parser.Parse(originalArticle);
        Assert.True(parse.IsAccepted);

        var headers = GetHeaderText(MaterializeAccepted(parse));
        Assert.Contains("Path: news.usenet.ninja!backfiller01.usenet.ninja!news.example.org", headers, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(headers, "backfiller01.usenet.ninja"));
        Assert.Equal(1, CountOccurrences(headers, "news.usenet.ninja"));
    }

    [Fact]
    public void Materialize_WhenPathLacksTracker_InsertsTrackerBeforeApplicationHop()
    {
        var parser = new NntpArticleParser(LocalFqdn);
        var originalArticle = BuildArticle(
            [
                "Date: Tue, 10 May 2011 13:48:50 -0500",
                "Message-ID: <materialize-path-tracker-absent@example.test>",
                "Newsgroups: alt.test",
                "From: user@example.test",
                "Path: foo!bar!",
            ],
            "body\r\n");

        var parse = parser.Parse(originalArticle);
        Assert.True(parse.IsAccepted);
        Assert.False(parse.ContainsOrganizationalTracker);

        var materialized = MaterializeAccepted(parse);
        var headers = GetHeaderText(materialized);
        Assert.Contains("Path: news.usenet.ninja!backfiller01.usenet.ninja!foo!bar", headers, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(headers, "news.usenet.ninja"));
        Assert.Equal(parse.BodyBytes.ToArray(), GetBodyBytes(materialized));
    }

    [Fact]
    public void Materialize_WhenPathAlreadyStartsWithTracker_DoesNotDuplicateTracker()
    {
        var parser = new NntpArticleParser(LocalFqdn);
        var originalArticle = BuildArticle(
            [
                "Date: Tue, 10 May 2011 13:48:50 -0500",
                "Message-ID: <materialize-path-tracker-first@example.test>",
                "Newsgroups: alt.test",
                "From: user@example.test",
                "Path: news.usenet.ninja!foo!",
            ],
            "body\r\n");

        var parse = parser.Parse(originalArticle);
        Assert.True(parse.IsAccepted);
        Assert.True(parse.ContainsOrganizationalTracker);

        var headers = GetHeaderText(MaterializeAccepted(parse));
        Assert.Contains("Path: backfiller01.usenet.ninja!news.usenet.ninja!foo", headers, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(headers, "news.usenet.ninja"));
    }

    [Fact]
    public void Materialize_WhenTrackerOccursLater_DoesNotDuplicateTracker()
    {
        var parser = new NntpArticleParser(LocalFqdn);
        var originalArticle = BuildArticle(
            [
                "Date: Tue, 10 May 2011 13:48:50 -0500",
                "Message-ID: <materialize-path-tracker-later@example.test>",
                "Newsgroups: alt.test",
                "From: user@example.test",
                "Path: foo!news.usenet.ninja!bar!",
            ],
            "body\r\n");

        var parse = parser.Parse(originalArticle);
        Assert.True(parse.IsAccepted);
        Assert.True(parse.ContainsOrganizationalTracker);

        var headers = GetHeaderText(MaterializeAccepted(parse));
        Assert.Contains("Path: backfiller01.usenet.ninja!foo!news.usenet.ninja!bar", headers, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(headers, "news.usenet.ninja"));
    }

    [Fact]
    public void Materialize_WhenDateComesFromInjectionDate_RewritesWinningCandidateHeader()
    {
        var parser = new NntpArticleParser(LocalFqdn);
        var originalArticle = BuildArticle(
            [
                "Injection-Date: Tue, 10 May 2011 13:48:50 -0500",
                "Message-ID: <materialize-injection-date@example.test>",
                "Newsgroups: alt.test",
                "From: user@example.test",
            ],
            "body-4\r\n");

        var parse = parser.Parse(originalArticle);
        Assert.True(parse.IsAccepted);
        Assert.Equal(NntpArticleHeaderName.InjectionDate, parse.SelectedDateHeaderName);

        var headers = GetHeaderText(MaterializeAccepted(parse));
        Assert.Contains("Injection-Date: Tue, 10 May 2011 18:48:50 +0000\r\n", headers, StringComparison.Ordinal);
    }

    [Fact]
    public void Materialize_WhenCrOnlyAndPathMissing_InsertsPathWithCrOnlyAndPreservesBodyBoundary()
    {
        var parser = new NntpArticleParser(LocalFqdn);
        var originalArticle = BuildArticle(
            [
                "Date: Tue, 10 May 2011 13:48:50 -0500",
                "Message-ID: <materialize-cr-only-path-missing@example.test>",
                "Newsgroups: alt.test",
                "From: user@example.test",
                "Subject: keep",
            ],
            "body-cr-only\r",
            "\r");

        var parse = parser.Parse(originalArticle);
        Assert.True(parse.IsAccepted);

        var materialized = MaterializeAccepted(parse);
        var article = Encoding.ASCII.GetString(materialized);
        Assert.Contains($"Path: {OrganizationalTrackerPrefix}{LocalFqdn}\r", article, StringComparison.Ordinal);
        Assert.Contains("Date: Tue, 10 May 2011 18:48:50 +0000\r", article, StringComparison.Ordinal);
        Assert.Contains("\r\rbody-cr-only\r", article, StringComparison.Ordinal);
        Assert.DoesNotContain("\r\n", article, StringComparison.Ordinal);
        Assert.Equal(parse.BodyBytes.ToArray(), GetBodyBytes(materialized, "\r"));
    }

    [Fact]
    public void Materialize_WhenCrOnlyAndPathPresent_RewritesPathAndPreservesCrOnlySeparators()
    {
        var parser = new NntpArticleParser(LocalFqdn);
        var originalArticle = BuildArticle(
            [
                "Date: Tue, 10 May 2011 13:48:50 -0500",
                "Message-ID: <materialize-cr-only-path-present@example.test>",
                "Newsgroups: alt.test",
                "From: user@example.test",
                "Path: news.example.org!feed2",
            ],
            "body-cr-only-path\r",
            "\r");

        var parse = parser.Parse(originalArticle);
        Assert.True(parse.IsAccepted);

        var materialized = MaterializeAccepted(parse);
        var article = Encoding.ASCII.GetString(materialized);
        Assert.Contains($"Path: {OrganizationalTrackerPrefix}{LocalFqdn}!news.example.org!feed2\r", article, StringComparison.Ordinal);
        Assert.DoesNotContain("\r\n", article, StringComparison.Ordinal);
        Assert.Equal(parse.BodyBytes.ToArray(), GetBodyBytes(materialized, "\r"));
    }

    [Theory]
    [InlineData("\r\n")]
    [InlineData("\n")]
    [InlineData("\r")]
    public void Materialize_PreservesHeaderSeparatorStyleAndBodyBytesAcrossSupportedSeparators(string separator)
    {
        var parser = new NntpArticleParser(LocalFqdn);
        var originalArticle = BuildArticle(
            [
                "Date: Tue, 10 May 2011 13:48:50 -0500",
                "Message-ID: <materialize-separator-variant@example.test>",
                "Newsgroups: alt.test",
                "From: user@example.test",
                "Subject: separator-variant",
            ],
            $"body-{separator.Length}x{((int)separator[0]).ToString()}\r",
            separator);

        var parse = parser.Parse(originalArticle);
        Assert.True(parse.IsAccepted);

        var materialized = MaterializeAccepted(parse);
        var article = Encoding.ASCII.GetString(materialized);
        Assert.Contains($"Path: {OrganizationalTrackerPrefix}{LocalFqdn}{separator}", article, StringComparison.Ordinal);
        Assert.Contains($"Date: Tue, 10 May 2011 18:48:50 +0000{separator}", article, StringComparison.Ordinal);
        Assert.Contains($"{separator}{separator}", article, StringComparison.Ordinal);
        Assert.Equal(parse.BodyBytes.ToArray(), GetBodyBytes(materialized, separator));
    }

    [Theory]
    [InlineData("\n", "\r\n")]
    [InlineData("\r\n", "\r")]
    [InlineData("\r", "\r\n")]
    public void Materialize_WhenPathMissingAndBoundaryUsesMixedTerminators_PreservesBodyAndMaterializes(
        string lastHeaderTerminator,
        string boundaryTerminator)
    {
        var parser = new NntpArticleParser(LocalFqdn);
        var body = Encoding.ASCII.GetBytes("body-a\r\nbody-b\nbody-c\r\n\rmarker");
        var article = BuildArticleWithCustomBoundary(
            [
                ("Date: Tue, 10 May 2011 13:48:50 -0500", "\r\n"),
                ("Message-ID: <materialize-mixed-path-missing@example.test>", "\r\n"),
                ("Newsgroups: alt.test", "\r\n"),
                ("From: user@example.test", "\r\n"),
                ("X-Keep: separator-contract", lastHeaderTerminator),
            ],
            boundaryTerminator,
            body);

        var parse = parser.Parse(article);
        Assert.True(parse.IsAccepted);

        var materialized = MaterializeAccepted(parse);
        var materializedText = Encoding.ASCII.GetString(materialized);
        Assert.Contains($"Path: {OrganizationalTrackerPrefix}{LocalFqdn}{lastHeaderTerminator}", materializedText, StringComparison.Ordinal);
        Assert.Contains("Date: Tue, 10 May 2011 18:48:50 +0000\r\n", materializedText, StringComparison.Ordinal);
        Assert.Contains($"X-Keep: separator-contract{lastHeaderTerminator}", materializedText, StringComparison.Ordinal);

        var reparsed = parser.Parse(materialized);
        Assert.True(reparsed.IsAccepted);
        Assert.Equal(parse.BodyBytes.ToArray(), reparsed.BodyBytes.ToArray());
    }

    [Theory]
    [InlineData("\n", "\r\n")]
    [InlineData("\r\n", "\r")]
    public void Materialize_WhenPathPresentAsFinalHeaderAndBoundaryUsesMixedTerminators_RewritesPathWithoutChangingBody(
        string lastHeaderTerminator,
        string boundaryTerminator)
    {
        var parser = new NntpArticleParser(LocalFqdn);
        var body = Encoding.ASCII.GetBytes("body-path-last\r\n\ntrailer");
        var article = BuildArticleWithCustomBoundary(
            [
                ("Date: Tue, 10 May 2011 13:48:50 -0500", "\r\n"),
                ("Message-ID: <materialize-mixed-path-last@example.test>", "\r\n"),
                ("Newsgroups: alt.test", "\r\n"),
                ("From: user@example.test", "\r\n"),
                ("Path: upstream.example.test!feed2", lastHeaderTerminator),
            ],
            boundaryTerminator,
            body);

        var parse = parser.Parse(article);
        Assert.True(parse.IsAccepted);
        Assert.Equal($"{OrganizationalTrackerPrefix}{LocalFqdn}!upstream.example.test!feed2", FormatCanonicalPath(parse));

        var materializedText = Encoding.ASCII.GetString(MaterializeAccepted(parse));
        Assert.Contains($"Path: {OrganizationalTrackerPrefix}{LocalFqdn}!upstream.example.test!feed2{lastHeaderTerminator}", materializedText, StringComparison.Ordinal);
        Assert.DoesNotContain("Path: upstream.example.test!feed2", materializedText, StringComparison.Ordinal);

        var reparsed = parser.Parse(Encoding.ASCII.GetBytes(materializedText));
        Assert.True(reparsed.IsAccepted);
        Assert.Equal(parse.BodyBytes.ToArray(), reparsed.BodyBytes.ToArray());
    }

    [Theory]
    [InlineData("\n", "\r\n")]
    [InlineData("\r\n", "\r")]
    public void Materialize_WhenDateIsFinalHeaderAndBoundaryUsesMixedTerminators_RewritesDateAndPreservesBody(
        string lastHeaderTerminator,
        string boundaryTerminator)
    {
        var parser = new NntpArticleParser(LocalFqdn);
        var body = Encoding.ASCII.GetBytes("body-date-last\n\r\nEND");
        var article = BuildArticleWithCustomBoundary(
            [
                ("Message-ID: <materialize-mixed-date-last@example.test>", "\r\n"),
                ("Newsgroups: alt.test", "\r\n"),
                ("From: user@example.test", "\r\n"),
                ("Date: Tue, 10 May 2011 13:48:50 -0500", lastHeaderTerminator),
            ],
            boundaryTerminator,
            body);

        var parse = parser.Parse(article);
        Assert.True(parse.IsAccepted);

        var materialized = MaterializeAccepted(parse);
        var materializedText = Encoding.ASCII.GetString(materialized);
        Assert.Contains($"Date: Tue, 10 May 2011 18:48:50 +0000{lastHeaderTerminator}", materializedText, StringComparison.Ordinal);
        Assert.Contains($"Path: {OrganizationalTrackerPrefix}{LocalFqdn}{lastHeaderTerminator}", materializedText, StringComparison.Ordinal);

        var reparsed = parser.Parse(materialized);
        Assert.True(reparsed.IsAccepted);
        Assert.Equal(parse.BodyBytes.ToArray(), reparsed.BodyBytes.ToArray());
    }

    [Fact]
    public void Materialize_WhenArticleIsRejected_DoesNotBypassValidation()
    {
        var parser = new NntpArticleParser(LocalFqdn);
        var originalArticle = BuildArticle(
            [
                "Date: INVALID",
                "Message-ID: <materialize-invalid-date@example.test>",
                "Newsgroups: alt.test",
                "From: user@example.test",
            ],
            "body-5\r\n");

        var parse = parser.Parse(originalArticle);
        Assert.False(parse.IsAccepted);

        var result = NntpArticleCanonicalMaterializer.Materialize(parse);
        Assert.False(result.IsAccepted);
        Assert.Equal(NntpArticleCanonicalFailureCode.ParseNotAccepted, result.FailureCode);
        Assert.Null(result.ArticleBytes);
    }

    [Fact]
    public void Materialize_WhenYEncArticleAccepted_PreservesOriginalValidatedYEncBodyBytes()
    {
        var parser = new NntpArticleParser(LocalFqdn);
        var yEncBody = BuildSyntheticSinglePartYEncBody([0x00, 0x2E, 0x3D, 0x41, 0x20]);
        var originalArticle = BuildArticle(
            [
                "Date: Tue, 10 May 2011 13:48:50 -0500",
                "Message-ID: <materialize-yenc-preserve@example.test>",
                "Newsgroups: alt.test",
                "From: user@example.test",
            ],
            yEncBody);

        var parse = parser.Parse(originalArticle);
        Assert.True(parse.IsAccepted);
        Assert.True(parse.YEncDetected);
        Assert.Equal(YEncArticleValidationStatus.ValidSinglePart, parse.YEncValidation.Status);

        var materializedBody = GetBodyBytes(MaterializeAccepted(parse));
        Assert.Equal(parse.BodyBytes.ToArray(), materializedBody);
        Assert.Equal(yEncBody, materializedBody);
    }

    [Fact]
    public void Materialize_WhenCanonicalDestinationWouldExceedHardArticleBytes_RejectsBeforeAllocatingSuccess()
    {
        var parser = new NntpArticleParser(LocalFqdn);
        string[] headers =
        [
            "Date: Tue, 10 May 2011 13:48:50 -0500",
            "Message-ID: <materialize-size-boundary@example.test>",
            "Newsgroups: alt.test",
            "From: user@example.test",
            "Path: b",
        ];

        var baselineArticle = BuildArticle(headers, []);
        var baselineParse = parser.Parse(baselineArticle);
        Assert.True(baselineParse.IsAccepted);

        var canonicalGrowth = (FormatCanonicalUtc(baselineParse).Length - baselineParse.OriginalDateValue.Length)
            + (FormatCanonicalPath(baselineParse).Length - baselineParse.OriginalPathValue.Length);
        Assert.True(canonicalGrowth > 0);

        var targetAcceptedLength = ArticleResourceLimits.MaxArticleBytes - canonicalGrowth + 1;
        var bodyLength = targetAcceptedLength - baselineParse.HeaderBytes.Length;
        Assert.True(bodyLength > 0);

        var article = BuildArticle(headers, BuildSafeBody(bodyLength));
        var parse = parser.Parse(article);
        Assert.True(parse.IsAccepted);
        Assert.Equal(targetAcceptedLength, parse.ArticleBytes.Length);

        var expectedCanonicalLength = parse.ArticleBytes.Length
            + FormatCanonicalUtc(parse).Length
            - parse.OriginalDateValue.Length
            + FormatCanonicalPath(parse).Length
            - parse.OriginalPathValue.Length;
        Assert.True(expectedCanonicalLength > ArticleResourceLimits.MaxArticleBytes);

        var result = NntpArticleCanonicalMaterializer.Materialize(parse);
        Assert.False(result.IsAccepted);
        Assert.Equal(NntpArticleCanonicalFailureCode.ArticleTooLarge, result.FailureCode);
        Assert.Null(result.ArticleBytes);
    }

    [Fact]
    public void Materialize_WhenCanonicalPathRewriteWouldExceedHardLineBytes_RejectsMaterialization()
    {
        var longFqdn = new string('a', 1017);
        var parser = new NntpArticleParser(longFqdn);
        var article = BuildArticle(
            [
                "Date: Tue, 10 May 2011 13:48:50 -0500",
                "Message-ID: <materialize-path-line-limit@example.test>",
                "Newsgroups: alt.test",
                "From: user@example.test",
                "Path: b",
            ],
            "body\r\n");

        var parse = parser.Parse(article);
        Assert.True(parse.IsAccepted);
        Assert.True(FormatCanonicalPath(parse).Length + "Path: ".Length > ArticleResourceLimits.MaxArticleLineBytes);

        var result = NntpArticleCanonicalMaterializer.Materialize(parse);
        Assert.False(result.IsAccepted);
        Assert.Equal(NntpArticleCanonicalFailureCode.PathRewriteLineTooLong, result.FailureCode);
        Assert.Null(result.ArticleBytes);
    }

    [Fact]
    public void Materialize_WhenCanonicalPathInsertionIsExactlyHardBoundary_SucceedsAtBoundary()
    {
        var boundaryFqdn = new string('a', ArticleResourceLimits.MaxArticleLineBytes - "Path: ".Length - OrganizationalTrackerPrefix.Length);
        var parser = new NntpArticleParser(boundaryFqdn);
        var article = BuildArticle(
            [
                "Date: Tue, 10 May 2011 13:48:50 -0500",
                "Message-ID: <materialize-path-insert-boundary@example.test>",
                "Newsgroups: alt.test",
                "From: user@example.test",
            ],
            "body\r\n");

        var parse = parser.Parse(article);
        Assert.True(parse.IsAccepted);
        Assert.Equal(OrganizationalTrackerPrefix + boundaryFqdn, FormatCanonicalPath(parse));

        var headers = GetHeaderText(MaterializeAccepted(parse));
        var pathLine = FindHeaderLine(headers, "Path:");
        Assert.NotNull(pathLine);
        Assert.Equal($"Path: {OrganizationalTrackerPrefix}{boundaryFqdn}", pathLine);
        Assert.Equal(ArticleResourceLimits.MaxArticleLineBytes, Encoding.ASCII.GetByteCount(pathLine));
    }

    [Fact]
    public void Materialize_WhenRejectedForCanonicalBoundaries_DoesNotMutateSourceBytes()
    {
        var longFqdn = new string('a', 1017);
        var parser = new NntpArticleParser(longFqdn);
        var article = BuildArticle(
            [
                "Date: Tue, 10 May 2011 13:48:50 -0500",
                "Message-ID: <materialize-source-owner-preserved@example.test>",
                "Newsgroups: alt.test",
                "From: user@example.test",
                "Path: b",
            ],
            "body\r\n");

        var parse = parser.Parse(article);
        Assert.True(parse.IsAccepted);

        var result = NntpArticleCanonicalMaterializer.Materialize(parse);
        Assert.False(result.IsAccepted);
        Assert.Equal(NntpArticleCanonicalFailureCode.PathRewriteLineTooLong, result.FailureCode);
        Assert.Equal((byte)'D', article[0]);
        Assert.True(parse.ArticleBytes.Span.SequenceEqual(article));
    }

    [Theory]
    [InlineData("\r\n", 1024)]
    [InlineData("\r\n", 1025)]
    [InlineData("\n", 1024)]
    [InlineData("\n", 1025)]
    [InlineData("\r", 1024)]
    [InlineData("\r", 1025)]
    public void Materialize_WhenPathInsertionLineContentAtOrAboveBoundary_EnforcesContentByteLimit(
        string separator,
        int pathContentLength)
    {
        var fqdn = new string('a', pathContentLength - "Path: ".Length - OrganizationalTrackerPrefix.Length);
        var parser = new NntpArticleParser(fqdn);
        var article = BuildArticle(
            [
                "Date: Tue, 10 May 2011 13:48:50 -0500",
                "Message-ID: <materialize-path-insert-exact-line-content-boundary@example.test>",
                "Newsgroups: alt.test",
                "From: user@example.test",
            ],
            "body\r\n",
            separator);

        var parse = parser.Parse(article);
        Assert.True(parse.IsAccepted);

        if (pathContentLength == 1024)
        {
            var materializedText = Encoding.ASCII.GetString(MaterializeAccepted(parse));
            var pathLine = ExtractHeaderLine(materializedText, "Path:", separator);
            Assert.Equal(1024, Encoding.ASCII.GetByteCount(pathLine));
            Assert.Equal(separator.Length, DetermineLineTerminatorLength(materializedText, "Path:", separator));
        }
        else
        {
            var result = NntpArticleCanonicalMaterializer.Materialize(parse);
            Assert.False(result.IsAccepted);
            Assert.Equal(NntpArticleCanonicalFailureCode.PathInsertionLineTooLong, result.FailureCode);
        }
    }

    [Fact]
    public void Materialize_WhenPathRewriteLineContentAt1024_SucceedsAnd1025Rejects()
    {
        const string localPrefix = "bf";
        var rewritePrefixLength = OrganizationalTrackerPrefix.Length + localPrefix.Length + 1;
        var pathValueAt1024 = 1024 - "Path: ".Length;

        var upstreamAt1024 = new string('x', pathValueAt1024 - rewritePrefixLength);
        var upstreamAt1025 = new string('x', pathValueAt1024 - rewritePrefixLength + 1);
        var parser = new NntpArticleParser(localPrefix);

        var acceptedArticle = BuildArticle(
            [
                "Date: Tue, 10 May 2011 13:48:50 -0500",
                "Message-ID: <materialize-path-rewrite-line-content-1024@example.test>",
                "Newsgroups: alt.test",
                "From: user@example.test",
                $"Path: {upstreamAt1024}",
            ],
            "body\r\n");
        var acceptedParse = parser.Parse(acceptedArticle);
        Assert.True(acceptedParse.IsAccepted);

        var acceptedPathLine = FindHeaderLine(GetHeaderText(MaterializeAccepted(acceptedParse)), "Path:");
        Assert.NotNull(acceptedPathLine);
        Assert.Equal(1024, Encoding.ASCII.GetByteCount(acceptedPathLine));

        var rejectedArticle = BuildArticle(
            [
                "Date: Tue, 10 May 2011 13:48:50 -0500",
                "Message-ID: <materialize-path-rewrite-line-content-1025@example.test>",
                "Newsgroups: alt.test",
                "From: user@example.test",
                $"Path: {upstreamAt1025}",
            ],
            "body\r\n");
        var rejectedParse = parser.Parse(rejectedArticle);
        Assert.True(rejectedParse.IsAccepted);

        var result = NntpArticleCanonicalMaterializer.Materialize(rejectedParse);
        Assert.False(result.IsAccepted);
        Assert.Equal(NntpArticleCanonicalFailureCode.PathRewriteLineTooLong, result.FailureCode);
    }

    [Fact]
    public void Materialize_WhenDateRewriteAt1024LineContentBoundary_SucceedsAnd1025Rejects()
    {
        const string dateHeaderName = "Injection-Date";
        const string shortDateValue = "Wed, 1 Jun 2011 13:48:50 -0500";
        const string canonicalDate = "Wed, 01 Jun 2011 18:48:50 +0000";
        var paddingForAccepted = 1023 - (dateHeaderName.Length + 2 + shortDateValue.Length);
        var paddingForRejected = 1024 - (dateHeaderName.Length + 2 + shortDateValue.Length);

        string BuildDateValue(int leadingPadding) => $"{new string(' ', leadingPadding)}{shortDateValue}";

        var parser = new NntpArticleParser(LocalFqdn, NntpArticleParserOptions.Default with { MaxHeaderValueBytes = 200_000 });

        var acceptedArticle = BuildArticle(
            [
                $"{dateHeaderName}: {BuildDateValue(paddingForAccepted)}",
                "Message-ID: <materialize-date-rewrite-line-content-1024@example.test>",
                "Newsgroups: alt.test",
                "From: user@example.test",
                "Path: x",
            ],
            "body\r\n");
        var acceptedParse = parser.Parse(acceptedArticle);
        Assert.True(acceptedParse.IsAccepted);
        Assert.Equal(canonicalDate, FormatCanonicalUtc(acceptedParse));

        var acceptedDateLine = FindHeaderLine(GetHeaderText(MaterializeAccepted(acceptedParse)), $"{dateHeaderName}:");
        Assert.NotNull(acceptedDateLine);
        Assert.Equal(1024, Encoding.ASCII.GetByteCount(acceptedDateLine));

        var rejectedArticle = BuildArticle(
            [
                $"{dateHeaderName}: {BuildDateValue(paddingForRejected)}",
                "Message-ID: <materialize-date-rewrite-line-content-1025@example.test>",
                "Newsgroups: alt.test",
                "From: user@example.test",
                "Path: x",
            ],
            "body\r\n");
        var rejectedParse = parser.Parse(rejectedArticle);
        Assert.True(rejectedParse.IsAccepted);
        Assert.Equal(canonicalDate, FormatCanonicalUtc(rejectedParse));

        var result = NntpArticleCanonicalMaterializer.Materialize(rejectedParse);
        Assert.False(result.IsAccepted);
        Assert.Equal(NntpArticleCanonicalFailureCode.DateLineTooLong, result.FailureCode);
    }

    private static byte[] MaterializeAccepted(in NntpArticleParseResult parse)
    {
        var result = NntpArticleCanonicalMaterializer.Materialize(parse);
        Assert.True(result.IsAccepted, result.FailureCode.ToString());
        Assert.NotNull(result.ArticleBytes);
        return result.ArticleBytes;
    }

    private static string FormatCanonicalUtc(in NntpArticleParseResult parse)
    {
        Span<byte> destination = stackalloc byte[40];
        Assert.True(parse.TryFormatCanonicalUtc(destination, out var written));
        return Encoding.ASCII.GetString(destination[..written]);
    }

    private static string FormatCanonicalPath(in NntpArticleParseResult parse)
    {
        Span<byte> destination = stackalloc byte[ArticlePathCanonicalizer.MaxPathLength + 256];
        Assert.True(parse.TryWriteCanonicalPath(destination, out var written));
        return Encoding.ASCII.GetString(destination[..written]);
    }

    private static byte[] BuildArticle(IReadOnlyList<string> headers, string body)
        => BuildArticle(headers, Encoding.ASCII.GetBytes(body));

    private static byte[] BuildArticleWithCustomBoundary(
        IReadOnlyList<(string Header, string Terminator)> headerLines,
        string boundaryTerminator,
        byte[] body)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < headerLines.Count; i++)
        {
            _ = builder.Append(headerLines[i].Header).Append(headerLines[i].Terminator);
        }

        _ = builder.Append(boundaryTerminator);
        var headerBytes = Encoding.ASCII.GetBytes(builder.ToString());
        var article = new byte[headerBytes.Length + body.Length];
        Buffer.BlockCopy(headerBytes, 0, article, 0, headerBytes.Length);
        Buffer.BlockCopy(body, 0, article, headerBytes.Length, body.Length);
        return article;
    }

    private static byte[] BuildSafeBody(int length)
    {
        if (length <= 0)
        {
            return [];
        }

        var body = new byte[length];
        Array.Fill(body, (byte)'A');
        for (var i = 100; i + 1 < body.Length; i += 102)
        {
            body[i] = (byte)'\r';
            body[i + 1] = (byte)'\n';
        }

        return body;
    }

    private static byte[] BuildArticle(IReadOnlyList<string> headers, string body, string separator)
        => BuildArticle(headers, Encoding.ASCII.GetBytes(body), separator);

    private static byte[] BuildArticle(IReadOnlyList<string> headers, byte[] body)
        => BuildArticle(headers, body, "\r\n");

    private static byte[] BuildArticle(IReadOnlyList<string> headers, byte[] body, string separator)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < headers.Count; i++)
        {
            _ = builder.Append(headers[i]).Append(separator);
        }

        _ = builder.Append(separator);
        var headerBytes = Encoding.ASCII.GetBytes(builder.ToString());
        var article = new byte[headerBytes.Length + body.Length];
        Buffer.BlockCopy(headerBytes, 0, article, 0, headerBytes.Length);
        Buffer.BlockCopy(body, 0, article, headerBytes.Length, body.Length);
        return article;
    }

    private static string GetHeaderText(ReadOnlySpan<byte> article)
    {
        var headerEnd = FindHeaderSeparator(article, out _);
        return Encoding.ASCII.GetString(article[..headerEnd]);
    }

    private static byte[] GetBodyBytes(ReadOnlySpan<byte> article)
        => GetBodyBytes(article, "\r\n");

    private static byte[] GetBodyBytes(ReadOnlySpan<byte> article, string separator)
    {
        var headerEnd = FindHeaderSeparator(article, out var separatorLength);
        if (separatorLength != separator.Length)
        {
            throw new InvalidOperationException($"Expected separator length {separator.Length} but found {separatorLength}.");
        }

        return article[(headerEnd + separatorLength + separatorLength)..].ToArray();
    }

    private static int FindHeaderSeparator(ReadOnlySpan<byte> article, out int separatorLength)
    {
        for (var i = 0; i <= article.Length - 4; i++)
        {
            if (article[i] == (byte)'\r'
                && article[i + 1] == (byte)'\n'
                && article[i + 2] == (byte)'\r'
                && article[i + 3] == (byte)'\n')
            {
                separatorLength = 2;
                return i;
            }
        }

        for (var i = 0; i <= article.Length - 2; i++)
        {
            if (article[i] == (byte)'\n' && article[i + 1] == (byte)'\n')
            {
                separatorLength = 1;
                return i;
            }

            if (article[i] == (byte)'\r' && article[i + 1] == (byte)'\r')
            {
                separatorLength = 1;
                return i;
            }
        }

        throw new InvalidOperationException("Article did not contain an accepted header separator.");
    }

    private static int CountOccurrences(string source, string value)
    {
        var count = 0;
        var index = 0;
        while (true)
        {
            index = source.IndexOf(value, index, StringComparison.Ordinal);
            if (index < 0)
            {
                return count;
            }

            count++;
            index += value.Length;
        }
    }

    private static string? FindHeaderLine(string headers, string prefix)
    {
        var lines = headers.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].StartsWith(prefix, StringComparison.Ordinal))
            {
                return lines[i];
            }
        }

        return null;
    }

    private static string ExtractHeaderLine(string materializedArticle, string prefix, string separator)
    {
        var lines = materializedArticle.Split(separator, StringSplitOptions.None);
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].StartsWith(prefix, StringComparison.Ordinal))
            {
                return lines[i];
            }
        }

        throw new InvalidOperationException($"Header line with prefix '{prefix}' was not found.");
    }

    private static int DetermineLineTerminatorLength(string materializedArticle, string prefix, string separator)
    {
        var index = materializedArticle.IndexOf(prefix, StringComparison.Ordinal);
        if (index < 0)
        {
            throw new InvalidOperationException($"Header line with prefix '{prefix}' was not found.");
        }

        var lineEnd = materializedArticle.IndexOf(separator, index, StringComparison.Ordinal);
        if (lineEnd < 0)
        {
            throw new InvalidOperationException($"Header line with prefix '{prefix}' did not contain separator '{separator}'.");
        }

        return separator.Length;
    }

    private static byte[] BuildSyntheticSinglePartYEncBody(ReadOnlySpan<byte> payload)
    {
        var encodedPayload = EncodeYEncPayload(payload);
        var crc = YEncCrc32.Compute(payload);
        var prefix = Encoding.ASCII.GetBytes($"=ybegin line=128 size={payload.Length} name=test.bin\r\n");
        var suffix = Encoding.ASCII.GetBytes($"\r\n=yend size={payload.Length} crc32={crc:x8}\r\n");
        var body = new byte[prefix.Length + encodedPayload.Length + suffix.Length];
        Buffer.BlockCopy(prefix, 0, body, 0, prefix.Length);
        Buffer.BlockCopy(encodedPayload, 0, body, prefix.Length, encodedPayload.Length);
        Buffer.BlockCopy(suffix, 0, body, prefix.Length + encodedPayload.Length, suffix.Length);
        return body;
    }

    private static byte[] EncodeYEncPayload(ReadOnlySpan<byte> decoded)
    {
        var output = new List<byte>();
        for (var i = 0; i < decoded.Length; i++)
        {
            var encoded = unchecked((byte)(decoded[i] + 42));
            var mustEscape = encoded is 0 or 9 or 10 or 13 or 32 or 46 or 61;
            if (mustEscape)
            {
                output.Add((byte)'=');
                output.Add(unchecked((byte)(encoded + 64)));
            }
            else
            {
                output.Add(encoded);
            }
        }

        if (output.Count == 0 || output[^1] != (byte)'\n')
        {
            output.Add((byte)'\r');
            output.Add((byte)'\n');
        }

        return [.. output];
    }
}
