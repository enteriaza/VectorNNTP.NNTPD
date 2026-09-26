using VectorNNTP.BackFiller.Accounts;
using VectorNNTP.BackFiller.Configuration;
using VectorNNTP.BackFiller.Tests.Fixtures;

namespace VectorNNTP.BackFiller.Tests.Accounts;

public sealed class MySqlProviderAccountSourceTests
{
    [Fact]
    public void Accounts_query_matches_the_old_nntpbackfilleraccounts_contract()
    {
        Assert.Equal("nntpbackfilleraccounts", MySqlProviderAccountSource.AccountsTableName);
        Assert.Equal(
            "SELECT entryid, backbone, hostname, keepalive, maxconnections, password, port, serverid, username, usessl FROM nntpbackfilleraccounts WHERE serverid = @ServerId;",
            MySqlProviderAccountSource.AccountsQuery);
        Assert.DoesNotContain("Password=", MySqlProviderAccountSource.AccountsQuery, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseEntryId_accepts_guid_and_guid_string()
    {
        var guid = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        Assert.Equal(guid, MySqlProviderAccountSource.ParseEntryId(guid));
        Assert.Equal(guid, MySqlProviderAccountSource.ParseEntryId(guid.ToString()));
    }

    [Fact]
    public void ParseEntryId_rejects_non_guid_values()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => MySqlProviderAccountSource.ParseEntryId(42));
        Assert.DoesNotContain("db-secret-xyz", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(ProviderAccountTestRows.SecretPassword, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData((byte)7)]
    [InlineData((short)7)]
    [InlineData(7)]
    public void ParseKeepAlive_accepts_integer_shapes(object raw)
    {
        Assert.Equal((byte)7, MySqlProviderAccountSource.ParseKeepAlive(raw));
    }

    [Fact]
    public void ParseKeepAlive_rejects_null_and_non_integers()
    {
        Assert.Throws<InvalidOperationException>(() => MySqlProviderAccountSource.ParseKeepAlive("keep"));
        var ex = Assert.Throws<InvalidOperationException>(
            () => MySqlProviderAccountSource.ParseKeepAlive(DBNull.Value));
        Assert.DoesNotContain("db-secret-xyz", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Source_stores_the_validated_connection_string_without_logging_it()
    {
        var runtime = BackFillerRuntimeOptionsFactory.Create(
            BackFillerTestOptions.CreateValid(),
            BackFillerTestOptions.CreateValidConnectionStrings());
        _ = new MySqlProviderAccountSource(runtime);
        Assert.Contains("Password=db-secret-xyz", runtime.GrabberDb.ConnectionString, StringComparison.Ordinal);
    }
}
