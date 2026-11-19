using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Channels;
using RadeonGuard.Core.Models;
using RadeonGuard.Core.Validation;

namespace RadeonGuard.Core.Hardware;

/// <summary>
/// Thread-safe, single-queue ADLX hardware adapter wrapping RadeonGuard.AdlxBridge.dll.
/// Serializes all driver escape calls onto a dedicated worker thread and performs read-after-write verification.
/// </summary>
public sealed class AdlxHardwareAdapter : IGpuHardwareAdapter
{
    private readonly Channel<Func<Task>> _commandQueue = Channel.CreateUnbounded<Func<Task>>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly Task _workerTask;
    private readonly CancellationTokenSource _disposeCts = new();
    private AdlxNativeInterop.RgTuningChangedCallback? _callbackDelegate;
    private GCHandle _callbackHandle;
    private bool _disposed;

    public bool IsInitialized { get; private set; }
    public string AdlxVersionString { get; private set; } = string.Empty;
    public event EventHandler<TuningChangedNotification>? TuningChanged;

    public AdlxHardwareAdapter()
    {
        _workerTask = Task.Factory.StartNew(
            ProcessQueueAsync,
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).Unwrap();
    }

    private async Task ProcessQueueAsync()
    {
        await foreach (var work in _commandQueue.Reader.ReadAllAsync())
        {
            try
            {
                await work().ConfigureAwait(false);
            }
            catch
            {
                // Individual task exceptions are propagated via their TaskCompletionSource.
            }
        }
    }

    private Task<T> EnqueueAsync<T>(Func<T> func, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_commandQueue.Writer.TryWrite(() =>
        {
            if (ct.IsCancellationRequested)
            {
                tcs.TrySetCanceled(ct);
                return Task.CompletedTask;
            }
            try
            {
                tcs.TrySetResult(func());
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
            return Task.CompletedTask;
        }))
        {
            tcs.TrySetException(new ObjectDisposedException(nameof(AdlxHardwareAdapter)));
        }
        return tcs.Task;
    }

    private Task<T> EnqueueAsync<T>(Func<Task<T>> func, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_commandQueue.Writer.TryWrite(async () =>
        {
            if (ct.IsCancellationRequested)
            {
                tcs.TrySetCanceled(ct);
                return;
            }
            try
            {
                tcs.TrySetResult(await func().ConfigureAwait(false));
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }))
        {
            tcs.TrySetException(new ObjectDisposedException(nameof(AdlxHardwareAdapter)));
        }
        return tcs.Task;
    }

    public unsafe Task<AdlxResultCode> InitializeAsync(CancellationToken ct = default) => EnqueueAsync(() =>
    {
        if (IsInitialized) return AdlxResultCode.Ok;

        ulong fullVer = 0;
        byte* verBuf = stackalloc byte[64];
        var res = (AdlxResultCode)AdlxNativeInterop.RgInitialize(&fullVer, verBuf, 64);
        if (res is AdlxResultCode.Ok or AdlxResultCode.AlreadyInitialized)
        {
            IsInitialized = true;
            AdlxVersionString = AdlxNativeInterop.ReadFixedUtf8(verBuf, 64);
            RegisterCallbackInternal();
        }
        return res;
    }, ct);

    private unsafe void RegisterCallbackInternal()
    {
        if (_callbackDelegate is not null) return;
        _callbackDelegate = OnNativeTuningChanged;
        _callbackHandle = GCHandle.Alloc(_callbackDelegate);
        var fnPtr = Marshal.GetFunctionPointerForDelegate(_callbackDelegate);
        AdlxNativeInterop.RgRegisterTuningChangedCallback(fnPtr);
    }

    private unsafe void OnNativeTuningChanged(AdlxNativeInterop.RgTuningChangedNotification* n)
    {
        if (n == null) return;
        var evt = new TuningChangedNotification(
            n->GpuUniqueId,
            (SyncOrigin)n->SyncOrigin,
            n->IsAutomaticTuningChanged != 0,
            n->IsPresetTuningChanged != 0,
            n->IsManualGfxChanged != 0,
            n->IsManualVramChanged != 0,
            n->IsManualFanChanged != 0,
            n->IsManualPowerChanged != 0);

        // Marshal off ADLX callback thread immediately to avoid blocking ADLX internals.
        ThreadPool.QueueUserWorkItem(static state =>
        {
            var (self, payload) = ((AdlxHardwareAdapter, TuningChangedNotification))state!;
            self.TuningChanged?.Invoke(self, payload);
        }, (this, evt));
    }

    public unsafe Task<int> GetGpuCountAsync(CancellationToken ct = default) => EnqueueAsync(() =>
    {
        int count = 0;
        var res = (AdlxResultCode)AdlxNativeInterop.RgGetGpuCount(&count);
        return res == AdlxResultCode.Ok ? count : 0;
    }, ct);

    public unsafe Task<GpuDeviceInfo?> GetGpuInfoAsync(int gpuIndex, CancellationToken ct = default) => EnqueueAsync(() =>
    {
        AdlxNativeInterop.RgGpuInfo raw;
        var res = (AdlxResultCode)AdlxNativeInterop.RgGetGpuInfo(gpuIndex, &raw);
        if (res != AdlxResultCode.Ok) return null;

        return new GpuDeviceInfo(
            raw.GpuIndex,
            raw.UniqueId,
            raw.TotalVramMb,
            raw.GpuType,
            raw.AsicFamilyType,
            raw.PciBusType,
            raw.PciLaneWidth,
            AdlxNativeInterop.ReadFixedUtf8(raw.Name, 128),
            AdlxNativeInterop.ReadFixedUtf8(raw.ProductName, 128),
            AdlxNativeInterop.ReadFixedUtf8(raw.VendorId, 16),
            AdlxNativeInterop.ReadFixedUtf8(raw.DeviceId, 16),
            AdlxNativeInterop.ReadFixedUtf8(raw.RevisionId, 16),
            AdlxNativeInterop.ReadFixedUtf8(raw.SubSystemId, 16),
            AdlxNativeInterop.ReadFixedUtf8(raw.SubSystemVendorId, 16),
            AdlxNativeInterop.ReadFixedUtf8(raw.PnpString, 260),
            AdlxNativeInterop.ReadFixedUtf8(raw.DriverPath, 260),
            AdlxNativeInterop.ReadFixedUtf8(raw.VramType, 32),
            AdlxNativeInterop.ReadFixedUtf8(raw.DriverVersion, 64),
            AdlxNativeInterop.ReadFixedUtf8(raw.AmdWindowsDriverVersion, 64),
            AdlxNativeInterop.ReadFixedUtf8(raw.AmdSoftwareVersion, 64),
            AdlxNativeInterop.ReadFixedUtf8(raw.BiosPartNumber, 64),
            AdlxNativeInterop.ReadFixedUtf8(raw.BiosVersion, 64),
            AdlxNativeInterop.ReadFixedUtf8(raw.BiosDate, 32));
    }, ct);

    public unsafe Task<GpuTelemetrySample?> GetCurrentMetricsAsync(int gpuIndex, CancellationToken ct = default) => EnqueueAsync(() =>
    {
        AdlxNativeInterop.RgGpuMetrics m;
        var res = (AdlxResultCode)AdlxNativeInterop.RgGetCurrentGpuMetrics(gpuIndex, &m);
        if (res != AdlxResultCode.Ok) return null;

        return new GpuTelemetrySample(
            DateTimeOffset.UtcNow,
            m.TimestampMs,
            m.GpuUsagePercent,
            m.GpuClockMHz,
            m.VramClockMHz,
            m.EdgeTemperatureC,
            m.HotspotTemperatureC,
            m.IntakeTemperatureC,
            m.GpuPowerW,
            m.TotalBoardPowerW,
            m.FanSpeedRpm,
            m.VramUsedMb,
            m.VoltageMv,
            m.ValidMask);
    }, ct);

    public unsafe Task<GpuTuningCapabilities?> GetTuningCapabilitiesAsync(int gpuIndex, CancellationToken ct = default) =>
        EnqueueAsync(() => GetTuningCapabilitiesInternal(gpuIndex), ct);

    private static unsafe GpuTuningCapabilities? GetTuningCapabilitiesInternal(int gpuIndex)
    {
        AdlxNativeInterop.RgGpuTuningCapabilities c;
        var res = (AdlxResultCode)AdlxNativeInterop.RgGetTuningCapabilities(gpuIndex, &c);
        if (res != AdlxResultCode.Ok) return null;

        return new GpuTuningCapabilities(
            c.ManualGfxTuningSupported != 0,
            c.IsGfxTuningV2 != 0,
            c.ManualVramTuningSupported != 0,
            c.IsVramTuningV2 != 0,
            c.VramMemoryTimingSupported != 0,
            c.ManualPowerTuningSupported != 0,
            c.TdcLimitSupported != 0,
            c.ManualFanTuningSupported != 0,
            c.AutoTuningSupported != 0,
            c.PresetTuningSupported != 0,
            c.AtFactory != 0,
            ToRange(c.GfxMinFreqRange),
            ToRange(c.GfxMaxFreqRange),
            ToRange(c.GfxVoltageRange),
            ToRange(c.VramMaxFreqRange),
            ToRange(c.PowerLimitRange),
            ToRange(c.TdcLimitRange),
            ToRange(c.FanSpeedRange),
            ToRange(c.FanTempRange),
            c.ZeroRpmSupported != 0,
            c.MinAcousticSupported != 0,
            c.MinFanSpeedSupported != 0,
            c.TargetFanSpeedSupported != 0,
            ToRange(c.MinAcousticRange),
            ToRange(c.MinFanSpeedRange),
            ToRange(c.TargetFanSpeedRange));
    }

    public unsafe Task<FanTuningHardwareSnapshot?> GetFanTuningStateAsync(int gpuIndex, bool defaultState = false, CancellationToken ct = default) =>
        EnqueueAsync(() => GetFanTuningStateInternal(gpuIndex, defaultState), ct);

    private static unsafe FanTuningHardwareSnapshot? GetFanTuningStateInternal(int gpuIndex, bool defaultState)
    {
        AdlxNativeInterop.RgFanTuningState raw;
        var res = (AdlxResultCode)(defaultState
            ? AdlxNativeInterop.RgGetDefaultFanTuningState(gpuIndex, &raw)
            : AdlxNativeInterop.RgGetFanTuningState(gpuIndex, &raw));

        if (res != AdlxResultCode.Ok && raw.Supported == 0) return null;

        var points = new List<FanCurvePoint>(raw.PointCount);
        for (int i = 0; i < raw.PointCount && i < 8; i++)
        {
            points.Add(new FanCurvePoint(raw.PointPairs[i * 2], raw.PointPairs[i * 2 + 1]));
        }

        return new FanTuningHardwareSnapshot(
            raw.Supported != 0,
            raw.ZeroRpmSupported != 0,
            raw.ZeroRpmEnabled != 0,
            raw.MinAcousticSupported != 0,
            raw.MinAcousticLimit,
            points,
            ToRange(raw.SpeedRange),
            ToRange(raw.TemperatureRange));
    }

    public unsafe Task<ManualTuningSnapshot?> GetManualTuningStateAsync(int gpuIndex, bool defaultState = false, CancellationToken ct = default) =>
        EnqueueAsync(() => GetManualTuningStateInternal(gpuIndex, defaultState), ct);

    private static unsafe ManualTuningSnapshot? GetManualTuningStateInternal(int gpuIndex, bool defaultState)
    {
        AdlxNativeInterop.RgManualTuningState raw;
        var res = (AdlxResultCode)(defaultState
            ? AdlxNativeInterop.RgGetDefaultManualTuningState(gpuIndex, &raw)
            : AdlxNativeInterop.RgGetManualTuningState(gpuIndex, &raw));
        if (res != AdlxResultCode.Ok) return null;

        return new ManualTuningSnapshot(
            raw.GfxMinFreqMHz,
            raw.GfxMaxFreqMHz,
            raw.GfxVoltageMv,
            raw.VramMaxFreqMHz,
            (VramTimingMode)raw.VramMemoryTiming,
            raw.PowerLimitPercent,
            raw.TdcLimitPercent,
            raw.GfxValid != 0,
            raw.VramValid != 0,
            raw.PowerValid != 0,
            raw.MemoryTimingValid != 0,
            raw.AtFactory != 0);
    }

    public unsafe Task<ThreeDSettings?> Get3DSettingsStateAsync(int gpuIndex, CancellationToken ct = default) =>
        EnqueueAsync(() => Get3DSettingsStateInternal(gpuIndex), ct);

    private static unsafe ThreeDSettings? Get3DSettingsStateInternal(int gpuIndex)
    {
        AdlxNativeInterop.Rg3DSettingsState raw;
        var res = (AdlxResultCode)AdlxNativeInterop.RgGet3DSettingsState(gpuIndex, &raw);
        if (res != AdlxResultCode.Ok) return null;

        return new ThreeDSettings
        {
            AntiLagSupported = raw.AntiLagSupported != 0,
            AntiLagEnabled = raw.AntiLagEnabled != 0,
            ChillSupported = raw.ChillSupported != 0,
            ChillEnabled = raw.ChillEnabled != 0,
            ChillMinFps = raw.ChillMinFps,
            ChillMaxFps = raw.ChillMaxFps,
            ChillFpsRange = ToRange(raw.ChillFpsRange),
            BoostSupported = raw.BoostSupported != 0,
            BoostEnabled = raw.BoostEnabled != 0,
            BoostResolution = raw.BoostResolution,
            BoostResolutionRange = ToRange(raw.BoostResolutionRange),
            ImageSharpeningSupported = raw.ImageSharpeningSupported != 0,
            ImageSharpeningEnabled = raw.ImageSharpeningEnabled != 0,
            ImageSharpeningPercent = raw.ImageSharpeningPercent,
            ImageSharpeningRange = ToRange(raw.ImageSharpeningRange),
            EnhancedSyncSupported = raw.EnhancedSyncSupported != 0,
            EnhancedSyncEnabled = raw.EnhancedSyncEnabled != 0,
            WaitForVerticalRefreshSupported = raw.WaitForVerticalRefreshSupported != 0,
            WaitForVerticalRefreshEnabled = raw.WaitForVerticalRefreshEnabled != 0,
            WaitForVerticalRefreshMode = (VSyncMode)raw.WaitForVerticalRefreshMode,
            FrtcSupported = raw.FrtcSupported != 0,
            FrtcEnabled = raw.FrtcEnabled != 0,
            FrtcFps = raw.FrtcFps,
            FrtcFpsRange = ToRange(raw.FrtcFpsRange),
            AfmfSupported = raw.AfmfSupported != 0,
            AfmfEnabled = raw.AfmfEnabled != 0
        };
    }

    public Task<HardwareApplyResult> ApplyFanProfileAsync(int gpuIndex, FanProfile profile, CancellationToken ct = default) =>
        EnqueueAsync(async () =>
        {
            var caps = GetTuningCapabilitiesInternal(gpuIndex);
            var val = ProfileValidator.ValidateFanProfile(profile, caps);
            if (!val.IsValid)
            {
                return new HardwareApplyResult(false, "REJECTED", AdlxResultCode.InvalidArgs, string.Join(" ", val.Errors), "{}");
            }

            var res = ApplyFanProfileRaw(gpuIndex, profile, out int errIdx);
            if (res == AdlxResultCode.ResetNeeded)
            {
                // Auto-tuning was active; reset to factory once and retry.
                AdlxNativeInterop.RgResetGpuTuningToFactory(gpuIndex);
                res = ApplyFanProfileRaw(gpuIndex, profile, out errIdx);
            }

            if (res != AdlxResultCode.Ok)
            {
                return new HardwareApplyResult(false, "FAILED", res, $"ADLX fan tuning returned {res} (invalid point index: {errIdx}).", "{}");
            }

            // Phase 3: Readback verification after short SMU settle delay
            await Task.Delay(150, ct).ConfigureAwait(false);
            var readback = GetFanTuningStateInternal(gpuIndex, false);
            bool matches = VerifyFanMatch(profile, readback);
            string json = JsonSerializer.Serialize(readback);
            return new HardwareApplyResult(
                matches,
                matches ? "VERIFIED" : "APPLIED",
                AdlxResultCode.Ok,
                matches ? null : "Driver accepted fan curve, but readback values differed slightly from target.",
                json);
        }, ct);

    private static unsafe AdlxResultCode ApplyFanProfileRaw(int gpuIndex, FanProfile profile, out int errIdx)
    {
        int count = Math.Min(profile.Points.Count, 8);
        var pts = stackalloc AdlxNativeInterop.RgFanCurvePoint[count];
        for (int i = 0; i < count; i++)
        {
            pts[i].TemperatureC = profile.Points[i].TemperatureC;
            pts[i].FanSpeedPercent = profile.Points[i].FanSpeedPercent;
        }

        int localErrIdx = -1;
        var res = (AdlxResultCode)AdlxNativeInterop.RgSetFanTuningState(
            gpuIndex,
            pts,
            count,
            profile.ZeroRpmEnabled ? (byte)1 : (byte)0,
            profile.MinAcousticLimitMHz ?? 0,
            profile.MinAcousticLimitMHz.HasValue ? (byte)1 : (byte)0,
            &localErrIdx);
        errIdx = localErrIdx;
        return res;
    }

    public Task<HardwareApplyResult> ApplyTuningProfileAsync(int gpuIndex, TuningProfile profile, FanProfile? fanProfile, CancellationToken ct = default) =>
        EnqueueAsync(async () =>
        {
            var caps = GetTuningCapabilitiesInternal(gpuIndex);
            if (caps is null)
            {
                return new HardwareApplyResult(false, "FAILED", AdlxResultCode.Fail, "Could not query GPU tuning capabilities.", "{}");
            }

            var val = ProfileValidator.ValidateTuningProfile(profile, caps);
            if (!val.IsValid)
            {
                return new HardwareApplyResult(false, "REJECTED", AdlxResultCode.InvalidArgs, string.Join(" ", val.Errors), "{}");
            }

            if (fanProfile is not null)
            {
                var fanVal = ProfileValidator.ValidateFanProfile(fanProfile, caps);
                if (!fanVal.IsValid)
                {
                    return new HardwareApplyResult(false, "REJECTED", AdlxResultCode.InvalidArgs, string.Join(" ", fanVal.Errors), "{}");
                }
            }

            if (profile.IsGlobalDefault && !profile.EnableClockTuning && !profile.EnableVoltageTuning &&
                !profile.EnableVramTuning && !profile.EnablePowerTuning)
            {
                var resetRes = (AdlxResultCode)AdlxNativeInterop.RgResetGpuTuningToFactory(gpuIndex);
                if (resetRes != AdlxResultCode.Ok)
                {
                    return new HardwareApplyResult(false, "FAILED", resetRes, $"ResetToFactory returned {resetRes}.", "{}");
                }
            }
            else
            {
                if (profile.EnableClockTuning || profile.EnableVoltageTuning)
                {
                    var def = GetManualTuningStateInternal(gpuIndex, true);
                    int minF = profile.EnableClockTuning ? profile.MinCoreClockMHz : (def?.GfxMinFreqMHz ?? caps.GfxMinFreqRange.MinValue);
                    int maxF = profile.EnableClockTuning ? profile.MaxCoreClockMHz : (def?.GfxMaxFreqMHz ?? caps.GfxMaxFreqRange.MaxValue);
                    int volt = profile.EnableVoltageTuning ? profile.CoreVoltageMv : (def?.GfxVoltageMv ?? caps.GfxVoltageRange.MaxValue);
                    var gfxRes = (AdlxResultCode)AdlxNativeInterop.RgSetManualGfxTuning(gpuIndex, minF, maxF, volt);
                    if (gfxRes == AdlxResultCode.ResetNeeded)
                    {
                        AdlxNativeInterop.RgResetGpuTuningToFactory(gpuIndex);
                        gfxRes = (AdlxResultCode)AdlxNativeInterop.RgSetManualGfxTuning(gpuIndex, minF, maxF, volt);
                    }
                    if (gfxRes != AdlxResultCode.Ok)
                    {
                        return new HardwareApplyResult(false, "FAILED", gfxRes, $"Manual GFX tuning failed: {gfxRes}", "{}");
                    }
                }

                if (profile.EnableVramTuning)
                {
                    var vramRes = (AdlxResultCode)AdlxNativeInterop.RgSetManualVramTuning(gpuIndex, profile.MaxVramClockMHz, (int)profile.VramTiming);
                    if (vramRes != AdlxResultCode.Ok)
                    {
                        return new HardwareApplyResult(false, "FAILED", vramRes, $"Manual VRAM tuning failed: {vramRes}", "{}");
                    }
                }

                if (profile.EnablePowerTuning)
                {
                    var pwrRes = (AdlxResultCode)AdlxNativeInterop.RgSetManualPowerTuning(gpuIndex, profile.PowerLimitDeltaPercent);
                    if (pwrRes != AdlxResultCode.Ok)
                    {
                        return new HardwareApplyResult(false, "FAILED", pwrRes, $"Manual Power tuning failed: {pwrRes}", "{}");
                    }
                }
            }

            if (fanProfile is not null)
            {
                var fanRes = ApplyFanProfileRaw(gpuIndex, fanProfile, out int errIdx);
                if (fanRes != AdlxResultCode.Ok)
                {
                    return new HardwareApplyResult(false, "FAILED", fanRes, $"Fan curve apply failed: {fanRes} (point {errIdx})", "{}");
                }
            }

            Apply3DSettingsRaw(gpuIndex, profile.ThreeDSettings);

            await Task.Delay(150, ct).ConfigureAwait(false);
            var manualReadback = GetManualTuningStateInternal(gpuIndex, false);
            var fanReadback = fanProfile is not null ? GetFanTuningStateInternal(gpuIndex, false) : null;
            bool ok = VerifyManualMatch(profile, manualReadback) && (fanProfile is null || VerifyFanMatch(fanProfile, fanReadback));

            string summary = JsonSerializer.Serialize(new { Manual = manualReadback, Fan = fanReadback });
            return new HardwareApplyResult(
                ok,
                ok ? "VERIFIED" : "APPLIED",
                AdlxResultCode.Ok,
                ok ? null : "Settings applied, but readback differed from target.",
                summary);
        }, ct);

    private static unsafe void Apply3DSettingsRaw(int gpuIndex, ThreeDSettings s)
    {
        AdlxNativeInterop.Rg3DSettingsState raw = new()
        {
            AntiLagEnabled = s.AntiLagEnabled ? (byte)1 : (byte)0,
            ChillEnabled = s.ChillEnabled ? (byte)1 : (byte)0,
            ChillMinFps = s.ChillMinFps,
            ChillMaxFps = s.ChillMaxFps,
            BoostEnabled = s.BoostEnabled ? (byte)1 : (byte)0,
            BoostResolution = s.BoostResolution,
            ImageSharpeningEnabled = s.ImageSharpeningEnabled ? (byte)1 : (byte)0,
            ImageSharpeningPercent = s.ImageSharpeningPercent,
            EnhancedSyncEnabled = s.EnhancedSyncEnabled ? (byte)1 : (byte)0,
            WaitForVerticalRefreshEnabled = s.WaitForVerticalRefreshEnabled ? (byte)1 : (byte)0,
            WaitForVerticalRefreshMode = (int)s.WaitForVerticalRefreshMode,
            FrtcEnabled = s.FrtcEnabled ? (byte)1 : (byte)0,
            FrtcFps = s.FrtcFps
        };
        AdlxNativeInterop.RgSet3DSettingsState(gpuIndex, &raw);
    }

    public Task<HardwareApplyResult> ResetToFactoryAsync(int gpuIndex, CancellationToken ct = default) => EnqueueAsync(() =>
    {
        var res = (AdlxResultCode)AdlxNativeInterop.RgResetGpuTuningToFactory(gpuIndex);
        return new HardwareApplyResult(
            res == AdlxResultCode.Ok,
            res == AdlxResultCode.Ok ? "VERIFIED" : "FAILED",
            res,
            res == AdlxResultCode.Ok ? null : $"ResetToFactory returned {res}",
            "{}");
    }, ct);

    public static bool VerifyFanMatch(FanProfile target, FanTuningHardwareSnapshot? actual)
    {
        if (actual is null || !actual.Supported) return false;
        if (actual.ZeroRpmSupported && actual.ZeroRpmEnabled != target.ZeroRpmEnabled) return false;
        if (actual.Points.Count != target.Points.Count) return false;
        for (int i = 0; i < target.Points.Count; i++)
        {
            if (Math.Abs(actual.Points[i].TemperatureC - target.Points[i].TemperatureC) > 1 ||
                Math.Abs(actual.Points[i].FanSpeedPercent - target.Points[i].FanSpeedPercent) > 1)
            {
                return false;
            }
        }
        return true;
    }

    public static bool VerifyManualMatch(TuningProfile target, ManualTuningSnapshot? actual)
    {
        if (actual is null) return false;
        if (target.EnableClockTuning && actual.GfxValid)
        {
            if (Math.Abs(actual.GfxMinFreqMHz - target.MinCoreClockMHz) > 5 ||
                Math.Abs(actual.GfxMaxFreqMHz - target.MaxCoreClockMHz) > 5)
                return false;
        }
        if (target.EnableVoltageTuning && actual.GfxValid)
        {
            if (Math.Abs(actual.GfxVoltageMv - target.CoreVoltageMv) > 5)
                return false;
        }
        if (target.EnableVramTuning && actual.VramValid)
        {
            if (Math.Abs(actual.VramMaxFreqMHz - target.MaxVramClockMHz) > 5)
                return false;
        }
        if (target.EnablePowerTuning && actual.PowerValid)
        {
            if (Math.Abs(actual.PowerLimitPercent - target.PowerLimitDeltaPercent) > 1)
                return false;
        }
        return true;
    }

    private static IntRange ToRange(AdlxNativeInterop.RgIntRange r) =>
        new(r.MinValue, r.MaxValue, r.Step, r.Supported != 0);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _commandQueue.Writer.TryComplete();
        try { _workerTask.Wait(TimeSpan.FromSeconds(3)); } catch { }
        if (IsInitialized)
        {
            AdlxNativeInterop.RgUnregisterTuningChangedCallback();
            AdlxNativeInterop.RgTerminate();
            IsInitialized = false;
        }
        if (_callbackHandle.IsAllocated)
        {
            _callbackHandle.Free();
        }
        _disposeCts.Dispose();
    }
}
