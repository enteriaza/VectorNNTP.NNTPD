using Microsoft.Extensions.Configuration;
using VectorNNTP.NNTPD.Configuration;
using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Tests.Fixtures;

namespace VectorNNTP.StorageServer.Tests.Configuration;

public sealed class StorageServerCacheDirTests
{
    [Fact]
    public void Default_CacheDir_is_cache_slash()
    {
        Assert.Equal("cache/", StorageServerOptions.DefaultCacheDir);
        Assert.Equal("cache/", new StorageServerOptions().CacheDir);
    }

    [Fact]
    public void CreateValid_fixture_defaults_CacheDir_when_omitted()
    {
        var options = StorageServerTestOptions.CreateValid();
        Assert.Equal(StorageServerOptions.DefaultCacheDir, options.CacheDir);
    }

    [Fact]
    public void Configuration_binding_applies_configured_CacheDir()
    {
        var configuration = StorageServerTestOptions.CreateValidConfiguration(
            new Dictionary<string, string?>
            {
                ["StorageServer:CacheDir"] = "data/storage-cache",
            });

        var bound = new StorageServerOptions();
        configuration.GetSection(StorageServerOptions.SectionName).Bind(bound);

        Assert.Equal("data/storage-cache", bound.CacheDir);
        Assert.True(ValidateOptionsResultSuccess(bound));
    }

    [Fact]
    public void Runtime_options_resolve_relative_CacheDir_via_ApplicationLocalPath()
    {
        var options = StorageServerTestOptions.CreateValid();
        options.CacheDir = StorageServerOptions.DefaultCacheDir;

        var baseDir = Path.Combine(Path.GetTempPath(), "vectornntp-ss-cachedir-base", Guid.NewGuid().ToString("N"));
        var runtime = StorageServerRuntimeOptionsFactory.Create(options, baseDir);

        var expected = ApplicationLocalPath.ResolveApplicationLocalPath(
            StorageServerOptions.DefaultCacheDir,
            baseDir);
        Assert.Equal(expected, runtime.CacheDir);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(baseDir, "cache")),
            runtime.CacheDir);
        Assert.False(Directory.Exists(runtime.CacheDir));
    }

    [Fact]
    public void Runtime_options_preserve_absolute_CacheDir()
    {
        var absolute = Path.Combine(Path.GetTempPath(), "vectornntp-ss-cachedir-abs", Guid.NewGuid().ToString("N"));
        var options = StorageServerTestOptions.CreateValid();
        options.CacheDir = absolute;

        var runtime = StorageServerRuntimeOptionsFactory.Create(options);
        Assert.Equal(Path.GetFullPath(absolute), runtime.CacheDir);
    }

    [Fact]
    public void Runtime_options_keep_LogDir_and_CacheDir_distinct()
    {
        var options = StorageServerTestOptions.CreateValid();
        options.LogDir = "/logs";
        options.CacheDir = "cache/";

        var baseDir = Path.Combine(Path.GetTempPath(), "vectornntp-ss-roots", Guid.NewGuid().ToString("N"));
        var runtime = StorageServerRuntimeOptionsFactory.Create(options, baseDir);

        Assert.Equal(
            ApplicationLocalPath.ResolveApplicationLocalPath("/logs", baseDir),
            runtime.LogDir);
        Assert.Equal(
            ApplicationLocalPath.ResolveApplicationLocalPath("cache/", baseDir),
            runtime.CacheDir);
        Assert.NotEqual(runtime.LogDir, runtime.CacheDir);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validator_rejects_null_empty_or_whitespace_CacheDir(string? cacheDir)
    {
        var options = StorageServerTestOptions.CreateValid();
        options.CacheDir = cacheDir!;
        var result = new StorageServerOptionsValidator().Validate(null, options);
        Assert.True(result.Failed);
        Assert.Contains(
            result.Failures!,
            static f => f.Contains("StorageServer:CacheDir", StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_accepts_valid_relative_and_absolute_CacheDir()
    {
        var relative = StorageServerTestOptions.CreateValid();
        relative.CacheDir = "data/storage-cache";
        Assert.False(new StorageServerOptionsValidator().Validate(null, relative).Failed);

        var absolute = StorageServerTestOptions.CreateValid();
        absolute.CacheDir = Path.Combine(Path.GetTempPath(), "vectornntp-ss-cachedir-ok");
        Assert.False(new StorageServerOptionsValidator().Validate(null, absolute).Failed);
    }

    [Fact]
    public void Validator_rejects_CacheDir_with_invalid_path_characters()
    {
        var invalidChars = Path.GetInvalidPathChars();
        Assert.NotEmpty(invalidChars);

        var options = StorageServerTestOptions.CreateValid();
        options.CacheDir = "cache" + invalidChars[0] + "bad";
        var result = new StorageServerOptionsValidator().Validate(null, options);
        Assert.True(result.Failed);
        Assert.Contains(
            result.Failures!,
            static f => f.Contains("StorageServer:CacheDir", StringComparison.Ordinal)
                        && f.Contains("invalid path characters", StringComparison.Ordinal));
    }

    [Fact]
    public void Production_appsettings_declares_CacheDir_default()
    {
        var path = FindAppsettings();
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        var storage = doc.RootElement.GetProperty("StorageServer");
        Assert.Equal("cache/", storage.GetProperty("CacheDir").GetString());
        Assert.Equal("/logs", storage.GetProperty("LogDir").GetString());
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
