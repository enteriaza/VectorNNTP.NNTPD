using VectorNNTP.BackFiller.Configuration;
using VectorNNTP.BackFiller.Tests.Fixtures;

namespace VectorNNTP.BackFiller.Tests.Configuration;

[Collection("WorkingDirectory")]
public sealed class BackFillerRuntimeOptionsFactoryTests
{
    [Fact]
    public void Create_projects_normalized_identity_and_paths()
    {
        var options = BackFillerTestOptions.CreateValid();
        options.DnsSuffix = "Usenet.Ninja.";
        options.RabbitMQ.Hosts = [" 127.0.0.1 ", "127.0.0.1"];

        var runtime = BackFillerRuntimeOptionsFactory.Create(
            options,
            BackFillerTestOptions.CreateValidNntpDb());

        Assert.Equal("usenet.ninja", runtime.DnsSuffix);
        Assert.Equal("backfiller01.usenet.ninja", runtime.Fqdn);
        Assert.Equal(1, runtime.ServerId);
        Assert.Equal(1190, runtime.BindPortTls);
        Assert.Equal(["127.0.0.1"], runtime.RabbitMq.Hosts);
        Assert.True(Path.IsPathRooted(runtime.LogDirectory));
        Assert.True(Path.IsPathRooted(runtime.CertificateDirectory));
        Assert.Equal("127.0.0.1", runtime.NntpDb.Server);
        Assert.Equal("nntp", runtime.NntpDb.Database);
        Assert.Equal(TimeSpan.FromSeconds(30), runtime.Shutdown.GracePeriod);
        Assert.True(runtime.Shutdown.DrainQueuedWork);
        Assert.True(runtime.Shutdown.FinishActiveArticles);
        options.RabbitMQ.Hosts = ["203.0.113.10"];
        Assert.Equal("backfiller01.usenet.ninja", runtime.Fqdn);
        Assert.Equal(["127.0.0.1"], runtime.RabbitMq.Hosts);
        Assert.Equal(4L * 1024 * 1024 * 1024, runtime.ArticleRetention.MaximumRetainedPayloadBytes);
        Assert.Equal(TimeSpan.FromSeconds(60), runtime.AccountRefreshInterval);
        options.BackFillerAccountRefreshIntervalSeconds = 90;
        Assert.Equal(TimeSpan.FromSeconds(60), runtime.AccountRefreshInterval);
    }

    [Fact]
    public void Runtime_certificate_names_are_only_the_backfiller_fqdn()
    {
        var runtime = BackFillerRuntimeOptionsFactory.Create(
            BackFillerTestOptions.CreateValid(),
            BackFillerTestOptions.CreateValidNntpDb(),
            BackFillerTestOptions.CreateValidAcme());

        Assert.Equal(["backfiller01.usenet.ninja"], runtime.CertificateDomainNames);
        Assert.DoesNotContain("news.usenet.ninja", runtime.CertificateDomainNames);
    }

    [Fact]
    public void Runtime_options_are_immutable_records()
    {
        var runtime = BackFillerRuntimeOptionsFactory.Create(
            BackFillerTestOptions.CreateValid(),
            BackFillerTestOptions.CreateValidNntpDb());

        Assert.True(runtime.GetType().IsSealed);
        var withFqdn = runtime with { Fqdn = "other.example" };
        Assert.NotSame(runtime, withFqdn);
        Assert.Equal("backfiller01.usenet.ninja", runtime.Fqdn);
        Assert.Equal("other.example", withFqdn.Fqdn);
        Assert.Null(runtime.GetType().GetProperty("Name"));
    }

    [Fact]
    public void Shutdown_flags_are_snapshotted_independently_and_do_not_follow_later_option_mutation()
    {
        var options = BackFillerTestOptions.CreateValid();
        options.Shutdown.DrainQueuedWork = false;
        options.Shutdown.FinishActiveArticles = false;
        options.Shutdown.GracePeriodSeconds = 45;
        var runtime = BackFillerRuntimeOptionsFactory.Create(
            options,
            BackFillerTestOptions.CreateValidNntpDb());

        options.Shutdown.DrainQueuedWork = true;
        options.Shutdown.FinishActiveArticles = true;
        options.Shutdown.GracePeriodSeconds = 90;

        Assert.False(runtime.Shutdown.DrainQueuedWork);
        Assert.False(runtime.Shutdown.FinishActiveArticles);
        Assert.Equal(TimeSpan.FromSeconds(45), runtime.Shutdown.GracePeriod);
    }

    [Fact]
    public void Relative_directories_resolve_against_the_content_root_not_the_working_directory()
    {
        var previous = Environment.CurrentDirectory;
        var contentRoot = Directory.CreateTempSubdirectory("bf-content-").FullName;
        var otherCwd = Directory.CreateTempSubdirectory("bf-cwd-").FullName;
        try
        {
            Environment.CurrentDirectory = otherCwd;
            var options = BackFillerTestOptions.CreateValid();
            options.LogDirectory = "logs";
            options.CertificateDirectory = "certs";
            options.AcmeStateDir = "certs";
            var runtime = BackFillerRuntimeOptionsFactory.Create(
                options,
                BackFillerTestOptions.CreateValidNntpDb(),
                contentRoot);

            Assert.Equal(Path.GetFullPath(Path.Combine(contentRoot, "logs")), runtime.LogDirectory);
            Assert.Equal(Path.GetFullPath(Path.Combine(contentRoot, "certs")), runtime.CertificateDirectory);
            Assert.NotEqual(Path.GetFullPath(Path.Combine(otherCwd, "certs")), runtime.CertificateDirectory);
        }
        finally
        {
            Environment.CurrentDirectory = previous;
            TryDelete(contentRoot);
            TryDelete(otherCwd);
        }
    }

    [Fact]
    public void Absolute_directories_remain_absolute()
    {
        var contentRoot = Directory.CreateTempSubdirectory("bf-content-abs-").FullName;
        var absoluteLogs = Directory.CreateTempSubdirectory("bf-abs-logs-").FullName;
        var absoluteCerts = Directory.CreateTempSubdirectory("bf-abs-certs-").FullName;
        try
        {
            var options = BackFillerTestOptions.CreateValid();
            options.LogDirectory = absoluteLogs;
            options.CertificateDirectory = absoluteCerts;
            options.AcmeStateDir = absoluteCerts;
            var runtime = BackFillerRuntimeOptionsFactory.Create(
                options,
                BackFillerTestOptions.CreateValidNntpDb(),
                contentRoot);

            Assert.Equal(Path.GetFullPath(absoluteLogs), runtime.LogDirectory);
            Assert.Equal(Path.GetFullPath(absoluteCerts), runtime.CertificateDirectory);
        }
        finally
        {
            TryDelete(contentRoot);
            TryDelete(absoluteLogs);
            TryDelete(absoluteCerts);
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
        catch (UnauthorizedAccessException)
        {
        }
    }
}
