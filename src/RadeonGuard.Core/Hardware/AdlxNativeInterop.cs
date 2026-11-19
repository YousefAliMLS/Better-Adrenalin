using System.Runtime.InteropServices;
using System.Text;

namespace RadeonGuard.Core.Hardware;

internal static unsafe partial class AdlxNativeInterop
{
    private const string DllName = "RadeonGuard.AdlxBridge.dll";

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct RgIntRange
    {
        public int MinValue;
        public int MaxValue;
        public int Step;
        public byte Supported;
        public fixed byte Reserved[3];
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct RgGpuInfo
    {
        public int GpuIndex;
        public int UniqueId;
        public uint TotalVramMb;
        public int GpuType;
        public int AsicFamilyType;
        public int PciBusType;
        public uint PciLaneWidth;
        public fixed byte Name[128];
        public fixed byte ProductName[128];
        public fixed byte VendorId[16];
        public fixed byte DeviceId[16];
        public fixed byte RevisionId[16];
        public fixed byte SubSystemId[16];
        public fixed byte SubSystemVendorId[16];
        public fixed byte PnpString[260];
        public fixed byte DriverPath[260];
        public fixed byte VramType[32];
        public fixed byte DriverVersion[64];
        public fixed byte AmdWindowsDriverVersion[64];
        public fixed byte AmdSoftwareVersion[64];
        public fixed byte BiosPartNumber[64];
        public fixed byte BiosVersion[64];
        public fixed byte BiosDate[32];
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct RgGpuMetrics
    {
        public long TimestampMs;
        public double GpuUsagePercent;
        public int GpuClockMHz;
        public int VramClockMHz;
        public double EdgeTemperatureC;
        public double HotspotTemperatureC;
        public double IntakeTemperatureC;
        public double GpuPowerW;
        public double TotalBoardPowerW;
        public int FanSpeedRpm;
        public int VramUsedMb;
        public int VoltageMv;
        public uint ValidMask;
        public uint Reserved;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct RgFanCurvePoint
    {
        public int TemperatureC;
        public int FanSpeedPercent;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct RgFanTuningState
    {
        public byte Supported;
        public byte ZeroRpmSupported;
        public byte ZeroRpmEnabled;
        public byte MinAcousticSupported;
        public byte MinFanSpeedSupported;
        public byte TargetFanSpeedSupported;
        public byte PointCount;
        public byte Reserved;
        public int MinAcousticLimit;
        public int MinFanSpeed;
        public int TargetFanSpeed;
        public RgIntRange SpeedRange;
        public RgIntRange TemperatureRange;
        public RgIntRange MinAcousticRange;
        public RgIntRange MinFanSpeedRange;
        public RgIntRange TargetFanSpeedRange;
        public fixed int PointPairs[16]; // 8 * (temperatureC, fanSpeedPercent)
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct RgGpuTuningCapabilities
    {
        public byte ManualGfxTuningSupported;
        public byte IsGfxTuningV2;
        public byte ManualVramTuningSupported;
        public byte IsVramTuningV2;
        public byte VramMemoryTimingSupported;
        public byte ManualPowerTuningSupported;
        public byte TdcLimitSupported;
        public byte ManualFanTuningSupported;
        public byte AutoTuningSupported;
        public byte PresetTuningSupported;
        public byte AtFactory;
        public byte Reserved;
        public RgIntRange GfxMinFreqRange;
        public RgIntRange GfxMaxFreqRange;
        public RgIntRange GfxVoltageRange;
        public RgIntRange VramMaxFreqRange;
        public RgIntRange PowerLimitRange;
        public RgIntRange TdcLimitRange;
        public RgIntRange FanSpeedRange;
        public RgIntRange FanTempRange;
        public byte ZeroRpmSupported;
        public byte MinAcousticSupported;
        public byte MinFanSpeedSupported;
        public byte TargetFanSpeedSupported;
        public RgIntRange MinAcousticRange;
        public RgIntRange MinFanSpeedRange;
        public RgIntRange TargetFanSpeedRange;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct RgManualTuningState
    {
        public int GfxMinFreqMHz;
        public int GfxMaxFreqMHz;
        public int GfxVoltageMv;
        public int VramMaxFreqMHz;
        public int VramMemoryTiming;
        public int PowerLimitPercent;
        public int TdcLimitPercent;
        public byte GfxValid;
        public byte VramValid;
        public byte PowerValid;
        public byte MemoryTimingValid;
        public byte TdcValid;
        public byte AtFactory;
        public fixed byte Reserved[2];
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct Rg3DSettingsState
    {
        public byte AntiLagSupported;
        public byte AntiLagEnabled;
        public byte ChillSupported;
        public byte ChillEnabled;
        public int ChillMinFps;
        public int ChillMaxFps;
        public RgIntRange ChillFpsRange;

        public byte BoostSupported;
        public byte BoostEnabled;
        public int BoostResolution;
        public RgIntRange BoostResolutionRange;

        public byte ImageSharpeningSupported;
        public byte ImageSharpeningEnabled;
        public int ImageSharpeningPercent;
        public RgIntRange ImageSharpeningRange;

        public byte EnhancedSyncSupported;
        public byte EnhancedSyncEnabled;

        public byte WaitForVerticalRefreshSupported;
        public byte WaitForVerticalRefreshEnabled;
        public int WaitForVerticalRefreshMode;

        public byte FrtcSupported;
        public byte FrtcEnabled;
        public int FrtcFps;
        public RgIntRange FrtcFpsRange;

        public byte AfmfSupported;
        public byte AfmfEnabled;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct RgTuningChangedNotification
    {
        public int GpuUniqueId;
        public int SyncOrigin;
        public byte IsAutomaticTuningChanged;
        public byte IsPresetTuningChanged;
        public byte IsManualGfxChanged;
        public byte IsManualVramChanged;
        public byte IsManualFanChanged;
        public byte IsManualPowerChanged;
        public fixed byte Reserved[2];
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate void RgTuningChangedCallback(RgTuningChangedNotification* notification);

    [LibraryImport(DllName)]
    internal static partial int RgInitialize(ulong* outFullVersion, byte* outVersionStr, int versionStrCap);

    [LibraryImport(DllName)]
    internal static partial int RgTerminate();

    [LibraryImport(DllName)]
    internal static partial int RgGetGpuCount(int* outCount);

    [LibraryImport(DllName)]
    internal static partial int RgGetGpuInfo(int gpuIndex, RgGpuInfo* outInfo);

    [LibraryImport(DllName)]
    internal static partial int RgGetCurrentGpuMetrics(int gpuIndex, RgGpuMetrics* outMetrics);

    [LibraryImport(DllName)]
    internal static partial int RgGetTuningCapabilities(int gpuIndex, RgGpuTuningCapabilities* outCaps);

    [LibraryImport(DllName)]
    internal static partial int RgGetFanTuningState(int gpuIndex, RgFanTuningState* outState);

    [LibraryImport(DllName)]
    internal static partial int RgGetDefaultFanTuningState(int gpuIndex, RgFanTuningState* outState);

    [LibraryImport(DllName)]
    internal static partial int RgGetManualTuningState(int gpuIndex, RgManualTuningState* outState);

    [LibraryImport(DllName)]
    internal static partial int RgGetDefaultManualTuningState(int gpuIndex, RgManualTuningState* outState);

    [LibraryImport(DllName)]
    internal static partial int RgGet3DSettingsState(int gpuIndex, Rg3DSettingsState* outState);

    [LibraryImport(DllName)]
    internal static partial int RgSetFanTuningState(
        int gpuIndex,
        RgFanCurvePoint* points,
        int pointCount,
        byte enableZeroRpm,
        int minAcousticLimit,
        byte applyMinAcoustic,
        int* outValidationErrorIndex);

    [LibraryImport(DllName)]
    internal static partial int RgSetManualGfxTuning(int gpuIndex, int minFreqMHz, int maxFreqMHz, int voltageMv);

    [LibraryImport(DllName)]
    internal static partial int RgSetManualVramTuning(int gpuIndex, int maxFreqMHz, int memoryTiming);

    [LibraryImport(DllName)]
    internal static partial int RgSetManualPowerTuning(int gpuIndex, int powerLimitPercent);

    [LibraryImport(DllName)]
    internal static partial int RgResetGpuTuningToFactory(int gpuIndex);

    [LibraryImport(DllName)]
    internal static partial int RgSet3DSettingsState(int gpuIndex, Rg3DSettingsState* state);

    [LibraryImport(DllName)]
    internal static partial int RgRegisterTuningChangedCallback(nint callbackPtr);

    [LibraryImport(DllName)]
    internal static partial int RgUnregisterTuningChangedCallback();

    internal static string ReadFixedUtf8(byte* ptr, int maxLen)
    {
        int len = 0;
        while (len < maxLen && ptr[len] != 0)
        {
            len++;
        }
        return len == 0 ? string.Empty : Encoding.UTF8.GetString(ptr, len);
    }
}
