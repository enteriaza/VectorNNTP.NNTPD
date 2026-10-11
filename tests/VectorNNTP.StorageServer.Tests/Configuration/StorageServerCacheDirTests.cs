using System.Reflection;
using Microsoft.Extensions.Configuration;
using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Tests.Fixtures;
using VectorNNTP.Common.Configuration;

namespace VectorNNTP.StorageServer.Tests.Configuration;

public sealed class StorageServerCacheDirTests
{
    [Fact]
    public void Default_spool_directory_is_spool_cache()
    {
        var storage = new ArticleStorageOptions();
        Assert.Equal("spool/cache", storage.SegmentPools.Directories[0].CacheDir);
        Assert.Equal(SegmentPoolDirectoryOptions.DefaultMaxCapacityBytes, storage.SegmentPools.Directories[0].MaxCapacity);
        Assert.Equal("spool/cache", new StorageServerOptions().Storage.SegmentPools.Directories[0].CacheDir);
    }

    [Fact]
    public void CreateValid_fixture_defaults_a_segment_pool_when_omitted()
    {
        var options = StorageServerTestOptions.CreateValid();
        Assert.Equal(SegmentPoolDirectoryOptions.DefaultCacheDir, options.Storage.SegmentPools.Directories[0].CacheDir);
        Assert.Equal(SegmentPoolDirectoryOptions.DefaultMaxCapacityBytes, options.Storage.SegmentPools.Directories[0].MaxCapacity);
    }

    [Fact]
    public void Configuration_binding_applies_nested_segment_pool_directory()
    {
        var configuration = StorageServerTestOptions.CreateValidConfiguration(
            new Dictionary<string, string?>
            {
                ["StorageServer:Storage:SegmentPools:Directories:0:CacheDir"] = "data/storage-cache",
                ["StorageServer:Storage:SegmentPools:Directories:0:MaxCapacity"] = "4096",
            });

        var bound = new StorageServerOptions();
        configuration.GetSection(StorageServerOptions.SectionName).Bind(bound);

        Assert.Equal("data/storage-cache", bound.Storage.SegmentPools.Directories[0].CacheDir);
        Assert.Equal(4096, bound.Storage.SegmentPools.Directories[0].MaxCapacity);
        Assert.True(ValidateOptionsResultSuccess(bound));
    }

    [Fact]
    public void Legacy_single_CacheDir_key_is_not_consumed()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["StorageServer:Storage:CacheDir"] = "legacy/cache",
                ["StorageServer:Storage:BulkPressure:WarningPercent"] = "1",
            })
            .Build();

        var bound = new StorageServerOptions();
        configuration.GetSection(StorageServerOptions.SectionName).Bind(bound);

        Assert.Equal(SegmentPoolDirectoryOptions.DefaultCacheDir, bound.Storage.SegmentPools.Directories[0].CacheDir);
        Assert.Equal(SegmentPoolOptions.DefaultWarningPercent, bound.Storage.SegmentPools.WarningPercent);
        Assert.Null(typeof(ArticleStorageOptions).GetProperty("CacheDir"));
        Assert.Null(typeof(ArticleStorageOptions).GetProperty("BulkPressure"));
    }

    [Fact]
    public void Runtime_options_resolve_relative_spool_directory_via_ApplicationLocalPath()
    {
        var options = StorageServerTestOptions.CreateValid();
        options.Storage.SegmentPools.Directories[0].CacheDir = SegmentPoolDirectoryOptions.DefaultCacheDir;

        var baseDir = Path.Combine(Path.GetTempPath(), "vectornntp-ss-cachedir-base", Guid.NewGuid().ToString("N"));
        var runtime = StorageServerRuntimeOptionsFactory.Create(options, baseDir);

        var expected = ApplicationLocalPath.ResolveApplicationLocalPath(
            SegmentPoolDirectoryOptions.DefaultCacheDir,
            baseDir);
        Assert.Equal(expected, runtime.CacheDir);
        Assert.Equal(expected, runtime.Storage.SegmentDir);
        Assert.Equal(expected, runtime.Storage.SegmentPools![0].Directory);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(baseDir, "spool", "cache")),
            runtime.CacheDir);
        Assert.False(Directory.Exists(runtime.CacheDir));
        Assert.NotEqual(Path.GetFullPath(baseDir), runtime.CacheDir);
    }

    [Fact]
    public void Runtime_options_preserve_absolute_spool_directory()
    {
        var absolute = Path.Combine(Path.GetTempPath(), "vectornntp-ss-cachedir-abs", Guid.NewGuid().ToString("N"));
        var options = StorageServerTestOptions.CreateValid();
        options.Storage.SegmentPools.Directories[0].CacheDir = absolute;

        var runtime = StorageServerRuntimeOptionsFactory.Create(options);
        Assert.Equal(Path.GetFullPath(absolute), runtime.CacheDir);
        Assert.Equal(Path.GetFullPath(absolute), runtime.Storage.SegmentPools![0].Directory);
    }

    [Fact]
    public void Runtime_options_keep_LogDir_spool_directory_and_ControlDir_distinct()
    {
        var options = StorageServerTestOptions.CreateValid();
        options.LogDir = "/logs";
        options.Storage.SegmentPools.Directories[0].CacheDir = "spool/cache";
        options.Storage.ControlDir = "spool/";

        var baseDir = Path.Combine(Path.GetTempPath(), "vectornntp-ss-roots", Guid.NewGuid().ToString("N"));
        var runtime = StorageServerRuntimeOptionsFactory.Create(options, baseDir);

        Assert.Equal(
            ApplicationLocalPath.ResolveApplicationLocalPath("/logs", baseDir),
            runtime.LogDir);
        Assert.Equal(
            ApplicationLocalPath.ResolveApplicationLocalPath("spool/cache", baseDir),
            runtime.CacheDir);
        Assert.Equal(
            ApplicationLocalPath.ResolveApplicationLocalPath("spool/", baseDir),
            runtime.ControlDir);
        Assert.NotEqual(runtime.LogDir, runtime.CacheDir);
        Assert.NotEqual(runtime.ControlDir, runtime.CacheDir);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(baseDir, "spool")),
            runtime.ControlDir);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validator_rejects_null_empty_or_whitespace_spool_directory(string? cacheDir)
    {
        var options = StorageServerTestOptions.CreateValid();
        options.Storage.SegmentPools.Directories[0].CacheDir = cacheDir!;
        var result = new StorageServerOptionsValidator().Validate(null, options);
        Assert.True(result.Failed);
        Assert.Contains(
            result.Failures!,
            static f => f.Contains("StorageServer:Storage:SegmentPools:Directories:0:CacheDir", StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_accepts_valid_relative_and_absolute_spool_directory()
    {
        var relative = StorageServerTestOptions.CreateValid();
        relative.Storage.SegmentPools.Directories[0].CacheDir = "data/storage-cache";
        Assert.False(new StorageServerOptionsValidator().Validate(null, relative).Failed);

        var absolute = StorageServerTestOptions.CreateValid();
        absolute.Storage.SegmentPools.Directories[0].CacheDir = Path.Combine(Path.GetTempPath(), "vectornntp-ss-cachedir-ok");
        Assert.False(new StorageServerOptionsValidator().Validate(null, absolute).Failed);
    }

    [Fact]
    public void Validator_rejects_spool_directory_with_invalid_path_characters()
    {
        var invalidChars = Path.GetInvalidPathChars();
        Assert.NotEmpty(invalidChars);

        var options = StorageServerTestOptions.CreateValid();
        options.Storage.SegmentPools.Directories[0].CacheDir = "cache" + invalidChars[0] + "bad";
        var result = new StorageServerOptionsValidator().Validate(null, options);
        Assert.True(result.Failed);
        Assert.Contains(
            result.Failures!,
            static f => f.Contains("StorageServer:Storage:SegmentPools:Directories:0:CacheDir", StringComparison.Ordinal)
                        && f.Contains("invalid path characters", StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_rejects_duplicate_and_nested_spool_directories()
    {
        var duplicate = StorageServerTestOptions.CreateValid();
        var duplicatePath = duplicate.Storage.SegmentPools.Directories[0].CacheDir;
        duplicate.Storage.SegmentPools.Directories.Clear();
        duplicate.Storage.SegmentPools.Directories.Add(new SegmentPoolDirectoryOptions
        {
            CacheDir = duplicatePath,
            MaxCapacity = 1024,
        });
        duplicate.Storage.SegmentPools.Directories.Add(new SegmentPoolDirectoryOptions
        {
            CacheDir = duplicatePath,
            MaxCapacity = 2048,
        });
        var duplicateResult = new StorageServerOptionsValidator().Validate(null, duplicate);
        Assert.True(duplicateResult.Failed);
        Assert.Contains(
            duplicateResult.Failures!,
            static f => f.Contains("duplicate or nested", StringComparison.Ordinal));

        var nested = StorageServerTestOptions.CreateValid();
        var parent = Path.Combine(Path.GetTempPath(), "vectornntp-spool-parent");
        nested.Storage.SegmentPools.Directories.Clear();
        nested.Storage.SegmentPools.Directories.Add(new SegmentPoolDirectoryOptions
        {
            CacheDir = parent,
            MaxCapacity = 1024,
        });
        nested.Storage.SegmentPools.Directories.Add(new SegmentPoolDirectoryOptions
        {
            CacheDir = Path.Combine(parent, "child"),
            MaxCapacity = 1024,
        });
        var nestedResult = new StorageServerOptionsValidator().Validate(null, nested);
        Assert.True(nestedResult.Failed);
        Assert.Contains(
            nestedResult.Failures!,
            static f => f.Contains("duplicate or nested", StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_rejects_non_positive_spool_capacity()
    {
        var options = StorageServerTestOptions.CreateValid();
        options.Storage.SegmentPools.Directories[0].MaxCapacity = 0;
        var result = new StorageServerOptionsValidator().Validate(null, options);
        Assert.True(result.Failed);
        Assert.Contains(
            result.Failures!,
            static f => f.Contains("Directories:0:MaxCapacity", StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_allows_a_spool_directory_under_the_control_root()
    {
        var options = StorageServerTestOptions.CreateValid();
        options.Storage.ControlDir = "spool";
        options.Storage.SegmentPools.Directories[0].CacheDir = "spool/cache";
        Assert.False(new StorageServerOptionsValidator().Validate(null, options).Failed);
    }

    [Fact]
    public void Production_appsettings_declares_segment_pools()
    {
        var path = FindAppsettings();
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        var server = doc.RootElement.GetProperty("StorageServer");
        Assert.False(server.TryGetProperty("CacheDir", out _));
        Assert.Equal("logs", server.GetProperty("LogDir").GetString());
        var storage = server.GetProperty("Storage");
        Assert.False(storage.TryGetProperty("CacheDir", out _));
        Assert.False(storage.TryGetProperty("BulkPressure", out _));
        Assert.Equal("spool/", storage.GetProperty("ControlDir").GetString());
        var directory = storage.GetProperty("SegmentPools").GetProperty("Directories")[0];
        Assert.Equal("spool/cache", directory.GetProperty("CacheDir").GetString());
        Assert.Equal(53687091200, directory.GetProperty("MaxCapacity").GetInt64());
    }

    private static bool ValidateOptionsResultSuccess(StorageServerOptions options) =>
        !new StorageServerOptionsValidator().Validate(null, options).Failed;

    private static string FindAppsettings()
    {
        var start = new DirectoryInfo(AppContext.BaseDirectory);
        for (var dir = start; dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "VectorNNTP.StorageServer", "VectorNNTP.StorageServer.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("Could not locate StorageServer VectorNNTP.StorageServer.json.");
    }
}
