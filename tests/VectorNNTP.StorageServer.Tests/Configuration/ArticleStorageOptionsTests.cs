using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Tests.Fixtures;
using VectorNNTP.Common.Configuration;

namespace VectorNNTP.StorageServer.Tests.Configuration;

public sealed class ArticleStorageOptionsTests
{
    [Fact]
    public void Defaults_are_documented_values()
    {
        var storage = new ArticleStorageOptions();
        Assert.Equal("spool/cache", storage.CacheDir);
        Assert.Equal("spool/", storage.ControlDir);
        Assert.Equal(64L * 1024 * 1024, storage.JournalSoftLimitBytes);
        Assert.Equal(128L * 1024 * 1024, storage.JournalHardLimitBytes);
        Assert.Equal(0, storage.JournalCheckpointThresholdBytes);
        Assert.Equal(0, storage.IndexCheckpointThresholdBytes);
        Assert.Equal(256L * 1024 * 1024, storage.SegmentTargetSizeBytes);
        Assert.Equal(0, storage.ArticleCache.MaxBytes);
        Assert.Null(typeof(ArticleCompactionPolicyOptions).GetProperty("Enabled"));
        Assert.Null(typeof(ArticleCompactionPolicyOptions).GetProperty("Maintenance" + "Enabled"));
        Assert.Equal(ArticleCompactionPolicyOptions.DefaultInterval, storage.Compaction.Interval);
        Assert.Equal(ArticleCompactionPolicyOptions.DefaultMinimumDeadBytes, storage.Compaction.MinimumDeadBytes);
        Assert.Equal(ArticleCompactionPolicyOptions.DefaultMinimumDeadRatio, storage.Compaction.MinimumDeadRatio);
        Assert.Equal(ArticleCapacityOptions.DefaultMaximumUtilization, storage.Capacity.MaximumUtilization);
        Assert.Equal(ArticleCapacityOptions.DefaultCompactionHeadroom, storage.Capacity.CompactionHeadroom);
        Assert.Equal(ArticleCapacityOptions.DefaultMaximumUsageCapacity, storage.Capacity.MaximumUsageCapacity);
        Assert.Equal(ArticleCapacityOptions.DefaultFreeCapacity, storage.Capacity.FreeCapacity);
        Assert.Null(typeof(ArticleCapacityOptions).GetProperty("Enabled"));
        Assert.Equal(ArticleStorageOptions.DefaultControlDir, StorageServerTestOptions.CreateValid().Storage.ControlDir);
    }

    [Fact]
    public void Runtime_resolves_ControlDir_and_keeps_CacheDir_as_SegmentDir()
    {
        var options = StorageServerTestOptions.CreateValid();
        options.Storage.CacheDir = "spool/cache";
        options.Storage.ControlDir = "spool/";
        var runtime = StorageServerRuntimeOptionsFactory.Create(options, AppContext.BaseDirectory);
        Assert.Equal(
            ApplicationLocalPath.ResolveApplicationLocalPath("spool/cache", AppContext.BaseDirectory),
            runtime.CacheDir);
        Assert.Equal(
            ApplicationLocalPath.ResolveApplicationLocalPath("spool/", AppContext.BaseDirectory),
            runtime.ControlDir);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "spool", "cache")),
            runtime.CacheDir);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "spool")),
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
                ["StorageServer:Storage:CacheDir"] = "sata/cache",
                ["StorageServer:Storage:ControlDir"] = "nvme/control",
                ["StorageServer:Storage:JournalSoftLimitBytes"] = "10",
                ["StorageServer:Storage:JournalHardLimitBytes"] = "20",
                ["StorageServer:Storage:JournalCheckpointThresholdBytes"] = "4096",
                ["StorageServer:Storage:IndexCheckpointThresholdBytes"] = "8192",
                ["StorageServer:Storage:SegmentTargetSizeBytes"] = "30",
                ["StorageServer:Storage:ArticleCache:MaxBytes"] = "4096",
                ["StorageServer:Storage:Compaction:Maintenance" + "Enabled"] = "true",
                ["StorageServer:Storage:Compaction:Interval"] = "00:00:30",
                ["StorageServer:Storage:Compaction:MinimumDeadBytes"] = "1048576",
                ["StorageServer:Storage:Compaction:MinimumDeadRatio"] = "25",
            })
            .Build();
        var bound = new StorageServerOptions();
        configuration.GetSection(StorageServerOptions.SectionName).Bind(bound);
        Assert.Equal("sata/cache", bound.Storage.CacheDir);
        Assert.Equal("nvme/control", bound.Storage.ControlDir);
        Assert.Equal(10, bound.Storage.JournalSoftLimitBytes);
        Assert.Equal(20, bound.Storage.JournalHardLimitBytes);
        Assert.Equal(4096, bound.Storage.JournalCheckpointThresholdBytes);
        Assert.Equal(8192, bound.Storage.IndexCheckpointThresholdBytes);
        Assert.Equal(30, bound.Storage.SegmentTargetSizeBytes);
        Assert.Equal(4096, bound.Storage.ArticleCache.MaxBytes);
        Assert.Null(typeof(ArticleCompactionPolicyOptions).GetProperty("Enabled"));
        Assert.Null(typeof(ArticleCompactionPolicyOptions).GetProperty("Maintenance" + "Enabled"));
        Assert.Equal(TimeSpan.FromSeconds(30), bound.Storage.Compaction.Interval);
        Assert.Equal(1_048_576, bound.Storage.Compaction.MinimumDeadBytes);
        Assert.Equal(25, bound.Storage.Compaction.MinimumDeadRatio);
    }

    [Fact]
    public void Validator_rejects_negative_index_checkpoint_threshold()
    {
        var options = StorageServerTestOptions.CreateValid();
        options.Storage.IndexCheckpointThresholdBytes = -1;
        var result = new StorageServerOptionsValidator().Validate(Options.DefaultName, options);
        Assert.True(result.Failed);
        Assert.Contains(
            result.Failures!,
            static f => f.Contains("IndexCheckpointThresholdBytes", StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_accepts_positive_index_checkpoint_threshold()
    {
        var options = StorageServerTestOptions.CreateValid();
        options.Storage.IndexCheckpointThresholdBytes = 4096;
        var result = new StorageServerOptionsValidator().Validate(Options.DefaultName, options);
        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validator_rejects_negative_journal_checkpoint_threshold()
    {
        var options = StorageServerTestOptions.CreateValid();
        options.Storage.JournalCheckpointThresholdBytes = -1;
        var result = new StorageServerOptionsValidator().Validate(Options.DefaultName, options);
        Assert.True(result.Failed);
        Assert.Contains(
            result.Failures!,
            static f => f.Contains("JournalCheckpointThresholdBytes", StringComparison.Ordinal));
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
    public void Validator_accepts_compaction_without_an_enable_switch()
    {
        Assert.Null(typeof(ArticleCompactionPolicyOptions).GetProperty("Enabled"));
        var options = StorageServerTestOptions.CreateValid();
        options.Storage.Compaction.Interval = TimeSpan.FromSeconds(5);
        options.Storage.Compaction.MinimumDeadRatio = 10;
        Assert.False(new StorageServerOptionsValidator().Validate(Options.DefaultName, options).Failed);
        options.Storage.Compaction.MinimumDeadRatio = 0;
        Assert.False(new StorageServerOptionsValidator().Validate(Options.DefaultName, options).Failed);
        options.Storage.Compaction.MinimumDeadRatio = 100;
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
        options.Storage.Compaction.MinimumDeadRatio = -1;
        var below = new StorageServerOptionsValidator().Validate(Options.DefaultName, options);
        Assert.True(below.Failed);
        Assert.Contains(
            below.Failures!,
            static f => f.Contains("Compaction:MinimumDeadRatio", StringComparison.Ordinal));

        options.Storage.Compaction.MinimumDeadRatio = 101;
        var result = new StorageServerOptionsValidator().Validate(Options.DefaultName, options);
        Assert.True(result.Failed);
        Assert.Contains(
            result.Failures!,
            static f => f.Contains("Compaction:MinimumDeadRatio", StringComparison.Ordinal));
    }

    [Fact]
    public void Production_appsettings_declares_Storage_ControlDir()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "VectorNNTP.StorageServer.json");
        Assert.True(File.Exists(path));
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        var storage = doc.RootElement.GetProperty("StorageServer").GetProperty("Storage");
        Assert.False(doc.RootElement.GetProperty("StorageServer").TryGetProperty("CacheDir", out _));
        Assert.Equal("spool/cache", storage.GetProperty("CacheDir").GetString());
        Assert.Equal("spool/", storage.GetProperty("ControlDir").GetString());
        Assert.Equal(0, storage.GetProperty("IndexCheckpointThresholdBytes").GetInt64());
        Assert.Equal(134217728, storage.GetProperty("JournalCheckpointThresholdBytes").GetInt64());
        Assert.Equal(67108864, storage.GetProperty("JournalSoftLimitBytes").GetInt64());
        Assert.Equal(134217728, storage.GetProperty("JournalHardLimitBytes").GetInt64());
        Assert.Equal(10737418240, storage.GetProperty("SegmentTargetSizeBytes").GetInt64());
        Assert.Equal(1073741824, storage.GetProperty("ArticleCache").GetProperty("MaxBytes").GetInt64());
        var compaction = storage.GetProperty("Compaction");
        Assert.False(compaction.TryGetProperty("Enabled", out _));
        Assert.False(compaction.TryGetProperty("Maintenance" + "Enabled", out _));
        Assert.Equal("00:01:00", compaction.GetProperty("Interval").GetString());
        Assert.Equal(67108864, compaction.GetProperty("MinimumDeadBytes").GetInt64());
        Assert.Equal(10, compaction.GetProperty("MinimumDeadRatio").GetInt32());
        var capacity = storage.GetProperty("Capacity");
        Assert.False(capacity.TryGetProperty("Enabled", out _));
        Assert.Equal(10, capacity.GetProperty("CompactionHeadroom").GetInt32());
        Assert.Equal(5, capacity.GetProperty("FreeCapacity").GetInt32());
        Assert.Equal(50, capacity.GetProperty("MaximumUsageCapacity").GetInt32());
        Assert.Equal(70, capacity.GetProperty("MaximumUtilization").GetInt32());
        AssertJsonPropertiesMatchOptions(storage, typeof(ArticleStorageOptions));
        AssertJsonPropertiesMatchOptions(storage.GetProperty("ArticleCache"), typeof(ArticleMemoryCacheOptions));
        AssertJsonPropertiesMatchOptions(storage.GetProperty("Capacity"), typeof(ArticleCapacityOptions));
        AssertJsonPropertiesMatchOptions(storage.GetProperty("Compaction"), typeof(ArticleCompactionPolicyOptions));

        var probe = StorageServerTestOptions.CreateValid();
        probe.Storage = new ArticleStorageOptions();
        new ConfigurationBuilder().AddJsonFile(path).Build()
            .GetSection(StorageServerOptions.SectionName)
            .GetSection("Storage")
            .Bind(probe.Storage);
        var validation = new StorageServerOptionsValidator().Validate(Options.DefaultName, probe);
        Assert.True(validation.Succeeded, string.Join("; ", validation.Failures ?? []));
    }

    private static void AssertJsonPropertiesMatchOptions(JsonElement element, Type optionsType)
    {
        var expected = optionsType
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(static property => property.CanWrite && property.SetMethod is { IsPublic: true })
            .Select(static property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var actual = element.EnumerateObject()
            .Select(static property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Validator_rejects_invalid_CompactionHeadroom()
    {
        var options = StorageServerTestOptions.CreateValid();
        options.Storage.Capacity.MaximumUtilization = 80;
        options.Storage.Capacity.MaximumUsageCapacity = 70;

        options.Storage.Capacity.CompactionHeadroom = 0;
        Assert.True(new StorageServerOptionsValidator().Validate(Options.DefaultName, options).Failed);
        options.Storage.Capacity.CompactionHeadroom = -1;
        Assert.True(new StorageServerOptionsValidator().Validate(Options.DefaultName, options).Failed);
        options.Storage.Capacity.CompactionHeadroom = 101;
        Assert.True(new StorageServerOptionsValidator().Validate(Options.DefaultName, options).Failed);
        options.Storage.Capacity.CompactionHeadroom = 21;
        Assert.True(new StorageServerOptionsValidator().Validate(Options.DefaultName, options).Failed);
        options.Storage.Capacity.CompactionHeadroom = 20;
        Assert.False(new StorageServerOptionsValidator().Validate(Options.DefaultName, options).Failed);
        options.Storage.Capacity.CompactionHeadroom = 10;
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

    [Fact]
    public void Configuration_rejects_fractional_MinimumDeadRatio()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["StorageServer:Storage:Compaction:MinimumDeadRatio"] = "0.10",
            })
            .Build();
        var bound = new StorageServerOptions();
        Assert.ThrowsAny<Exception>(() =>
            configuration.GetSection(StorageServerOptions.SectionName).Bind(bound));
    }

    [Fact]
    public void Repository_does_not_name_the_removed_enable_switches()
    {
        var tokens = new[]
        {
            "Maintenance" + "Enabled",
            "Compaction:" + "Enabled",
            "Compaction." + "Enabled",
            "Capacity:" + "Enabled",
            "Capacity." + "Enabled",
        };
        var root = FindRepositoryRoot();
        var hits = new List<string>();
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            if (IsHistoricalOrBuildPath(path))
            {
                continue;
            }

            var extension = Path.GetExtension(path);
            if (extension is not (
                ".cs" or ".json" or ".md" or ".csproj" or ".props" or ".targets" or ".yml" or ".yaml" or ".xml"))
            {
                continue;
            }

            var text = File.ReadAllText(path);
            foreach (var token in tokens)
            {
                if (text.Contains(token, StringComparison.Ordinal))
                {
                    hits.Add(Path.GetRelativePath(root, path) + " [" + token + "]");
                    break;
                }
            }
        }

        Assert.Empty(hits);
    }

    private static bool IsHistoricalOrBuildPath(string path)
    {
        var parts = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        foreach (var part in parts)
        {
            if (part is "bin" or "obj" or ".git" or ".idea" or ".artifacts" or "TestResults" or "node_modules")
            {
                return true;
            }
        }

        return false;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "VectorNNTP.NNTPD.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate VectorNNTP.NNTPD.sln.");
    }
}
