using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using VectorNNTP.NNTPD.Configuration;
using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Tests.Fixtures;

namespace VectorNNTP.StorageServer.Tests.Configuration;

public sealed class ArticleStorageOptionsTests
{
    [Fact]
    public void Defaults_are_documented_values()
    {
        var storage = new ArticleStorageOptions();
        Assert.Equal("control/", storage.ControlDir);
        Assert.Equal(64L * 1024 * 1024, storage.JournalSoftLimitBytes);
        Assert.Equal(128L * 1024 * 1024, storage.JournalHardLimitBytes);
        Assert.Equal(256L * 1024 * 1024, storage.SegmentTargetSizeBytes);
        Assert.Equal(0, storage.ArticleCache.MaxBytes);
        Assert.False(storage.Compaction.Enabled);
        Assert.False(storage.Compaction.MaintenanceEnabled);
        Assert.Equal(ArticleCompactionPolicyOptions.DefaultInterval, storage.Compaction.Interval);
        Assert.Equal(ArticleCompactionPolicyOptions.DefaultMinimumDeadBytes, storage.Compaction.MinimumDeadBytes);
        Assert.Equal(ArticleCompactionPolicyOptions.DefaultMinimumDeadRatio, storage.Compaction.MinimumDeadRatio);
        Assert.False(storage.Capacity.Enabled);
        Assert.Equal(ArticleCapacityOptions.DefaultMaximumUtilization, storage.Capacity.MaximumUtilization);
        Assert.Equal(ArticleCapacityOptions.DefaultCompactionHeadroom, storage.Capacity.CompactionHeadroom);
        Assert.Equal(ArticleStorageOptions.DefaultControlDir, StorageServerTestOptions.CreateValid().Storage.ControlDir);
    }

    [Fact]
    public void Runtime_resolves_ControlDir_and_keeps_CacheDir_as_SegmentDir()
    {
        var options = StorageServerTestOptions.CreateValid();
        options.CacheDir = "cache/";
        options.Storage.ControlDir = "control/";
        var runtime = StorageServerRuntimeOptionsFactory.Create(options, AppContext.BaseDirectory);
        Assert.Equal(
            ApplicationLocalPath.ResolveApplicationLocalPath("cache/", AppContext.BaseDirectory),
            runtime.CacheDir);
        Assert.Equal(
            ApplicationLocalPath.ResolveApplicationLocalPath("control/", AppContext.BaseDirectory),
            runtime.ControlDir);
        Assert.Equal(runtime.CacheDir, runtime.Storage.SegmentDir);
        Assert.Equal(runtime.ControlDir, runtime.Storage.ControlDir);
        Assert.NotEqual(runtime.LogDir, runtime.ControlDir);
        Assert.NotEqual(runtime.CacheDir, runtime.ControlDir);
    }

    [Fact]
    public void Validator_rejects_hard_limit_below_soft()
    {
        var options = StorageServerTestOptions.CreateValid();
        options.Storage.JournalSoftLimitBytes = 100;
        options.Storage.JournalHardLimitBytes = 50;
        var result = new StorageServerOptionsValidator().Validate(Options.DefaultName, options);
        Assert.True(result.Failed);
        Assert.Contains(
            result.Failures!,
            static f => f.Contains("JournalHardLimitBytes", StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_rejects_empty_ControlDir()
    {
        var options = StorageServerTestOptions.CreateValid();
        options.Storage.ControlDir = " ";
        var result = new StorageServerOptionsValidator().Validate(Options.DefaultName, options);
        Assert.True(result.Failed);
        Assert.Contains(
            result.Failures!,
            static f => f.Contains("ControlDir", StringComparison.Ordinal));
    }

    [Fact]
    public void Configuration_binding_applies_Storage_section()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["StorageServer:ServerId"] = "1",
                ["StorageServer:Storage:ControlDir"] = "nvme/control",
                ["StorageServer:Storage:JournalSoftLimitBytes"] = "10",
                ["StorageServer:Storage:JournalHardLimitBytes"] = "20",
                ["StorageServer:Storage:SegmentTargetSizeBytes"] = "30",
                ["StorageServer:Storage:ArticleCache:MaxBytes"] = "4096",
                ["StorageServer:Storage:Compaction:Enabled"] = "true",
                ["StorageServer:Storage:Compaction:MaintenanceEnabled"] = "true",
                ["StorageServer:Storage:Compaction:Interval"] = "00:00:30",
                ["StorageServer:Storage:Compaction:MinimumDeadBytes"] = "1048576",
                ["StorageServer:Storage:Compaction:MinimumDeadRatio"] = "0.25",
            })
            .Build();
        var bound = new StorageServerOptions();
        configuration.GetSection(StorageServerOptions.SectionName).Bind(bound);
        Assert.Equal("nvme/control", bound.Storage.ControlDir);
        Assert.Equal(10, bound.Storage.JournalSoftLimitBytes);
        Assert.Equal(20, bound.Storage.JournalHardLimitBytes);
        Assert.Equal(30, bound.Storage.SegmentTargetSizeBytes);
        Assert.Equal(4096, bound.Storage.ArticleCache.MaxBytes);
        Assert.True(bound.Storage.Compaction.Enabled);
        Assert.True(bound.Storage.Compaction.MaintenanceEnabled);
        Assert.Equal(TimeSpan.FromSeconds(30), bound.Storage.Compaction.Interval);
        Assert.Equal(1_048_576, bound.Storage.Compaction.MinimumDeadBytes);
        Assert.Equal(0.25, bound.Storage.Compaction.MinimumDeadRatio);
    }

    [Fact]
    public void Validator_rejects_zero_Compaction_Interval()
    {
        var options = StorageServerTestOptions.CreateValid();
        options.Storage.Compaction.Interval = TimeSpan.Zero;
        var result = new StorageServerOptionsValidator().Validate(Options.DefaultName, options);
        Assert.True(result.Failed);
        Assert.Contains(
            result.Failures!,
            static f => f.Contains("Compaction:Interval", StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_rejects_negative_Compaction_Interval()
    {
        var options = StorageServerTestOptions.CreateValid();
        options.Storage.Compaction.Interval = TimeSpan.FromSeconds(-1);
        var result = new StorageServerOptionsValidator().Validate(Options.DefaultName, options);
        Assert.True(result.Failed);
        Assert.Contains(
            result.Failures!,
            static f => f.Contains("Compaction:Interval", StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_allows_MaintenanceEnabled_with_policy_disabled_and_inverse()
    {
        var options = StorageServerTestOptions.CreateValid();
        options.Storage.Compaction.Enabled = false;
        options.Storage.Compaction.MaintenanceEnabled = true;
        options.Storage.Compaction.Interval = TimeSpan.FromSeconds(5);
        Assert.False(new StorageServerOptionsValidator().Validate(Options.DefaultName, options).Failed);

        options.Storage.Compaction.Enabled = true;
        options.Storage.Compaction.MaintenanceEnabled = false;
        Assert.False(new StorageServerOptionsValidator().Validate(Options.DefaultName, options).Failed);
    }

    [Fact]
    public void Validator_rejects_negative_ArticleCache_MaxBytes()
    {
        var options = StorageServerTestOptions.CreateValid();
        options.Storage.ArticleCache.MaxBytes = -1;
        var result = new StorageServerOptionsValidator().Validate(Options.DefaultName, options);
        Assert.True(result.Failed);
        Assert.Contains(
            result.Failures!,
            static f => f.Contains("ArticleCache:MaxBytes", StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_rejects_negative_Compaction_MinimumDeadBytes()
    {
        var options = StorageServerTestOptions.CreateValid();
        options.Storage.Compaction.MinimumDeadBytes = -1;
        var result = new StorageServerOptionsValidator().Validate(Options.DefaultName, options);
        Assert.True(result.Failed);
        Assert.Contains(
            result.Failures!,
            static f => f.Contains("Compaction:MinimumDeadBytes", StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_rejects_out_of_range_Compaction_MinimumDeadRatio()
    {
        var options = StorageServerTestOptions.CreateValid();
        options.Storage.Compaction.MinimumDeadRatio = 1.5;
        var result = new StorageServerOptionsValidator().Validate(Options.DefaultName, options);
        Assert.True(result.Failed);
        Assert.Contains(
            result.Failures!,
            static f => f.Contains("Compaction:MinimumDeadRatio", StringComparison.Ordinal));
    }

    [Fact]
    public void Production_appsettings_declares_Storage_ControlDir()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        Assert.True(File.Exists(path));
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        var storage = doc.RootElement.GetProperty("StorageServer").GetProperty("Storage");
        Assert.Equal("control/", storage.GetProperty("ControlDir").GetString());
        Assert.Equal(67108864, storage.GetProperty("JournalSoftLimitBytes").GetInt64());
        Assert.Equal(134217728, storage.GetProperty("JournalHardLimitBytes").GetInt64());
        Assert.Equal(268435456, storage.GetProperty("SegmentTargetSizeBytes").GetInt64());
        Assert.Equal(0, storage.GetProperty("ArticleCache").GetProperty("MaxBytes").GetInt64());
        var compaction = storage.GetProperty("Compaction");
        Assert.False(compaction.GetProperty("Enabled").GetBoolean());
        Assert.False(compaction.GetProperty("MaintenanceEnabled").GetBoolean());
        Assert.Equal("00:01:00", compaction.GetProperty("Interval").GetString());
        Assert.Equal(67108864, compaction.GetProperty("MinimumDeadBytes").GetInt64());
        Assert.Equal(0.10, compaction.GetProperty("MinimumDeadRatio").GetDouble());
        var capacity = storage.GetProperty("Capacity");
        Assert.False(capacity.GetProperty("Enabled").GetBoolean());
        Assert.Equal(0.80, capacity.GetProperty("MaximumUtilization").GetDouble());
        Assert.Equal(0.10, capacity.GetProperty("CompactionHeadroom").GetDouble());
    }

    [Fact]
    public void Validator_rejects_invalid_CompactionHeadroom()
    {
        var options = StorageServerTestOptions.CreateValid();
        options.Storage.Capacity.MaximumUtilization = 0.80;

        options.Storage.Capacity.CompactionHeadroom = 0;
        Assert.True(new StorageServerOptionsValidator().Validate(Options.DefaultName, options).Failed);
        options.Storage.Capacity.CompactionHeadroom = -0.1;
        Assert.True(new StorageServerOptionsValidator().Validate(Options.DefaultName, options).Failed);
        options.Storage.Capacity.CompactionHeadroom = double.NaN;
        Assert.True(new StorageServerOptionsValidator().Validate(Options.DefaultName, options).Failed);
        options.Storage.Capacity.CompactionHeadroom = double.PositiveInfinity;
        Assert.True(new StorageServerOptionsValidator().Validate(Options.DefaultName, options).Failed);
        options.Storage.Capacity.CompactionHeadroom = 0.20;
        Assert.True(new StorageServerOptionsValidator().Validate(Options.DefaultName, options).Failed);
        options.Storage.Capacity.CompactionHeadroom = 0.25;
        Assert.True(new StorageServerOptionsValidator().Validate(Options.DefaultName, options).Failed);
        options.Storage.Capacity.CompactionHeadroom = 0.10;
        Assert.False(new StorageServerOptionsValidator().Validate(Options.DefaultName, options).Failed);
    }

    [Fact]
    public void Configuration_rejects_malformed_Compaction_Interval()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["StorageServer:Storage:Compaction:Interval"] = "not-a-timespan",
            })
            .Build();
        var bound = new StorageServerOptions();
        Assert.ThrowsAny<Exception>(() =>
            configuration.GetSection(StorageServerOptions.SectionName).Bind(bound));
    }
}
