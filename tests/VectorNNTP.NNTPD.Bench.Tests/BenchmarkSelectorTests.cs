namespace VectorNNTP.NNTPD.Bench.Tests;

public sealed class BenchmarkSelectorTests
{
    [Fact]
    public void Parse_Default_SelectsBenchIt()
    {
        var options = BenchOptions.Parse([]);
        Assert.Equal("BENCHIT", options.Benchmark);
        Assert.Equal("BENCHIT", BenchmarkWorkloadCatalog.Resolve(options.Benchmark).Name);
    }

    [Theory]
    [InlineData("BENCHIT")]
    [InlineData("benchit")]
    [InlineData("BenchIt")]
    public void Resolve_BenchIt_IsCaseInsensitive(string name)
    {
        Assert.Equal("BENCHIT", BenchmarkWorkloadCatalog.Resolve(name).Name);
    }

    [Theory]
    [InlineData("TAKETHIS")]
    [InlineData("takethis")]
    [InlineData("TakeThis")]
    public void Resolve_TakeThis_IsCaseInsensitive(string name)
    {
        Assert.Equal("TAKETHIS", BenchmarkWorkloadCatalog.Resolve(name).Name);
    }

    [Theory]
    [InlineData("CHECK")]
    [InlineData("check")]
    [InlineData("Check")]
    public void Resolve_Check_IsCaseInsensitive(string name)
    {
        Assert.Equal("CHECK", BenchmarkWorkloadCatalog.Resolve(name).Name);
    }

    [Theory]
    [InlineData("IHAVE")]
    [InlineData("ihave")]
    public void Resolve_Ihave_IsCaseInsensitive(string name)
    {
        Assert.Equal("IHAVE", BenchmarkWorkloadCatalog.Resolve(name).Name);
    }

    [Fact]
    public void Resolve_Unknown_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() => BenchmarkWorkloadCatalog.Resolve("NOT-A-WORKLOAD"));
        Assert.Contains("Unknown benchmark", ex.Message, StringComparison.Ordinal);
        Assert.Contains("BENCHIT", ex.Message, StringComparison.Ordinal);
        Assert.Contains("TAKETHIS", ex.Message, StringComparison.Ordinal);
        Assert.Contains("CHECK", ex.Message, StringComparison.Ordinal);
        Assert.Contains("IHAVE", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_Ihave_SharedDefaultsMatchTakeThis()
    {
        var options = BenchOptions.Parse(["--benchmark", "IHAVE"]);
        Assert.Equal("IHAVE", options.Benchmark);
        Assert.Equal(0, options.WarmupSeconds);
        Assert.Equal(30, options.MeasureSeconds);
        Assert.Equal(1, options.Runs);
        Assert.Equal("IHAVE", BenchmarkWorkloadCatalog.Resolve(options.Benchmark).Name);
    }

    [Fact]
    public void Parse_UnknownArgument_Throws()
    {
        Assert.Throws<ArgumentException>(() => BenchOptions.Parse(["--not-a-flag"]));
    }

    [Fact]
    public void Parse_TakeThis_SharedAndSpecificOptions()
    {
        var options = BenchOptions.Parse(
        [
            "--benchmark", "TAKETHIS",
            "--host", "127.0.0.1",
            "--port", "11996",
            "--connections", "10",
            "--duration", "30",
            "--article-size", "768000",
            "--pipeline-depth", "64",
        ]);

        Assert.Equal("TAKETHIS", options.Benchmark);
        Assert.Equal("127.0.0.1", options.Host);
        Assert.Equal(11996, options.PlainPort);
        Assert.Equal(10, options.ConnectionsFilter);
        Assert.Equal(30, options.MeasureSeconds);
        Assert.Equal(768000, options.ArticleSize);
        Assert.Equal(64, options.PipelineDepth);
        Assert.Equal(0, options.WarmupSeconds);
        Assert.Equal(1, options.Runs);
    }

    [Fact]
    public void Parse_BenchIt_PreservesHistoricalDefaults()
    {
        var options = BenchOptions.Parse(["--host", "198.18.0.66", "--plain-port", "1199"]);
        Assert.Equal("BENCHIT", options.Benchmark);
        Assert.Equal(5, options.WarmupSeconds);
        Assert.Equal(60, options.MeasureSeconds);
        Assert.Equal(2, options.Runs);
        Assert.Null(options.ConnectionsFilter);
        Assert.Null(options.ModeFilter);
    }
}
