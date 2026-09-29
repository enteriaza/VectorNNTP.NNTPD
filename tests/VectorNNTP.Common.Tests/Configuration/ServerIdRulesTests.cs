using VectorNNTP.NNTPD.Configuration;

namespace VectorNNTP.Common.Tests.Configuration;

/// <summary>
/// Locks the shared ServerId required/range matrix.
/// </summary>
public sealed class ServerIdRulesTests
{
    [Fact]
    public void Bounds_are_one_through_two_hundred_fifty_five()
    {
        Assert.Equal(1, ServerIdRules.MinimumInclusive);
        Assert.Equal(255, ServerIdRules.MaximumInclusive);
    }

    [Fact]
    public void Classify_treats_missing_as_distinct_from_zero()
    {
        Assert.True(ServerIdRules.IsMissing(null));
        Assert.False(ServerIdRules.IsMissing(0));
        Assert.Equal(ServerIdValidationStatus.Missing, ServerIdRules.Classify(null));
        Assert.Equal(ServerIdValidationStatus.OutOfRange, ServerIdRules.Classify(0));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(50)]
    [InlineData(99)]
    [InlineData(100)]
    [InlineData(255)]
    public void Classify_accepts_inclusive_range(int serverId)
    {
        Assert.True(ServerIdRules.IsInRange(serverId));
        Assert.Equal(ServerIdValidationStatus.Valid, ServerIdRules.Classify(serverId));
        Assert.Null(ServerIdRules.Validate(serverId, "ServerId"));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(256)]
    [InlineData(257)]
    [InlineData(int.MaxValue)]
    public void Classify_rejects_out_of_range(int serverId)
    {
        Assert.False(ServerIdRules.IsInRange(serverId));
        Assert.Equal(ServerIdValidationStatus.OutOfRange, ServerIdRules.Classify(serverId));
        var failure = ServerIdRules.Validate(serverId, "ServerId");
        Assert.Contains("1–255", failure, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_missing_names_only_the_caller_supplied_key()
    {
        var failure = ServerIdRules.Validate(null, "ServerId");
        Assert.Contains("required", failure, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ServerId", failure, StringComparison.Ordinal);
        Assert.DoesNotContain("BACKFILLER__", failure, StringComparison.Ordinal);
        Assert.DoesNotContain("NNTPD__", failure, StringComparison.Ordinal);
        Assert.DoesNotContain("VECTOR__", failure, StringComparison.Ordinal);
    }
}
