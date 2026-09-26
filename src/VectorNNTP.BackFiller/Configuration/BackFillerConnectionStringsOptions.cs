using Microsoft.Extensions.Options;

namespace VectorNNTP.BackFiller.Configuration;

/// <summary>
/// Bindable connection-string options under the standard <c>ConnectionStrings</c> section.
/// </summary>
/// <remarks>
/// GrabberDB remains a top-level <c>ConnectionStrings</c> key (same section as the old worker).
/// The canonical prefixed environment variable is
/// <see cref="BackFillerOptions.GrabberDbEnvironmentVariable"/>.
/// Never log <see cref="GrabberDB"/>.
/// </remarks>
public sealed class BackFillerConnectionStringsOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "ConnectionStrings";

    /// <summary>Control-plane MySQL connection string. Secret.</summary>
    public string? GrabberDB { get; set; }
}

/// <summary>
/// Validates <see cref="BackFillerConnectionStringsOptions"/> at bind / startup time.
/// </summary>
/// <remarks>Does not open a database connection. Failure messages never include the raw string.</remarks>
public sealed class BackFillerConnectionStringsOptionsValidator : IValidateOptions<BackFillerConnectionStringsOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, BackFillerConnectionStringsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!GrabberDbConnectionString.TryParse(options.GrabberDB, out _, out _, out _, out var reason))
        {
            return ValidateOptionsResult.Fail(reason);
        }

        return ValidateOptionsResult.Success;
    }
}
