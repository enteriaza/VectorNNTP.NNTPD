namespace VectorNNTP.StorageServer.Tests.Configuration;

public sealed class StorageServerJsonStorageRemovalTests
{
    [Fact]
    public void Shipped_configuration_has_no_journal_or_segment_settings()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "VectorNNTP.NNTPD.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        var path = Path.Combine(directory!.FullName, "src", "VectorNNTP.StorageServer", "VectorNNTP.StorageServer.json");
        var json = File.ReadAllText(path);
        Assert.DoesNotContain("Journal", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Segment", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"Storage\"", json, StringComparison.Ordinal);
        Assert.Contains("\"Listener\"", json, StringComparison.Ordinal);
        Assert.Contains("\"BindPortTls\"", json, StringComparison.Ordinal);
    }
}
