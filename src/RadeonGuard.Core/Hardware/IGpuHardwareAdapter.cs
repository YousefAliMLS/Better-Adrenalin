using RadeonGuard.Core.Models;

namespace RadeonGuard.Core.Hardware;

public sealed record HardwareApplyResult(
    bool Success,
    string ApplyStatus, // VERIFIED | APPLIED | FAILED | REJECTED
    AdlxResultCode ResultCode,
    string? ErrorMessage,
    string ReadbackSummaryJson);

public interface IGpuHardwareAdapter : IDisposable
{
    bool IsInitialized { get; }
    string AdlxVersionString { get; }
    event EventHandler<TuningChangedNotification>? TuningChanged;

    Task<AdlxResultCode> InitializeAsync(CancellationToken ct = default);
    Task<int> GetGpuCountAsync(CancellationToken ct = default);
    Task<GpuDeviceInfo?> GetGpuInfoAsync(int gpuIndex, CancellationToken ct = default);
    Task<GpuTelemetrySample?> GetCurrentMetricsAsync(int gpuIndex, CancellationToken ct = default);
    Task<GpuTuningCapabilities?> GetTuningCapabilitiesAsync(int gpuIndex, CancellationToken ct = default);

    Task<FanTuningHardwareSnapshot?> GetFanTuningStateAsync(int gpuIndex, bool defaultState = false, CancellationToken ct = default);
    Task<ManualTuningSnapshot?> GetManualTuningStateAsync(int gpuIndex, bool defaultState = false, CancellationToken ct = default);
    Task<ThreeDSettings?> Get3DSettingsStateAsync(int gpuIndex, CancellationToken ct = default);

    Task<HardwareApplyResult> ApplyFanProfileAsync(int gpuIndex, FanProfile profile, CancellationToken ct = default);
    Task<HardwareApplyResult> ApplyTuningProfileAsync(int gpuIndex, TuningProfile profile, FanProfile? fanProfile, CancellationToken ct = default);
    Task<HardwareApplyResult> ResetToFactoryAsync(int gpuIndex, CancellationToken ct = default);
}
