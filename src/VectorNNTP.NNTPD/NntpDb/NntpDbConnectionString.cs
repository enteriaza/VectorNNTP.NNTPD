using MySqlConnector;
using VectorNNTP.NNTPD.Configuration;

namespace VectorNNTP.NNTPD.NntpDb;

/// <summary>
/// Validates <c>ConnectionStrings:NntpDB</c> with MySqlConnector's parser.
/// </summary>
/// <remarks>
/// Does not implement connection-string parsing rules and never logs the string.
/// </remarks>
internal static class NntpDbConnectionString
{
    /// <summary>
    /// Verifies that <paramref name="connectionString"/> is accepted by
    /// <see cref="MySqlConnectionStringBuilder"/>.
    /// </summary>
    /// <param name="connectionString">Configured <c>ConnectionStrings:NntpDB</c> value.</param>
    /// <exception cref="NntpDbConfigurationException">
    /// The value is missing or rejected by MySqlConnector.
    /// </exception>
    public static void Validate(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new NntpDbConfigurationException(
                $"ConnectionStrings:{NntpDbOptions.ConnectionStringName} must be configured.");
        }

        try
        {
            _ = new MySqlConnectionStringBuilder(connectionString);
        }
        catch (ArgumentException ex)
        {
            throw new NntpDbConfigurationException(ex.Message, ex);
        }
    }
}
