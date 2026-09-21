using Microsoft.Extensions.Options;
using VectorNNTP.NNTPD.Configuration;

namespace VectorNNTP.NNTPD.Tests;

public sealed class NntpdOptionsValidatorTests
{
    private readonly NntpdOptionsValidator _validator = new();

    [Fact]
    public void Validate_Succeeds_ForDefaults()
    {
        var result = _validator.Validate(null, new NntpdOptions());
        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_Fails_ForEmptyApplicationName()
    {
        var result = _validator.Validate(null, new NntpdOptions { ApplicationName = " " });
        Assert.True(result.Failed);
    }

    [Fact]
    public void Validate_Fails_ForTooShortShutdownTimeout()
    {
        var result = _validator.Validate(
            null,
            new NntpdOptions { GracefulShutdownTimeout = TimeSpan.FromMilliseconds(100) });
        Assert.True(result.Failed);
    }

    [Fact]
    public void Validate_Fails_ForTooShortStartupTimeout()
    {
        var result = _validator.Validate(
            null,
            new NntpdOptions { StartupTimeout = TimeSpan.FromMilliseconds(10) });
        Assert.True(result.Failed);
    }

    [Fact]
    public void Validate_Fails_ForInvalidWatchdogIntervalFraction()
    {
        var result = _validator.Validate(
            null,
            new NntpdOptions
            {
                Systemd = new SystemdOptions { WatchdogIntervalFraction = 1.5 },
            });
        Assert.True(result.Failed);
    }
}
