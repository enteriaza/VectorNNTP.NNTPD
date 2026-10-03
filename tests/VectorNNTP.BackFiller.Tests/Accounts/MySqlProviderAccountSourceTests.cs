using VectorNNTP.BackFiller.Accounts;
using VectorNNTP.BackFiller.Configuration;
using VectorNNTP.BackFiller.Tests.Fixtures;

namespace VectorNNTP.BackFiller.Tests.Accounts
{
    public sealed class MySqlProviderAccountSourceTests
    {
        [Fact]
        public void Accounts_query_selects_mapped_columns_and_filters_by_server_id()
        {
            Assert.Equal("nntpbackfilleraccounts", MySqlProviderAccountSource.AccountsTableName);
            Assert.Equal(
                "SELECT backbone, hostname, keepalive, maxconnections, password, port, username, usessl FROM nntpbackfilleraccounts WHERE serverid = @ServerId;",
                MySqlProviderAccountSource.AccountsQuery);
            Assert.DoesNotContain("Password=", MySqlProviderAccountSource.AccountsQuery, StringComparison.OrdinalIgnoreCase);
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
        public void Source_does_not_set_an_application_command_timeout()
        {
            var source = File.ReadAllText(FindSource());
            Assert.DoesNotContain("CommandTimeout", source, StringComparison.Ordinal);
            Assert.DoesNotContain("CommandTimeoutSeconds", source, StringComparison.Ordinal);
        }

        [Fact]
        public void Source_stores_the_validated_connection_string_without_logging_it()
        {
            var runtime = BackFillerRuntimeOptionsFactory.Create(
                BackFillerTestOptions.CreateValid(),
                BackFillerTestOptions.CreateValidNntpDb());
            _ = new MySqlProviderAccountSource(runtime);
            Assert.Contains("Password=db-secret-xyz", runtime.NntpDb.ConnectionString, StringComparison.Ordinal);
        }

        private static string FindSource()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null)
            {
                var candidate = Path.Combine(
                    directory.FullName,
                    "src",
                    "VectorNNTP.BackFiller",
                    "Accounts",
                    "MySqlProviderAccountSource.cs");
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                directory = directory.Parent;
            }

            throw new DirectoryNotFoundException("Could not locate MySqlProviderAccountSource.cs.");
        }
    }
}
