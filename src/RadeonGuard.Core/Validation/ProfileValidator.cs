using RadeonGuard.Core.Models;

namespace RadeonGuard.Core.Validation;

public sealed record ValidationResult(bool IsValid, IReadOnlyList<string> Errors)
{
    public static ValidationResult Success() => new(true, Array.Empty<string>());
    public static ValidationResult Failure(params string[] errors) => new(false, errors);
}

public static class ProfileValidator
{
    public static ValidationResult ValidateFanProfile(FanProfile profile, GpuTuningCapabilities? caps = null)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(profile.Name))
        {
            errors.Add("Fan profile name cannot be empty.");
        }

        if (profile.Points.Count == 0)
        {
            errors.Add("Fan profile must contain at least 1 curve point (RX 6800 XT uses 5 hardware points).");
            return new ValidationResult(false, errors);
        }

        if (caps is not null && !caps.ManualFanTuningSupported)
        {
            errors.Add("Manual fan tuning is not supported on the detected GPU.");
        }

        int minTemp = caps?.FanTempRange.Supported == true ? caps.FanTempRange.MinValue : 20;
        int maxTemp = caps?.FanTempRange.Supported == true ? caps.FanTempRange.MaxValue : 110;
        int minSpd = caps?.FanSpeedRange.Supported == true ? caps.FanSpeedRange.MinValue : 0;
        int maxSpd = caps?.FanSpeedRange.Supported == true ? caps.FanSpeedRange.MaxValue : 100;

        for (int i = 0; i < profile.Points.Count; i++)
        {
            var pt = profile.Points[i];
            if (pt.TemperatureC < minTemp || pt.TemperatureC > maxTemp)
            {
                errors.Add($"Point {i + 1} temperature {pt.TemperatureC}°C is outside hardware range [{minTemp}..{maxTemp}]°C.");
            }
            if (pt.FanSpeedPercent < minSpd || pt.FanSpeedPercent > maxSpd)
            {
                errors.Add($"Point {i + 1} fan speed {pt.FanSpeedPercent}% is outside hardware range [{minSpd}..{maxSpd}]%.");
            }
            if (i > 0)
            {
                var prev = profile.Points[i - 1];
                if (pt.TemperatureC <= prev.TemperatureC)
                {
                    errors.Add($"Point {i + 1} temperature ({pt.TemperatureC}°C) must be strictly greater than Point {i} ({prev.TemperatureC}°C).");
                }
                if (pt.FanSpeedPercent < prev.FanSpeedPercent)
                {
                    errors.Add($"Point {i + 1} fan speed ({pt.FanSpeedPercent}%) cannot be lower than Point {i} ({prev.FanSpeedPercent}%).");
                }
            }
        }

        if (profile.MinAcousticLimitMHz.HasValue && caps?.MinAcousticSupported == true && caps.MinAcousticRange.Supported)
        {
            int m = profile.MinAcousticLimitMHz.Value;
            if (m < caps.MinAcousticRange.MinValue || m > caps.MinAcousticRange.MaxValue)
            {
                errors.Add($"Min Acoustic Limit {m} MHz is outside hardware range [{caps.MinAcousticRange.MinValue}..{caps.MinAcousticRange.MaxValue}] MHz.");
            }
        }

        return errors.Count == 0 ? ValidationResult.Success() : new ValidationResult(false, errors);
    }

    public static ValidationResult ValidateTuningProfile(TuningProfile profile, GpuTuningCapabilities? caps = null)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(profile.Name))
        {
            errors.Add("Tuning profile name cannot be empty.");
        }

        if (profile.IsQuarantined)
        {
            errors.Add($"Profile '{profile.Name}' is currently quarantined ({profile.QuarantineReason ?? "driver timeout detected"}) and cannot be auto-applied until reviewed.");
        }

        if (profile.EnableClockTuning)
        {
            if (profile.MinCoreClockMHz > profile.MaxCoreClockMHz)
            {
                errors.Add($"Minimum GPU clock ({profile.MinCoreClockMHz} MHz) cannot exceed maximum GPU clock ({profile.MaxCoreClockMHz} MHz).");
            }
            if (caps is not null)
            {
                if (!caps.ManualGfxTuningSupported)
                {
                    errors.Add("Manual graphics clock tuning is not supported by this GPU.");
                }
                else
                {
                    if (caps.GfxMinFreqRange.Supported &&
                        (profile.MinCoreClockMHz < caps.GfxMinFreqRange.MinValue || profile.MinCoreClockMHz > caps.GfxMinFreqRange.MaxValue))
                    {
                        errors.Add($"Min Core Clock {profile.MinCoreClockMHz} MHz is outside ADLX range [{caps.GfxMinFreqRange.MinValue}..{caps.GfxMinFreqRange.MaxValue}] MHz.");
                    }
                    if (caps.GfxMaxFreqRange.Supported &&
                        (profile.MaxCoreClockMHz < caps.GfxMaxFreqRange.MinValue || profile.MaxCoreClockMHz > caps.GfxMaxFreqRange.MaxValue))
                    {
                        errors.Add($"Max Core Clock {profile.MaxCoreClockMHz} MHz is outside ADLX range [{caps.GfxMaxFreqRange.MinValue}..{caps.GfxMaxFreqRange.MaxValue}] MHz.");
                    }
                }
            }
        }

        if (profile.EnableVoltageTuning && caps is not null)
        {
            if (!caps.ManualGfxTuningSupported)
            {
                errors.Add("Manual voltage tuning is not supported by this GPU.");
            }
            else if (caps.GfxVoltageRange.Supported &&
                     (profile.CoreVoltageMv < caps.GfxVoltageRange.MinValue || profile.CoreVoltageMv > caps.GfxVoltageRange.MaxValue))
            {
                errors.Add($"Core Voltage {profile.CoreVoltageMv} mV is outside ADLX range [{caps.GfxVoltageRange.MinValue}..{caps.GfxVoltageRange.MaxValue}] mV.");
            }
        }

        if (profile.EnableVramTuning && caps is not null)
        {
            if (!caps.ManualVramTuningSupported)
            {
                errors.Add("Manual VRAM tuning is not supported by this GPU.");
            }
            else if (caps.VramMaxFreqRange.Supported &&
                     (profile.MaxVramClockMHz < caps.VramMaxFreqRange.MinValue || profile.MaxVramClockMHz > caps.VramMaxFreqRange.MaxValue))
            {
                errors.Add($"Max VRAM Clock {profile.MaxVramClockMHz} MHz is outside ADLX range [{caps.VramMaxFreqRange.MinValue}..{caps.VramMaxFreqRange.MaxValue}] MHz.");
            }
        }

        if (profile.EnablePowerTuning && caps is not null)
        {
            if (!caps.ManualPowerTuningSupported)
            {
                errors.Add("Manual power limit tuning is not supported by this GPU.");
            }
            else if (caps.PowerLimitRange.Supported &&
                     (profile.PowerLimitDeltaPercent < caps.PowerLimitRange.MinValue || profile.PowerLimitDeltaPercent > caps.PowerLimitRange.MaxValue))
            {
                errors.Add($"Power Limit {profile.PowerLimitDeltaPercent}% is outside ADLX range [{caps.PowerLimitRange.MinValue}..{caps.PowerLimitRange.MaxValue}]%.");
            }
        }

        return errors.Count == 0 ? ValidationResult.Success() : new ValidationResult(false, errors);
    }
}
