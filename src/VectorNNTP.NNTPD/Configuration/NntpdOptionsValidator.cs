using Microsoft.Extensions.Options;
using VectorNNTP.NNTPD.Configuration;

namespace VectorNNTP.NNTPD.Configuration;

/// <summary>
/// Validates <see cref="NntpdOptions"/> at options bind / startup time.
/// </summary>
public sealed class NntpdOptionsValidator : IValidateOptions<NntpdOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, NntpdOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.ApplicationName))
        {
            failures.Add($"{nameof(NntpdOptions.ApplicationName)} must be a non-empty string.");
        }
        else if (options.ApplicationName.Length > 128)
        {
            failures.Add($"{nameof(NntpdOptions.ApplicationName)} must be 128 characters or fewer.");
        }

        if (options.GracefulShutdownTimeout < TimeSpan.FromSeconds(1))
        {
            failures.Add($"{nameof(NntpdOptions.GracefulShutdownTimeout)} must be at least 1 second.");
        }

        if (options.GracefulShutdownTimeout > TimeSpan.FromHours(1))
        {
            failures.Add($"{nameof(NntpdOptions.GracefulShutdownTimeout)} must not exceed 1 hour.");
        }

        if (options.StartupTimeout is { } startupTimeout)
        {
            if (startupTimeout < TimeSpan.FromSeconds(1))
            {
                failures.Add($"{nameof(NntpdOptions.StartupTimeout)} must be at least 1 second when specified.");
            }

            if (startupTimeout > TimeSpan.FromHours(1))
            {
                failures.Add($"{nameof(NntpdOptions.StartupTimeout)} must not exceed 1 hour.");
            }
        }

        if (options.Systemd is null)
        {
            failures.Add($"{nameof(NntpdOptions.Systemd)} must be provided.");
        }
        else
        {
            var fraction = options.Systemd.WatchdogIntervalFraction;
            if (double.IsNaN(fraction) || double.IsInfinity(fraction) || fraction is <= 0 or >= 1)
            {
                failures.Add(
                    $"{nameof(NntpdOptions.Systemd)}.{nameof(SystemdOptions.WatchdogIntervalFraction)} must be in the open interval (0, 1).");
            }
            else if (fraction is < 0.05 or > 0.9)
            {
                failures.Add(
                    $"{nameof(NntpdOptions.Systemd)}.{nameof(SystemdOptions.WatchdogIntervalFraction)} must be between 0.05 and 0.9 inclusive.");
            }
        }

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }
}
