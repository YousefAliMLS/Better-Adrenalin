namespace RadeonGuard.Core.Models;

public enum AdlxResultCode
{
    Ok = 0,
    AlreadyEnabled = 1,
    AlreadyInitialized = 2,
    Fail = 3,
    InvalidArgs = 4,
    BadVer = 5,
    UnknownInterface = 6,
    Terminated = 7,
    AdlInitError = 8,
    NotFound = 9,
    InvalidObject = 10,
    OrphanObjects = 11,
    NotSupported = 12,
    PendingOperation = 13,
    GpuInactive = 14,
    GpuInUse = 15,
    TimeoutOperation = 16,
    NotActive = 17,
    ResetNeeded = 18
}

public enum SyncOrigin
{
    Unknown = 1,
    Internal = 2,
    External = 3
}

public enum VramTimingMode
{
    Default = 0,
    FastTiming = 1,
    FastTimingLevel2 = 2,
    Automatic = 3,
    Level1 = 4,
    Level2 = 5
}

public enum VSyncMode
{
    AlwaysOff = 0,
    OffUnlessAppSpecifies = 1,
    OnUnlessAppSpecifies = 2,
    AlwaysOn = 3
}

public enum ExecutableRole
{
    PrimaryRenderer,
    LauncherBootstrapper
}

public enum ConflictResolutionPolicy
{
    AutoReapply = 0,
    AlertOnly = 1
}

public enum ZeroRpmOverrideMode
{
    InheritFromFanProfile = 0,
    ForceEnabled = 1,
    ForceDisabled = 2
}

public sealed record IntRange(int MinValue, int MaxValue, int Step, bool Supported);

public sealed record GpuDeviceInfo(
    int GpuIndex,
    int UniqueId,
    uint TotalVramMb,
    int GpuType,
    int AsicFamilyType,
    int PciBusType,
    uint PciLaneWidth,
    string Name,
    string ProductName,
    string VendorId,
    string DeviceId,
    string RevisionId,
    string SubSystemId,
    string SubSystemVendorId,
    string PnpString,
    string DriverPath,
    string VramType,
    string DriverVersion,
    string AmdWindowsDriverVersion,
    string AmdSoftwareVersion,
    string BiosPartNumber,
    string BiosVersion,
    string BiosDate)
{
    /// <summary>
    /// Stable hardware key that survives driver updates and Windows adapter index renumbering.
    /// </summary>
    public string HardwareKey => $"PCI\\VEN_{VendorId}&DEV_{DeviceId}&SUBSYS_{SubSystemId}{SubSystemVendorId}&REV_{RevisionId}";
}

public sealed record GpuTelemetrySample(
    DateTimeOffset CapturedAtUtc,
    long DriverTimestampMs,
    double GpuUsagePercent,
    int GpuClockMHz,
    int VramClockMHz,
    double EdgeTemperatureC,
    double HotspotTemperatureC,
    double IntakeTemperatureC,
    double GpuPowerW,
    double TotalBoardPowerW,
    int FanSpeedRpm,
    int VramUsedMb,
    int VoltageMv,
    uint ValidMask)
{
    public bool HasUsage => (ValidMask & (1u << 0)) != 0;
    public bool HasGpuClock => (ValidMask & (1u << 1)) != 0;
    public bool HasVramClock => (ValidMask & (1u << 2)) != 0;
    public bool HasEdgeTemp => (ValidMask & (1u << 3)) != 0;
    public bool HasHotspotTemp => (ValidMask & (1u << 4)) != 0;
    public bool HasGpuPower => (ValidMask & (1u << 5)) != 0;
    public bool HasTotalBoardPower => (ValidMask & (1u << 6)) != 0;
    public bool HasFanRpm => (ValidMask & (1u << 7)) != 0;
    public bool HasVramUsed => (ValidMask & (1u << 8)) != 0;
    public bool HasVoltage => (ValidMask & (1u << 9)) != 0;
}

public sealed record GpuTuningCapabilities(
    bool ManualGfxTuningSupported,
    bool IsGfxTuningV2,
    bool ManualVramTuningSupported,
    bool IsVramTuningV2,
    bool VramMemoryTimingSupported,
    bool ManualPowerTuningSupported,
    bool TdcLimitSupported,
    bool ManualFanTuningSupported,
    bool AutoTuningSupported,
    bool PresetTuningSupported,
    bool AtFactory,
    IntRange GfxMinFreqRange,
    IntRange GfxMaxFreqRange,
    IntRange GfxVoltageRange,
    IntRange VramMaxFreqRange,
    IntRange PowerLimitRange,
    IntRange TdcLimitRange,
    IntRange FanSpeedRange,
    IntRange FanTempRange,
    bool ZeroRpmSupported,
    bool MinAcousticSupported,
    bool MinFanSpeedSupported,
    bool TargetFanSpeedSupported,
    IntRange MinAcousticRange,
    IntRange MinFanSpeedRange,
    IntRange TargetFanSpeedRange);

public sealed record FanCurvePoint(int TemperatureC, int FanSpeedPercent);

public sealed record FanProfile
{
    public string FanProfileId { get; init; } = Guid.NewGuid().ToString("B");
    public string GpuHardwareKey { get; init; } = string.Empty;
    public string Name { get; init; } = "Default Fan";
    public bool IsDefault { get; init; }
    public bool ZeroRpmEnabled { get; init; } = true;
    public int? MinAcousticLimitMHz { get; init; }
    public IReadOnlyList<FanCurvePoint> Points { get; init; } = Array.Empty<FanCurvePoint>();
    public string CurvePayloadSha256 { get; init; } = string.Empty;
    public int RevisionNumber { get; init; } = 1;
    public DateTimeOffset UpdatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record ThreeDSettings
{
    public bool AntiLagSupported { get; init; }
    public bool AntiLagEnabled { get; init; }
    public bool ChillSupported { get; init; }
    public bool ChillEnabled { get; init; }
    public int ChillMinFps { get; init; } = 60;
    public int ChillMaxFps { get; init; } = 144;
    public IntRange? ChillFpsRange { get; init; }

    public bool BoostSupported { get; init; }
    public bool BoostEnabled { get; init; }
    public int BoostResolution { get; init; } = 83;
    public IntRange? BoostResolutionRange { get; init; }

    public bool ImageSharpeningSupported { get; init; }
    public bool ImageSharpeningEnabled { get; init; }
    public int ImageSharpeningPercent { get; init; } = 80;
    public IntRange? ImageSharpeningRange { get; init; }

    public bool EnhancedSyncSupported { get; init; }
    public bool EnhancedSyncEnabled { get; init; }

    public bool WaitForVerticalRefreshSupported { get; init; }
    public bool WaitForVerticalRefreshEnabled { get; init; }
    public VSyncMode WaitForVerticalRefreshMode { get; init; } = VSyncMode.OffUnlessAppSpecifies;

    public bool FrtcSupported { get; init; }
    public bool FrtcEnabled { get; init; }
    public int FrtcFps { get; init; } = 144;
    public IntRange? FrtcFpsRange { get; init; }

    public bool AfmfSupported { get; init; }
    public bool AfmfEnabled { get; init; }
}

public sealed record ManualTuningSnapshot(
    int GfxMinFreqMHz,
    int GfxMaxFreqMHz,
    int GfxVoltageMv,
    int VramMaxFreqMHz,
    VramTimingMode VramMemoryTiming,
    int PowerLimitPercent,
    int TdcLimitPercent,
    bool GfxValid,
    bool VramValid,
    bool PowerValid,
    bool MemoryTimingValid,
    bool AtFactory);

public sealed record FanTuningHardwareSnapshot(
    bool Supported,
    bool ZeroRpmSupported,
    bool ZeroRpmEnabled,
    bool MinAcousticSupported,
    int MinAcousticLimitMHz,
    IReadOnlyList<FanCurvePoint> Points,
    IntRange SpeedRange,
    IntRange TemperatureRange);

public sealed record TuningProfile
{
    public string TuningProfileId { get; init; } = Guid.NewGuid().ToString("B");
    public string GpuHardwareKey { get; init; } = string.Empty;
    public string Name { get; init; } = "Global Default";
    public bool IsGlobalDefault { get; init; }
    public bool IsQuarantined { get; init; }
    public string? QuarantineReason { get; init; }

    public bool EnableClockTuning { get; init; }
    public int MinCoreClockMHz { get; init; } = 500;
    public int MaxCoreClockMHz { get; init; } = 2400;

    public bool EnableVoltageTuning { get; init; }
    public int CoreVoltageMv { get; init; } = 1150;

    public bool EnableVramTuning { get; init; }
    public int MaxVramClockMHz { get; init; } = 2000;
    public VramTimingMode VramTiming { get; init; } = VramTimingMode.Default;

    public bool EnablePowerTuning { get; init; }
    public int PowerLimitDeltaPercent { get; init; } = 0;

    public string LinkedFanProfileId { get; init; } = string.Empty;
    public ThreeDSettings ThreeDSettings { get; init; } = new();

    public string PayloadSha256 { get; init; } = string.Empty;
    public int RevisionNumber { get; init; } = 1;
    public DateTimeOffset UpdatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record GameExecutable
{
    public long Id { get; init; }
    public string GameProfileId { get; init; } = string.Empty;
    public string ExeFullPath { get; init; } = string.Empty;
    public string ExeFileName { get; init; } = string.Empty;
    public ExecutableRole Role { get; init; } = ExecutableRole.PrimaryRenderer;
}

public sealed record GameProfile
{
    public string GameProfileId { get; init; } = Guid.NewGuid().ToString("B");
    public string Title { get; init; } = string.Empty;
    public string LauncherSource { get; init; } = "Manual";
    public string LauncherAppId { get; init; } = string.Empty;
    public string TuningProfileId { get; init; } = string.Empty;
    public string? OverrideFanProfileId { get; init; }
    public bool IsEnabled { get; init; } = true;
    public bool LockWhileProcessRunning { get; init; } = true;
    public ZeroRpmOverrideMode ZeroRpmOverride { get; init; } = ZeroRpmOverrideMode.InheritFromFanProfile;
    public DateTimeOffset? LastLaunchedUtc { get; init; }
    public IReadOnlyList<GameExecutable> Executables { get; init; } = Array.Empty<GameExecutable>();

    public FanProfile? ApplyZeroRpmOverride(FanProfile? baseFan)
    {
        if (baseFan is null) return null;
        return ZeroRpmOverride switch
        {
            ZeroRpmOverrideMode.ForceEnabled => baseFan with { ZeroRpmEnabled = true },
            ZeroRpmOverrideMode.ForceDisabled => baseFan with { ZeroRpmEnabled = false },
            _ => baseFan
        };
    }
}

public sealed record DenylistEntry(string ExeFileName, string Reason, bool IsBuiltIn);

public sealed record ProfileRevision(
    long RevisionId,
    string EntityType,
    string EntityId,
    int RevisionNumber,
    string SnapshotJson,
    string ChangeReason,
    DateTimeOffset CreatedAtUtc);

public sealed record AppliedStateRecord(
    long LogId,
    string GpuHardwareKey,
    string TargetProfileId,
    string TargetProfileName,
    string TargetFanProfileId,
    string TargetFanProfileName,
    string PersistStatus,
    string ApplyStatus,
    string DriverReadbackJson,
    string? ErrorCode,
    string TriggerReason,
    DateTimeOffset TimestampUtc);

public sealed record TuningChangedNotification(
    int GpuUniqueId,
    SyncOrigin Origin,
    bool IsAutomaticTuningChanged,
    bool IsPresetTuningChanged,
    bool IsManualGfxChanged,
    bool IsManualVramChanged,
    bool IsManualFanChanged,
    bool IsManualPowerChanged);
