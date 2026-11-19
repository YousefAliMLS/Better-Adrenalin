using System.Text.Json;
using RadeonGuard.Core.Models;
using RadeonGuard.Core.Validation;

namespace RadeonGuard.Core.Hardware;

/// <summary>
/// Deterministic in-memory RX 6800 XT simulation for automated unit & fault-injection testing.
/// </summary>
public sealed class MockGpuHardwareAdapter : IGpuHardwareAdapter
{
    private readonly GpuDeviceInfo _gpu = new(
        0, 2304, 16384, 2, 1, 6, 16,
        "AMD Radeon RX 6800 XT", "AMD Radeon RX 6800 XT",
        "1002", "73BF", "C1", "439E", "1DA2",
        @"PCI\VEN_1002&DEV_73BF&SUBSYS_439E1DA2&REV_C1\6&18786A0B&0&00000019",
        @"HKLM\SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}\0001",
        "GDDR6", "32.0.21045.16010", "32.0.21045.16010", "25.10.45.16",
        "113-D4120500-101", "020.001.000.049", "2020/11/01");

    private readonly GpuTuningCapabilities _caps = new(
        true, true, true, true, true, true, false, true, true, true, false,
        new IntRange(500, 2500, 1, true),
        new IntRange(500, 2800, 1, true),
        new IntRange(881, 1150, 1, true),
        new IntRange(1000, 2150, 2, true),
        new IntRange(-10, 15, 1, true),
        new IntRange(0, 0, 0, false),
        new IntRange(15, 100, 1, true),
        new IntRange(20, 110, 1, true),
        true, true, false, false,
        new IntRange(500, 2500, 1, true),
        new IntRange(0, 0, 0, false),
        new IntRange(0, 0, 0, false));

    private FanTuningHardwareSnapshot _fanState = new(
        true, true, true, true, 1500,
        new[]
        {
            new FanCurvePoint(30, 15),
            new FanCurvePoint(50, 30),
            new FanCurvePoint(65, 50),
            new FanCurvePoint(80, 75),
            new FanCurvePoint(95, 100)
        },
        new IntRange(15, 100, 1, true),
        new IntRange(20, 110, 1, true));

    private ManualTuningSnapshot _manualState = new(
        500, 2439, 1150, 2000, VramTimingMode.Default, 0, 0,
        true, true, true, true, true);

    private ThreeDSettings _threeDState = new()
    {
        AntiLagSupported = true,
        ChillSupported = true,
        BoostSupported = true,
        ImageSharpeningSupported = true,
        EnhancedSyncSupported = true,
        WaitForVerticalRefreshSupported = true,
        FrtcSupported = true
    };

    public bool IsInitialized { get; private set; }
    public string AdlxVersionString => "1.4.0.121 (Mock)";
    public double SimulatedEdgeTempC { get; set; } = 42.0;
    public double SimulatedHotspotTempC { get; set; } = 51.0;
    public double SimulatedGpuUsagePercent { get; set; } = 18.5;
    public bool SimulateApplyFailure { get; set; }
    public bool SimulateStuckFanSpin { get; set; }
    private bool _sawZeroRpmPulseOff;
    public int ApplyFanCallCount { get; private set; }
    public int ApplyTuningCallCount { get; private set; }

    public event EventHandler<TuningChangedNotification>? TuningChanged;

    public Task<AdlxResultCode> InitializeAsync(CancellationToken ct = default)
    {
        IsInitialized = true;
        return Task.FromResult(AdlxResultCode.Ok);
    }

    public Task<int> GetGpuCountAsync(CancellationToken ct = default) => Task.FromResult(1);
    public Task<GpuDeviceInfo?> GetGpuInfoAsync(int gpuIndex, CancellationToken ct = default) =>
        Task.FromResult<GpuDeviceInfo?>(gpuIndex == 0 ? _gpu : null);

    public Task<GpuTelemetrySample?> GetCurrentMetricsAsync(int gpuIndex, CancellationToken ct = default) =>
        Task.FromResult<GpuTelemetrySample?>(new GpuTelemetrySample(
            DateTimeOffset.UtcNow, Environment.TickCount64,
            SimulatedGpuUsagePercent, 1450, 2000, SimulatedEdgeTempC, SimulatedHotspotTempC, 34.0,
            45.0, 58.0, SimulateStuckFanSpin ? 950 : (_fanState.ZeroRpmEnabled && SimulatedHotspotTempC < 55 ? 0 : 1350),
            2450, 912, 0x3FF));

    public Task<GpuTuningCapabilities?> GetTuningCapabilitiesAsync(int gpuIndex, CancellationToken ct = default) =>
        Task.FromResult<GpuTuningCapabilities?>(_caps);

    public Task<FanTuningHardwareSnapshot?> GetFanTuningStateAsync(int gpuIndex, bool defaultState = false, CancellationToken ct = default) =>
        Task.FromResult<FanTuningHardwareSnapshot?>(_fanState);

    public Task<ManualTuningSnapshot?> GetManualTuningStateAsync(int gpuIndex, bool defaultState = false, CancellationToken ct = default) =>
        Task.FromResult<ManualTuningSnapshot?>(_manualState);

    public Task<ThreeDSettings?> Get3DSettingsStateAsync(int gpuIndex, CancellationToken ct = default) =>
        Task.FromResult<ThreeDSettings?>(_threeDState);

    public Task<HardwareApplyResult> ApplyFanProfileAsync(int gpuIndex, FanProfile profile, CancellationToken ct = default)
    {
        var val = ProfileValidator.ValidateFanProfile(profile, _caps);
        if (!val.IsValid)
        {
            return Task.FromResult(new HardwareApplyResult(false, "REJECTED", AdlxResultCode.InvalidArgs, string.Join(" ", val.Errors), "{}"));
        }
        if (SimulateApplyFailure)
        {
            return Task.FromResult(new HardwareApplyResult(false, "FAILED", AdlxResultCode.Fail, "Simulated ADLX write failure.", "{}"));
        }
        ApplyFanCallCount++;
        if (SimulateStuckFanSpin)
        {
            if (!profile.ZeroRpmEnabled)
            {
                _sawZeroRpmPulseOff = true;
            }
            else if (_sawZeroRpmPulseOff)
            {
                SimulateStuckFanSpin = false;
                _sawZeroRpmPulseOff = false;
            }
        }
        _fanState = _fanState with
        {
            ZeroRpmEnabled = profile.ZeroRpmEnabled,
            MinAcousticLimitMHz = profile.MinAcousticLimitMHz ?? _fanState.MinAcousticLimitMHz,
            Points = profile.Points.ToList()
        };
        return Task.FromResult(new HardwareApplyResult(true, "VERIFIED", AdlxResultCode.Ok, null, JsonSerializer.Serialize(_fanState)));
    }

    public async Task<HardwareApplyResult> ApplyTuningProfileAsync(int gpuIndex, TuningProfile profile, FanProfile? fanProfile, CancellationToken ct = default)
    {
        var val = ProfileValidator.ValidateTuningProfile(profile, _caps);
        if (!val.IsValid)
        {
            return new HardwareApplyResult(false, "REJECTED", AdlxResultCode.InvalidArgs, string.Join(" ", val.Errors), "{}");
        }
        if (SimulateApplyFailure)
        {
            return new HardwareApplyResult(false, "FAILED", AdlxResultCode.Fail, "Simulated ADLX tuning write failure.", "{}");
        }

        ApplyTuningCallCount++;
        _manualState = _manualState with
        {
            GfxMinFreqMHz = profile.EnableClockTuning ? profile.MinCoreClockMHz : 500,
            GfxMaxFreqMHz = profile.EnableClockTuning ? profile.MaxCoreClockMHz : 2439,
            GfxVoltageMv = profile.EnableVoltageTuning ? profile.CoreVoltageMv : 1150,
            VramMaxFreqMHz = profile.EnableVramTuning ? profile.MaxVramClockMHz : 2000,
            VramMemoryTiming = profile.EnableVramTuning ? profile.VramTiming : VramTimingMode.Default,
            PowerLimitPercent = profile.EnablePowerTuning ? profile.PowerLimitDeltaPercent : 0,
            AtFactory = profile.IsGlobalDefault && !profile.EnableClockTuning && !profile.EnableVoltageTuning
        };
        _threeDState = profile.ThreeDSettings;

        if (fanProfile is not null)
        {
            var fanRes = await ApplyFanProfileAsync(gpuIndex, fanProfile, ct).ConfigureAwait(false);
            if (!fanRes.Success) return fanRes;
        }

        return new HardwareApplyResult(true, "VERIFIED", AdlxResultCode.Ok, null, JsonSerializer.Serialize(new { Manual = _manualState, Fan = _fanState }));
    }

    public Task<HardwareApplyResult> ResetToFactoryAsync(int gpuIndex, CancellationToken ct = default)
    {
        _manualState = new ManualTuningSnapshot(500, 2439, 1150, 2000, VramTimingMode.Default, 0, 0, true, true, true, true, true);
        return Task.FromResult(new HardwareApplyResult(true, "VERIFIED", AdlxResultCode.Ok, null, "{}"));
    }

    /// <summary>
    /// Simulates external interference (e.g. Adrenalin or Afterburner resetting the fan curve to factory).
    /// </summary>
    public void SimulateExternalFanReset()
    {
        _fanState = _fanState with
        {
            ZeroRpmEnabled = true,
            Points = new[]
            {
                new FanCurvePoint(30, 15),
                new FanCurvePoint(55, 25),
                new FanCurvePoint(70, 40),
                new FanCurvePoint(85, 60),
                new FanCurvePoint(95, 85)
            }
        };
        TuningChanged?.Invoke(this, new TuningChangedNotification(
            _gpu.UniqueId, SyncOrigin.External, false, false, false, false, true, false));
    }

    public void Dispose() { }
}
