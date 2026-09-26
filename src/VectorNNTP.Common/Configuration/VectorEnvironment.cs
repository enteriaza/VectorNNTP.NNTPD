using Microsoft.Extensions.Configuration;

namespace VectorNNTP.NNTPD.Configuration;

/// <summary>
/// Shared VectorNNTP environment-variable contract.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Prefix"/> is only for shared or common infrastructure
/// (ACME, Cloudflare, bind addresses, RabbitMQ, and standard
/// <c>ConnectionStrings</c>). Application-specific settings stay in the
/// application configuration section and use that application's environment
/// namespace. Nested configuration sections use <c>__</c>. Values are
/// case-sensitive and must be passed through unchanged.
/// </para>
/// <para>
/// Example: <c>CloudFlareZoneId</c> → <c>VECTOR__CLOUDFLAREZONEID</c>;
/// <c>RabbitMQ:Username</c> → <c>VECTOR__RABBITMQ__USERNAME</c>.
/// </para>
/// </remarks>
public static class VectorEnvironment
{
    /// <summary>Canonical environment-variable prefix, including the trailing separator.</summary>
    public const string Prefix = "VECTOR__";

    /// <summary>
    /// Builds the canonical uppercase environment-variable name for a configuration key path.
    /// </summary>
    /// <param name="configurationKeys">Configuration key segments in declaration order (for example <c>RabbitMQ</c>, <c>Username</c>).</param>
    /// <returns>The <c>VECTOR__</c> name, for example <c>VECTOR__RABBITMQ__USERNAME</c>.</returns>
    public static string Variable(params string[] configurationKeys)
    {
        ArgumentNullException.ThrowIfNull(configurationKeys);
        if (configurationKeys.Length == 0)
        {
            throw new ArgumentException("At least one configuration key is required.", nameof(configurationKeys));
        }

        var suffix = string.Join("__", configurationKeys.Select(static key =>
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new ArgumentException("Configuration key segments must be non-empty.", nameof(configurationKeys));
            }

            return key.Trim().ToUpperInvariant();
        }));

        return Prefix + suffix;
    }

    /// <summary>
    /// Returns whether <paramref name="environmentVariableName"/> is a canonical VectorNNTP name.
    /// </summary>
    /// <param name="environmentVariableName">Candidate environment-variable name.</param>
    /// <returns><see langword="true"/> when the name is uppercase and uses <see cref="Prefix"/>.</returns>
    public static bool IsCanonicalName(string? environmentVariableName)
    {
        if (string.IsNullOrWhiteSpace(environmentVariableName))
        {
            return false;
        }

        return environmentVariableName.StartsWith(Prefix, StringComparison.Ordinal)
               && environmentVariableName.Equals(environmentVariableName.ToUpperInvariant(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Registers the canonical <see cref="Prefix"/> environment-variable source.
    /// </summary>
    /// <param name="builder">The configuration builder.</param>
    /// <returns>The same <paramref name="builder"/> instance.</returns>
    /// <remarks>
    /// After the prefix is stripped, <c>VECTOR__RABBITMQ__USERNAME</c> binds to
    /// <c>RabbitMQ:Username</c>. Obsolete <c>nntpd__</c> and <c>backfiller__</c>
    /// prefixes are not registered and are not aliases.
    /// </remarks>
    public static IConfigurationBuilder AddVectorEnvironmentVariables(this IConfigurationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddEnvironmentVariables(prefix: Prefix);
    }
}
