using Microsoft.Extensions.Options;
using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Storage.Engine;
using VectorNNTP.StorageServer.Storage.Engine.Maintenance;
using VectorNNTP.StorageServer.Tests.Fixtures;

namespace VectorNNTP.StorageServer.Tests.Storage.Engine.Maintenance;

/// <summary>
/// Bulk cache-volume watermarks are a pure classification. They are not journal
/// <see cref="StorageWritePressure"/>.
/// </summary>
public sealed class BulkStoragePressurePolicyTests
{
    private const long Total = 10_000;

    [Theory]
    [InlineData(7_499, BulkStoragePressureState.Normal, 74)]
    [InlineData(7_500, BulkStoragePressureState.Warning, 75)]
    [InlineData(7_501, BulkStoragePressureState.Warning, 75)]
    [InlineData(7_999, BulkStoragePressureState.Warning, 79)]
    [InlineData(8_000, BulkStoragePressureState.Pressure, 80)]
    [InlineData(8_001, BulkStoragePressureState.Pressure, 80)]
    [InlineData(8_499, BulkStoragePressureState.Pressure, 84)]
    [InlineData(8_500, BulkStoragePressureState.High, 85)]
    [InlineData(8_501, BulkStoragePressureState.High, 85)]
    [InlineData(8_999, BulkStoragePressureState.High, 89)]
    [InlineData(9_000, BulkStoragePressureState.Critical, 90)]
    [InlineData(9_001, BulkStoragePressureState.Critical, 90)]
    [InlineData(9_499, BulkStoragePressureState.Critical, 94)]
    [InlineData(9_500, BulkStoragePressureState.Emergency, 95)]
    [InlineData(9_501, BulkStoragePressureState.Emergency, 95)]
    public void Used_percent_boundaries_classify_the_watermark(
        long used,
        BulkStoragePressureState expected,
        int usedPercent)
    {
        var evaluation = new BulkStoragePressurePolicy().Evaluate(Total, used, Total - used);

        Assert.Equal(expected, evaluation.State);
        Assert.Equal(usedPercent, evaluation.UsedPercent);
        Assert.Equal((int)((Total - used) * 100m / Total), evaluation.FreePercent);
        Assert.Equal(Total, evaluation.TotalBytes);
        Assert.Equal(used, evaluation.UsedBytes);
        Assert.Equal(Total - used, evaluation.FreeBytes);
        Assert.Equal(expected is BulkStoragePressureState.Normal or BulkStoragePressureState.Warning,
            evaluation.NormalMaintenanceSufficient);
        Assert.Equal(expected is BulkStoragePressureState.Pressure
                or BulkStoragePressureState.High
                or BulkStoragePressureState.Critical
                or BulkStoragePressureState.Emergency,
            evaluation.AccelerateReclamation);
        Assert.Equal(expected == BulkStoragePressureState.Emergency,
            evaluation.EmergencyAdmissionProtectionRequired);
    }

    [Fact]
    public void Defaults_match_the_documented_ladder()
    {
        var options = new BulkStoragePressureOptions();
        Assert.Equal(75, options.WarningPercent);
        Assert.Equal(80, options.PressurePercent);
        Assert.Equal(85, options.HighPercent);
        Assert.Equal(90, options.CriticalPercent);
        Assert.Equal(95, options.EmergencyPercent);
        Assert.Equal(5, options.OperationalReservePercent);
        Assert.Equal(5, options.RecoveryReservePercent);
        Assert.Equal(10, options.RewriteReservePercent);
        Assert.False(new StorageServerOptionsValidator().Validate(
            Options.DefaultName,
            StorageServerTestOptions.CreateValid()).Failed);
    }

    [Fact]
    public void Custom_increasing_thresholds_classify_with_that_ladder()
    {
        var options = StorageServerTestOptions.CreateValid();
        options.Storage.BulkPressure = new BulkStoragePressureOptions
        {
            WarningPercent = 10,
            PressurePercent = 20,
            HighPercent = 30,
            CriticalPercent = 40,
            EmergencyPercent = 50,
        };
        Assert.False(new StorageServerOptionsValidator().Validate(Options.DefaultName, options).Failed);

        var evaluation = new BulkStoragePressurePolicy(options.Storage.BulkPressure)
            .Evaluate(Total, 2_000, 8_000);
        Assert.Equal(BulkStoragePressureState.Pressure, evaluation.State);
    }

    [Theory]
    [InlineData(-1, 80, 85, 90, 95)]
    [InlineData(75, 101, 85, 90, 95)]
    [InlineData(80, 75, 85, 90, 95)]
    [InlineData(75, 75, 85, 90, 95)]
    public void Invalid_thresholds_fail_startup(
        int warning,
        int pressure,
        int high,
        int critical,
        int emergency)
    {
        var options = StorageServerTestOptions.CreateValid();
        options.Storage.BulkPressure.WarningPercent = warning;
        options.Storage.BulkPressure.PressurePercent = pressure;
        options.Storage.BulkPressure.HighPercent = high;
        options.Storage.BulkPressure.CriticalPercent = critical;
        options.Storage.BulkPressure.EmergencyPercent = emergency;

        var result = new StorageServerOptionsValidator().Validate(Options.DefaultName, options);
        Assert.True(result.Failed);
        Assert.Contains(
            result.Failures!,
            static failure => failure.Contains("BulkPressure", StringComparison.Ordinal));
    }

    [Fact]
    public void Reserve_bytes_shrink_as_free_space_crosses_the_watermarks()
    {
        var policy = new BulkStoragePressurePolicy();

        var normal = policy.Evaluate(Total, 5_000, 5_000);
        Assert.Equal(BulkStoragePressureState.Normal, normal.State);
        Assert.Equal(500, normal.OperationalReserveBytes);
        Assert.Equal(500, normal.RecoveryReserveBytes);
        Assert.Equal(1_000, normal.RewriteReserveBytes);
        Assert.Equal(3_000, normal.AvailableReserveBytes);
        Assert.False(normal.ReserveExhausted);
        Assert.True(normal.RewriteAllowed);

        var warning = policy.Evaluate(Total, 7_500, 2_500);
        Assert.Equal(BulkStoragePressureState.Warning, warning.State);
        Assert.Equal(500, warning.AvailableReserveBytes);
        Assert.False(warning.ReserveExhausted);
        Assert.Equal("AgeRetention", warning.MaintenanceMode);

        var pressure = policy.Evaluate(Total, 8_000, 2_000);
        Assert.Equal(BulkStoragePressureState.Pressure, pressure.State);
        Assert.Equal(0, pressure.AvailableReserveBytes);
        Assert.True(pressure.ReserveExhausted);
        Assert.Equal("PrioritizeReclaimable", pressure.MaintenanceMode);
        Assert.True(policy.AllowsNewRewrite(in pressure, liveBytes: 100, deadBytes: 100));

        var critical = policy.Evaluate(Total, 9_200, 800);
        Assert.Equal(BulkStoragePressureState.Critical, critical.State);
        Assert.True(critical.ReserveExhausted);
        Assert.False(critical.RewriteAllowed);
        Assert.False(policy.AllowsNewRewrite(in critical, liveBytes: 10, deadBytes: 1_000));

        var emergency = policy.Evaluate(Total, 9_600, 400);
        Assert.Equal(BulkStoragePressureState.Emergency, emergency.State);
        Assert.True(emergency.ReserveExhausted);
        Assert.True(emergency.EmergencyAdmissionProtectionRequired);
        Assert.False(emergency.RewriteAllowed);
        Assert.False(policy.AllowsNewRewrite(in emergency, liveBytes: 1, deadBytes: 1_000_000));
        Assert.Equal("EmergencyProtectIngress", emergency.MaintenanceMode);
    }

    [Fact]
    public void High_and_critical_rewrites_require_reserve_headroom_and_a_net_benefit()
    {
        var options = new BulkStoragePressureOptions
        {
            OperationalReservePercent = 1,
            RecoveryReservePercent = 1,
            RewriteReservePercent = 1,
        };
        var policy = new BulkStoragePressurePolicy(options);
        var high = policy.Evaluate(Total, 8_700, 1_300);
        Assert.Equal(BulkStoragePressureState.High, high.State);
        Assert.False(high.ReserveExhausted);
        Assert.True(policy.AllowsNewRewrite(in high, liveBytes: 100, deadBytes: 50));
        Assert.False(policy.AllowsNewRewrite(in high, liveBytes: 1_200, deadBytes: 50));

        var critical = policy.Evaluate(Total, 9_200, 800);
        Assert.Equal(BulkStoragePressureState.Critical, critical.State);
        Assert.True(policy.AllowsNewRewrite(in critical, liveBytes: 100, deadBytes: 200));
        Assert.False(policy.AllowsNewRewrite(in critical, liveBytes: 200, deadBytes: 100));
        Assert.False(policy.AllowsNewRewrite(in critical, liveBytes: 700, deadBytes: 800));
    }

    [Fact]
    public void Filesystem_used_percent_is_not_journal_outstanding_bytes()
    {
        var bulk = new BulkStoragePressurePolicy().Evaluate(1_000_000, 960_000, 40_000);
        Assert.Equal(BulkStoragePressureState.Emergency, bulk.State);
        Assert.Equal(960_000, bulk.UsedBytes);
        Assert.True(bulk.EmergencyAdmissionProtectionRequired);
        Assert.False(bulk.RewriteAllowed);
        var unmeasured = BulkStoragePressurePolicy.Unmeasured();
        Assert.False(unmeasured.Measured);
        Assert.Equal(BulkStoragePressureState.Normal, unmeasured.State);
        Assert.True(unmeasured.RewriteAllowed);
    }

    [Theory]
    [InlineData(1_000, BulkStoragePressureState.Normal)]
    [InlineData(7_600, BulkStoragePressureState.Warning)]
    [InlineData(8_200, BulkStoragePressureState.Pressure)]
    public void Normal_warning_and_pressure_admit_without_a_recovery_reserve_floor(
        long used,
        BulkStoragePressureState state)
    {
        var policy = new BulkStoragePressurePolicy();
        var evaluation = policy.Evaluate(Total, used, Total - used);
        Assert.Equal(state, evaluation.State);
        Assert.True(policy.AllowsNewAccept(in evaluation, segmentBytes: Total, unwrittenSegmentBytes: 0));
    }

    [Fact]
    public void High_critical_and_emergency_admit_only_above_the_recovery_reserve()
    {
        var policy = new BulkStoragePressurePolicy();
        var high = policy.Evaluate(Total, 8_800, 1_200);
        Assert.Equal(BulkStoragePressureState.High, high.State);
        Assert.Equal(500, high.RecoveryReserveBytes);
        Assert.True(policy.AllowsNewAccept(in high, segmentBytes: 700, unwrittenSegmentBytes: 0));
        Assert.False(policy.AllowsNewAccept(in high, segmentBytes: 701, unwrittenSegmentBytes: 0));
        Assert.False(policy.AllowsNewAccept(in high, segmentBytes: 200, unwrittenSegmentBytes: 501));

        var critical = policy.Evaluate(Total, 9_200, 800);
        Assert.True(policy.AllowsNewAccept(in critical, segmentBytes: 300, unwrittenSegmentBytes: 0));
        Assert.False(policy.AllowsNewAccept(in critical, segmentBytes: 301, unwrittenSegmentBytes: 0));

        var emergency = policy.Evaluate(Total, 9_600, 400);
        Assert.Equal(BulkStoragePressureState.Emergency, emergency.State);
        Assert.False(policy.AllowsNewAccept(in emergency, segmentBytes: 1, unwrittenSegmentBytes: 0));
        Assert.Equal(400 - 500 - 1, BulkStoragePressurePolicy.ProtectedHeadroomBytes(in emergency, 1, 0));
    }

    [Fact]
    public void Unmeasured_capacity_does_not_admit()
    {
        var policy = new BulkStoragePressurePolicy();
        var unmeasured = BulkStoragePressurePolicy.Unmeasured();
        Assert.False(policy.AllowsNewAccept(in unmeasured, segmentBytes: 1, unwrittenSegmentBytes: 0));
        Assert.Equal(0, BulkStoragePressurePolicy.ProtectedHeadroomBytes(in unmeasured, 1, 0));
    }
}
