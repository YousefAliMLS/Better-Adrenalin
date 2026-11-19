using RadeonGuard.Core.Hardware;
using RadeonGuard.Core.Models;
using RadeonGuard.Core.Validation;
using Xunit;

namespace RadeonGuard.Tests;

public sealed class ProfileValidatorTests
{
    [Fact]
    public async Task ValidFivePointFanCurve_PassesValidation()
    {
        using var mock = new MockGpuHardwareAdapter();
        var caps = await mock.GetTuningCapabilitiesAsync(0);
        var fan = new FanProfile
        {
            Name = "Fortnite Quiet",
            ZeroRpmEnabled = true,
            Points =
            [
                new FanCurvePoint(30, 15),
                new FanCurvePoint(50, 30),
                new FanCurvePoint(63, 46),
                new FanCurvePoint(72, 48),
                new FanCurvePoint(80, 65)
            ]
        };

        var res = ProfileValidator.ValidateFanProfile(fan, caps);
        Assert.True(res.IsValid);
    }

    [Fact]
    public async Task NonMonotonicFanCurve_IsRejected()
    {
        using var mock = new MockGpuHardwareAdapter();
        var caps = await mock.GetTuningCapabilitiesAsync(0);
        var fan = new FanProfile
        {
            Name = "Invalid Curve",
            Points =
            [
                new FanCurvePoint(30, 20),
                new FanCurvePoint(50, 40),
                new FanCurvePoint(45, 50), // temp drops!
                new FanCurvePoint(70, 45), // pwm drops!
                new FanCurvePoint(85, 80)
            ]
        };

        var res = ProfileValidator.ValidateFanProfile(fan, caps);
        Assert.False(res.IsValid);
        Assert.True(res.Errors.Count >= 2);
    }

    [Fact]
    public async Task OutOfRangeTuning_And_QuarantinedProfile_AreRejected()
    {
        using var mock = new MockGpuHardwareAdapter();
        var caps = await mock.GetTuningCapabilitiesAsync(0);
        var tuning = new TuningProfile
        {
            Name = "Extreme Bad",
            IsQuarantined = true,
            QuarantineReason = "TDR",
            EnableClockTuning = true,
            MinCoreClockMHz = 2600,
            MaxCoreClockMHz = 2400, // min > max
            EnableVoltageTuning = true,
            CoreVoltageMv = 500,    // below 881 mV
            EnablePowerTuning = true,
            PowerLimitDeltaPercent = 50 // above +15%
        };

        var res = ProfileValidator.ValidateTuningProfile(tuning, caps);
        Assert.False(res.IsValid);
        Assert.Contains(res.Errors, e => e.Contains("quarantined", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(res.Errors, e => e.Contains("Minimum GPU clock", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(res.Errors, e => e.Contains("Core Voltage", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(res.Errors, e => e.Contains("Power Limit", StringComparison.OrdinalIgnoreCase));
    }
}
