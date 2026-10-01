using Microsoft.Extensions.Configuration;
using VectorNNTP.NNTPD.Configuration;
using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Tests.Fixtures;

namespace VectorNNTP.StorageServer.Tests.Configuration;

public sealed class StorageServerCacheDirTests
{
    [Fact]
    public void Default_CacheDir_is_spool_cache()
    {
        Assert.Equal("spool/cache", ArticleStorageOptions.DefaultCacheDir);
        Assert.Equal("spool/cache", new ArticleStorageOptions().CacheDir);
        Assert.Equal("spool/cache", new StorageServerOptions().Storage.CacheDir);
    }

    [Fact]
    public void CreateValid_fixture_defaults_CacheDir_when_omitted()
    {
        var options = StorageServerTestOptions.CreateValid();
        Assert.Equal(ArticleStorageOptions.DefaultCacheDir, options.Storage.CacheDir);
    }

    [Fact]
    public void Configuration_binding_applies_nested_CacheDir()
    {
        var configuration = StorageServerTestOptions.CreateValidConfiguration(
            new Dictionary<string, string?>
            {
                ["StorageServer:Storage:CacheDir"] = "data/storage-cache",
            });

        var bound = new StorageServerOptions();
        configuration.GetSection(StorageServerOptions.SectionName).Bind(bound);

        Assert.Equal("data/storage-cache", bound.Storage.CacheDir);
        Assert.True(ValidateOptionsResultSuccess(bound));
    }

    [Fact]
    public void Old_StorageServer_CacheDir_key_is_not_used()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["StorageServer:CacheDir"] = "legacy/cache",
            })
            .Build();

        var bound = new StorageServerOptions();
        configuration.GetSection(StorageServerOptions.SectionName).Bind(bound);

        Assert.Equal(ArticleStorageOptions.DefaultCacheDir, bound.Storage.CacheDir);
    }

    [Fact]
    public void Runtime_options_resolve_relative_CacheDir_via_ApplicationLocalPath()
    {
        var options = StorageServerTestOptions.CreateValid();
        options.Storage.CacheDir = ArticleStorageOptions.DefaultCacheDir;

        var baseDir = Path.Combine(Path.GetTempPath(), "vectornntp-ss-cachedir-base", Guid.NewGuid().ToString("N"));
        var runtime = StorageServerRuntimeOptionsFactory.Create(options, baseDir);

        var expected = ApplicationLocalPath.ResolveApplicationLocalPath(
            ArticleStorageOptions.DefaultCacheDir,
            baseDir);
        Assert.Equal(expected, runtime.CacheDir);
        Assert.Equal(expected, runtime.Storage.SegmentDir);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(baseDir, "spool", "cache")),
            runtime.CacheDir);
        Assert.False(Directory.Exists(runtime.CacheDir));
        Assert.NotEqual(Path.GetFullPath(baseDir), runtime.CacheDir);
    }

    [Fact]
    public void Runtime_options_preserve_absolute_CacheDir()
    {
        var absolute = Path.Combine(Path.GetTempPath(), "vectornntp-ss-cachedir-abs", Guid.NewGuid().ToString("N"));
        var options = StorageServerTestOptions.CreateValid();
        options.Storage.CacheDir = absolute;

        var runtime = StorageServerRuntimeOptionsFactory.Create(options);
        Assert.Equal(Path.GetFullPath(absolute), runtime.CacheDir);
    }

    [Fact]
    public void Runtime_options_keep_LogDir_CacheDir_and_ControlDir_distinct()
    {
        var options = StorageServerTestOptions.CreateValid();
        options.LogDir = "/logs";
        options.Storage.CacheDir = "spool/cache";
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
    public void Validator_rejects_null_empty_or_whitespace_CacheDir(string? cacheDir)
    {
        var options = StorageServerTestOptions.CreateValid();
        options.Storage.CacheDir = cacheDir!;
        var result = new StorageServerOptionsValidator().Validate(null, options);
        Assert.True(result.Failed);
        Assert.Contains(
            result.Failures!,
            static f => f.Contains("StorageServer:Storage:CacheDir", StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_accepts_valid_relative_and_absolute_CacheDir()
    {
        var relative = StorageServerTestOptions.CreateValid();
        relative.Storage.CacheDir = "data/storage-cache";
        Assert.False(new StorageServerOptionsValidator().Validate(null, relative).Failed);

        var absolute = StorageServerTestOptions.CreateValid();
        absolute.Storage.CacheDir = Path.Combine(Path.GetTempPath(), "vectornntp-ss-cachedir-ok");
        Assert.False(new StorageServerOptionsValidator().Validate(null, absolute).Failed);
    }

    [Fact]
    public void Validator_rejects_CacheDir_with_invalid_path_characters()
    {
        var invalidChars = Path.GetInvalidPathChars();
        Assert.NotEmpty(invalidChars);

        var options = StorageServerTestOptions.CreateValid();
        options.Storage.CacheDir = "cache" + invalidChars[0] + "bad";
        var result = new StorageServerOptionsValidator().Validate(null, options);
        Assert.True(result.Failed);
        Assert.Contains(
            result.Failures!,
            static f => f.Contains("StorageServer:Storage:CacheDir", StringComparison.Ordinal)
                        && f.Contains("invalid path characters", StringComparison.Ordinal));
    }

    [Fact]
    public void Production_appsettings_declares_nested_CacheDir()
    {
        var path = FindAppsettings();
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        var server = doc.RootElement.GetProperty("StorageServer");
        Assert.False(server.TryGetProperty("CacheDir", out _));
        Assert.Equal("logs", server.GetProperty("LogDir").GetString());
        var storage = server.GetProperty("Storage");
        Assert.Equal("spool/cache", storage.GetProperty("CacheDir").GetString());
        Assert.Equal("spool/", storage.GetProperty("ControlDir").GetString());
    }

    private static bool ValidateOptionsResultSuccess(StorageServerOptions options) =>
        !new StorageServerOptionsValidator().Validate(null, options).Failed;

    private static string FindAppsettings()
    {
        var start = new DirectoryInfo(AppContext.BaseDirectory);
        for (var dir = start; dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "VectorNNTP.StorageServer", "appsettings.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("Could not locate StorageServer appsettings.json.");
    }
}
