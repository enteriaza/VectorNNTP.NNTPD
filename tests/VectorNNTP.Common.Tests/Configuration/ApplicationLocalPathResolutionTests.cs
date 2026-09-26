using VectorNNTP.NNTPD.Configuration;

namespace VectorNNTP.Common.Tests.Configuration;

/// <summary>
/// Proves Common <see cref="ApplicationLocalPath.ResolveApplicationLocalPath"/>
/// and the ACME wrapper <see cref="AcmeCloudflareOptionsValidator.ResolveAcmeStateDir"/>.
/// </summary>
[Collection("WorkingDirectory")]
public sealed class ApplicationLocalPathResolutionTests
{
    [Fact]
    public void Relative_path_resolves_beneath_supplied_application_base()
    {
        var baseDir = Directory.CreateTempSubdirectory("app-base-").FullName;
        try
        {
            var resolved = ApplicationLocalPath.ResolveApplicationLocalPath("certs/", baseDir);
            Assert.StartsWith(
                Path.GetFullPath(baseDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                resolved,
                StringComparison.OrdinalIgnoreCase);
            Assert.EndsWith("certs", resolved, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDelete(baseDir);
        }
    }

    [Fact]
    public void Relative_path_with_omitted_base_uses_appcontext_base_directory()
    {
        var resolved = ApplicationLocalPath.ResolveApplicationLocalPath("certs/", applicationBaseDirectory: null);
        Assert.Equal(
            ApplicationLocalPath.ResolveApplicationLocalPath("certs/", AppContext.BaseDirectory),
            resolved);
        Assert.StartsWith(
            Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            resolved,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Absolute_path_remains_absolute()
    {
        var absolute = Directory.CreateTempSubdirectory("app-abs-").FullName;
        var unusedBase = Directory.CreateTempSubdirectory("app-unused-").FullName;
        try
        {
            var resolved = ApplicationLocalPath.ResolveApplicationLocalPath(absolute, unusedBase);
            Assert.Equal(
                Path.GetFullPath(absolute).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                resolved);
            Assert.DoesNotContain(
                Path.GetFullPath(unusedBase).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                resolved,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDelete(absolute);
            TryDelete(unusedBase);
        }
    }

    [Fact]
    public void Foreign_cwd_cannot_influence_the_result()
    {
        var previous = Environment.CurrentDirectory;
        var baseDir = Directory.CreateTempSubdirectory("app-base-cwd-").FullName;
        var cwd = Directory.CreateTempSubdirectory("app-cwd-").FullName;
        try
        {
            Environment.CurrentDirectory = cwd;
            var resolved = ApplicationLocalPath.ResolveApplicationLocalPath("logs/", baseDir);
            Assert.StartsWith(
                Path.GetFullPath(baseDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                resolved,
                StringComparison.OrdinalIgnoreCase);
            Assert.NotEqual(Path.GetFullPath("logs/"), resolved);
            Assert.DoesNotContain(
                Path.GetFullPath(cwd).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                resolved,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.CurrentDirectory = previous;
            TryDelete(baseDir);
            TryDelete(cwd);
        }
    }

    [Fact]
    public void Decoy_ide_content_root_cannot_influence_when_base_is_appcontext()
    {
        var previous = Environment.CurrentDirectory;
        var decoyContentRoot = Directory.CreateTempSubdirectory("app-decoy-content-").FullName;
        var cwd = Directory.CreateTempSubdirectory("app-decoy-cwd-").FullName;
        try
        {
            Environment.CurrentDirectory = cwd;
            var resolved = ApplicationLocalPath.ResolveApplicationLocalPath("certs/", AppContext.BaseDirectory);
            Assert.Equal(
                ApplicationLocalPath.ResolveApplicationLocalPath("certs/", AppContext.BaseDirectory),
                resolved);
            Assert.DoesNotContain(
                Path.GetFullPath(decoyContentRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                resolved,
                StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                Path.GetFullPath(cwd).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                resolved,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.CurrentDirectory = previous;
            TryDelete(decoyContentRoot);
            TryDelete(cwd);
        }
    }

    [Fact]
    public void Normalization_is_deterministic_and_strips_trailing_separators()
    {
        var baseDir = Directory.CreateTempSubdirectory("app-norm-").FullName;
        try
        {
            var first = ApplicationLocalPath.ResolveApplicationLocalPath("certs/", baseDir);
            var second = ApplicationLocalPath.ResolveApplicationLocalPath("certs", baseDir);
            var third = ApplicationLocalPath.ResolveApplicationLocalPath("  certs/  ", baseDir);
            Assert.Equal(first, second);
            Assert.Equal(first, third);
            Assert.False(first.EndsWith(Path.DirectorySeparatorChar));
            Assert.False(first.EndsWith(Path.AltDirectorySeparatorChar));
        }
        finally
        {
            TryDelete(baseDir);
        }
    }

    [Fact]
    public void Null_or_whitespace_path_is_rejected()
    {
        Assert.Throws<ArgumentNullException>(static () =>
            ApplicationLocalPath.ResolveApplicationLocalPath(null!, AppContext.BaseDirectory));
        Assert.Throws<ArgumentException>(static () =>
            ApplicationLocalPath.ResolveApplicationLocalPath("   ", AppContext.BaseDirectory));
    }

    [Fact]
    public void Relative_path_algorithm_exists_only_in_application_local_path()
    {
        var helper = File.ReadAllText(FindRepoFile(
            Path.Combine("src", "VectorNNTP.Common", "Configuration", "ApplicationLocalPath.cs")));
        var acmeWrapper = File.ReadAllText(FindRepoFile(
            Path.Combine("src", "VectorNNTP.Common", "Configuration", "AcmeCloudflareOptionsValidator.cs")));
        Assert.Contains("Path.GetFullPath(trimmed, Path.GetFullPath(root))", helper, StringComparison.Ordinal);
        Assert.Contains("ApplicationLocalPath.ResolveApplicationLocalPath", acmeWrapper, StringComparison.Ordinal);
        Assert.DoesNotContain("Path.GetFullPath(trimmed, Path.GetFullPath(root))", acmeWrapper, StringComparison.Ordinal);
    }

    [Fact]
    public void Acme_wrapper_delegates_to_the_generic_resolver()
    {
        var baseDir = Directory.CreateTempSubdirectory("app-acme-wrap-").FullName;
        try
        {
            Assert.Equal(
                ApplicationLocalPath.ResolveApplicationLocalPath("certs/", baseDir),
                AcmeCloudflareOptionsValidator.ResolveAcmeStateDir("certs/", baseDir));
        }
        finally
        {
            TryDelete(baseDir);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static string FindRepoFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not locate {relativePath}.");
    }
}
