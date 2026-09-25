using VectorNNTP.NNTPD.NntpDb;

namespace VectorNNTP.NNTPD.Tests.NntpDb;

public sealed class MySqlNntpDbConnectionTests
{
    [Fact]
    public void ConvertSelectOneScalar_AcceptsInt32()
    {
        Assert.Equal(1, MySqlNntpDbConnection.ConvertSelectOneScalar(1));
    }

    [Fact]
    public void ConvertSelectOneScalar_AcceptsMySqlInt64()
    {
        Assert.Equal(1, MySqlNntpDbConnection.ConvertSelectOneScalar(1L));
    }
}
