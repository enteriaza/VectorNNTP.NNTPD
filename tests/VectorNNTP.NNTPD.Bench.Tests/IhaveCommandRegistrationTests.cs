using System.Text;

namespace VectorNNTP.NNTPD.Bench.Tests;

public sealed class IhaveCommandRegistrationTests
{
    [Fact]
    public void Catalog_RegistersIhaveAsFirstClassCommand()
    {
        Assert.Contains(BenchmarkWorkloadCatalog.All, static w => w.Name == "IHAVE");
        Assert.Equal("IHAVE", BenchmarkWorkloadCatalog.Resolve("IHAVE").Name);
        Assert.Equal("IHAVE", BenchmarkWorkloadCatalog.Resolve("ihave").Name);
        Assert.IsType<IhaveWorkload>(BenchmarkWorkloadCatalog.Resolve("IHAVE"));
    }

    [Fact]
    public void Parse_Ihave_UsesSharedCommandDefaults()
    {
        var options = BenchOptions.Parse(
        [
            "--benchmark", "IHAVE",
            "--host", "198.18.0.66",
            "--port", "1199",
            "--connections", "1",
            "--duration", "30",
        ]);

        Assert.Equal("IHAVE", options.Benchmark);
        Assert.Equal("198.18.0.66", options.Host);
        Assert.Equal(1199, options.PlainPort);
        Assert.Equal(1, options.ConnectionsFilter);
        Assert.Equal(30, options.MeasureSeconds);
        Assert.Equal(0, options.WarmupSeconds);
        Assert.Equal(1, options.Runs);
        Assert.False(options.Timing);
        Assert.Equal("IHAVE", BenchmarkWorkloadCatalog.Resolve(options.Benchmark).Name);
    }

    [Fact]
    public void Parse_Ihave_HonorsExplicitMethodologyFlags()
    {
        var options = BenchOptions.Parse(
        [
            "--benchmark", "IHAVE",
            "--warmup-seconds", "5",
            "--measure-seconds", "60",
            "--runs", "2",
            "--connections", "1",
        ]);

        Assert.Equal(5, options.WarmupSeconds);
        Assert.Equal(60, options.MeasureSeconds);
        Assert.Equal(2, options.Runs);
        Assert.Equal(1, options.ConnectionsFilter);
        Assert.False(options.Timing);
        Assert.Equal(BenchOptions.DefaultTimingSamples, options.TimingSamples);
    }

    [Fact]
    public void Parse_IhaveTiming_UsesSamplesAndWarmup()
    {
        var options = BenchOptions.Parse(
        [
            "--benchmark", "IHAVE",
            "--timing",
            "--samples", "2500",
            "--host", "127.0.0.1",
            "--port", "1199",
        ]);

        Assert.True(options.Timing);
        Assert.Equal(2500, options.TimingSamples);
        Assert.Equal(5, options.WarmupSeconds);
        Assert.Equal(1, options.Runs);
    }

    [Fact]
    public void Parse_Timing_AllowsTakeThis()
    {
        var options = BenchOptions.Parse(
        [
            "--benchmark", "TAKETHIS",
            "--timing",
            "--samples", "1000",
            "--host", "127.0.0.1",
            "--port", "1199",
        ]);

        Assert.True(options.Timing);
        Assert.Equal(1000, options.TimingSamples);
        Assert.Equal(5, options.WarmupSeconds);
        Assert.Equal("TAKETHIS", options.Benchmark);
    }

    [Fact]
    public void Parse_Timing_WithoutIhaveOrTakeThis_Throws()
    {
        Assert.Throws<ArgumentException>(() => BenchOptions.Parse(["--benchmark", "CHECK", "--timing"]));
    }

    [Fact]
    public void Parse_Samples_WithoutTiming_Throws()
    {
        Assert.Throws<ArgumentException>(() => BenchOptions.Parse(["--benchmark", "IHAVE", "--samples", "10"]));
    }

    [Fact]
    public void MessageIds_AreUnique_AndFixedLength()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var buffer = new IhaveCommandBuffer(3, instance: 1);
        var first = buffer.Buffer.ToArray();
        buffer.SetSequence(42);
        var second = buffer.Buffer.ToArray();
        Assert.Equal(first.Length, second.Length);
        Assert.Equal(
            Encoding.ASCII.GetBytes("IHAVE <i0000000001-03-000000000042@vectornntp.local>\r\n"),
            second);
        Assert.Equal("<i0000000001-03-000000000042@vectornntp.local>", buffer.CurrentMessageId());

        for (var connection = 0; connection < 4; connection++)
        {
            var command = new IhaveCommandBuffer(connection, instance: 7);
            for (var sequence = 1; sequence <= 50; sequence++)
            {
                command.SetSequence(sequence);
                Assert.True(seen.Add(command.CurrentMessageId()), command.CurrentMessageId());
                Assert.Equal(
                    IhaveCommandBuffer.FormatMessageId(connection, sequence, instance: 7),
                    command.CurrentMessageId());
            }
        }

        Assert.Equal(200, seen.Count);
    }

    [Fact]
    public void PreparedArticles_RestuffStoredCorpusBytes()
    {
        var stored = "Subject: hi\r\n\r\n.dot\r\n"u8.ToArray();
        var wire = IhaveCorpusCatalog.ToWireArticle(stored);
        Assert.True(wire.AsSpan().EndsWith("\r\n.\r\n"u8));
        Assert.Contains("..dot\r\n"u8.ToArray(), wire);
        var prepared = IhavePreparedArticles.FromWireArticles(wire);
        var index = 0;
        Assert.Equal(wire, prepared.Next(ref index));
        Assert.Equal(1, prepared.PreparedCount);
        Assert.Equal(wire.Length, prepared.PreparedBytes);
    }
}
