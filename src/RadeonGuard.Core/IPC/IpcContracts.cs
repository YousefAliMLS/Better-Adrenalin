using RadeonGuard.Core.Models;

namespace RadeonGuard.Core.IPC;

public sealed record IpcRequest(string Command, string? PayloadJson = null);

public sealed record IpcResponse(bool Success, string? ErrorMessage = null, string? PayloadJson = null);

public sealed record GuardianStatusSnapshot(
    bool GuardianRunning,
    bool AdlxInitialized,
    string AdlxVersion,
    GpuDeviceInfo? GpuInfo,
    GpuTelemetrySample? LatestMetrics,
    GpuTuningCapabilities? Capabilities,
    FanTuningHardwareSnapshot? LiveFanState,
    ManualTuningSnapshot? LiveManualState,
    ThreeDSettings? LiveThreeDState,
    string ActiveTuningProfileId,
    string ActiveTuningProfileName,
    string ActiveFanProfileId,
    string ActiveFanProfileName,
    string? ActiveGameTitle,
    int? ActiveGamePid,
    string HardwareSyncState, // VERIFIED | APPLIED | DRIFT_DETECTED | RECOVERING | ERROR
    ConflictResolutionPolicy ConflictPolicy,
    DateTimeOffset CapturedAtUtc);

public static class IpcConstants
{
    public const string PipeName = "RadeonGuard.IPC";
}
