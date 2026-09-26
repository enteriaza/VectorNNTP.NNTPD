using MySqlConnector;

namespace VectorNNTP.BackFiller.Configuration;

/// <summary>
/// Parses and validates <c>ConnectionStrings:GrabberDB</c> without connecting.
/// </summary>
/// <remarks>
/// Failures never include the raw connection string or password.
/// </remarks>
public static class GrabberDbConnectionString
{
    /// <summary>Configuration key.</summary>
    public const string ConfigurationKey = "ConnectionStrings:GrabberDB";

    /// <summary>
    /// Attempts to parse a MySQL control-plane connection string.
    /// </summary>
    /// <param name="connectionString">Configured value.</param>
    /// <param name="server">Parsed server host when successful.</param>
    /// <param name="database">Parsed database name when successful.</param>
    /// <param name="userId">Parsed user id when successful.</param>
    /// <param name="reason">Failure reason that does not contain secrets.</param>
    /// <returns><see langword="true"/> when required components are present.</returns>
    public static bool TryParse(
        string? connectionString,
        out string? server,
        out string? database,
        out string? userId,
        out string reason)
    {
        server = null;
        database = null;
        userId = null;
        reason = string.Empty;

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            reason = $"{ConfigurationKey} is required and cannot be empty.";
            return false;
        }

        MySqlConnectionStringBuilder builder;
        try
        {
            builder = new MySqlConnectionStringBuilder(connectionString);
        }
        catch (ArgumentException)
        {
            reason = $"{ConfigurationKey} has invalid MySQL connection-string syntax.";
            return false;
        }
        catch (FormatException)
        {
            reason = $"{ConfigurationKey} has invalid MySQL connection-string syntax.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(builder.Server))
        {
            reason = $"{ConfigurationKey} must include a server/host.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(builder.Database))
        {
            reason = $"{ConfigurationKey} must include a database name.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(builder.UserID))
        {
            reason = $"{ConfigurationKey} must include a User ID.";
            return false;
        }

        server = builder.Server.Trim();
        database = builder.Database.Trim();
        userId = builder.UserID.Trim();
        return true;
    }
}
