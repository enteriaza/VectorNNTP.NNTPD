using VectorNNTP.BackFiller.Accounts;

namespace VectorNNTP.BackFiller.Tests.Fixtures;

internal static class ProviderAccountTestRows
{
    internal const string SecretPassword = "account-password-secret-xyz";

    internal static ProviderAccountRow Create(
        string backbone = "Giganews",
        string hostname = "news.example.test",
        int port = 563,
        int maxConnections = 4,
        string username = "nntp-user",
        string? password = SecretPassword,
        string useSsl = "y",
        int serverId = 1,
        byte keepAlive = 0)
    {
        return new ProviderAccountRow(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            backbone,
            hostname,
            keepAlive,
            maxConnections,
            username,
            password!,
            port,
            serverId,
            useSsl);
    }
}
