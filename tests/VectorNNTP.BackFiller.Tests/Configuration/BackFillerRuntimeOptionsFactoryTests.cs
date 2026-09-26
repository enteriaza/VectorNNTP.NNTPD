using VectorNNTP.BackFiller.Configuration;
using VectorNNTP.BackFiller.Tests.Fixtures;

namespace VectorNNTP.BackFiller.Tests.Configuration;

public sealed class BackFillerRuntimeOptionsFactoryTests
{
    [Fact]
    public void Create_projects_normalized_identity_and_paths()
    {
        var options = BackFillerTestOptions.CreateValid();
        options.Name = " BackFiller ";
        options.DnsSuffix = "Usenet.Ninja.";
        options.RabbitMQ.Hosts = [" 127.0.0.1 ", "127.0.0.1"];

        var runtime = BackFillerRuntimeOptionsFactory.Create(
            options,
            BackFillerTestOptions.CreateValidConnectionStrings());

        Assert.Equal("backfiller", runtime.Name);
        Assert.Equal("usenet.ninja", runtime.DnsSuffix);
        Assert.Equal("backfiller01.usenet.ninja", runtime.Fqdn);
        Assert.Equal(1, runtime.ServerId);
        Assert.Equal(1190, runtime.BindPort);
        Assert.Equal(["127.0.0.1"], runtime.RabbitMq.Hosts);
        Assert.True(Path.IsPathRooted(runtime.LogDirectory));
        Assert.True(Path.IsPathRooted(runtime.CertificateDirectory));
        Assert.Equal("127.0.0.1", runtime.GrabberDb.Server);
        Assert.Equal("nntp", runtime.GrabberDb.Database);
        Assert.Equal(TimeSpan.FromSeconds(30), runtime.Shutdown.GracePeriod);
        options.Name = "mutated-after-snapshot";
        options.RabbitMQ.Hosts = ["203.0.113.10"];
        Assert.Equal("backfiller", runtime.Name);
        Assert.Equal(["127.0.0.1"], runtime.RabbitMq.Hosts);
        Assert.Equal(4L * 1024 * 1024 * 1024, runtime.ArticleRetention.MaximumRetainedPayloadBytes);
        Assert.Equal(TimeSpan.FromSeconds(60), runtime.Accounts.RefreshInterval);
        Assert.Equal(TimeSpan.FromSeconds(15), runtime.Accounts.CommandTimeout);
    }

    [Fact]
    public void Runtime_options_are_immutable_records()
    {
        var runtime = BackFillerRuntimeOptionsFactory.Create(
            BackFillerTestOptions.CreateValid(),
            BackFillerTestOptions.CreateValidConnectionStrings());

        Assert.True(runtime.GetType().IsSealed);
        var withName = runtime with { Name = "other" };
        Assert.NotSame(runtime, withName);
        Assert.Equal("backfiller", runtime.Name);
        Assert.Equal("other", withName.Name);
    }
}
