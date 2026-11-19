#pragma once

#include <cstdint>

#ifdef RADEONGUARD_ADLXBRIDGE_EXPORTS
#define RG_API __declspec(dllexport)
#else
#define RG_API __declspec(dllimport)
#endif

extern "C" {

#pragma pack(push, 8)

struct RgIntRange {
    int32_t minValue;
    int32_t maxValue;
    int32_t step;
    uint8_t supported;
    uint8_t reserved[3];
};

struct RgGpuInfo {
    int32_t gpuIndex;
    int32_t uniqueId;
    uint32_t totalVramMb;
    int32_t gpuType;        // ADLX_GPU_TYPE
    int32_t asicFamilyType; // ADLX_ASIC_FAMILY_TYPE
    int32_t pciBusType;     // ADLX_PCI_BUS_TYPE
    uint32_t pciLaneWidth;
    char name[128];
    char productName[128];
    char vendorId[16];
    char deviceId[16];
    char revisionId[16];
    char subSystemId[16];
    char subSystemVendorId[16];
    char pnpString[260];
    char driverPath[260];
    char vramType[32];
    char driverVersion[64];
    char amdWindowsDriverVersion[64];
    char amdSoftwareVersion[64];
    char biosPartNumber[64];
    char biosVersion[64];
    char biosDate[32];
};

struct RgGpuMetrics {
    int64_t timestampMs;
    double gpuUsagePercent;
    int32_t gpuClockMHz;
    int32_t vramClockMHz;
    double edgeTemperatureC;
    double hotspotTemperatureC;
    double intakeTemperatureC;
    double gpuPowerW;
    double totalBoardPowerW;
    int32_t fanSpeedRpm;
    int32_t vramUsedMb;
    int32_t voltageMv;
    uint32_t validMask; // bit flags for which fields succeeded
    uint32_t reserved;
};

struct RgFanCurvePoint {
    int32_t temperatureC;
    int32_t fanSpeedPercent;
};

struct RgFanTuningState {
    uint8_t supported;
    uint8_t zeroRpmSupported;
    uint8_t zeroRpmEnabled;
    uint8_t minAcousticSupported;
    uint8_t minFanSpeedSupported;
    uint8_t targetFanSpeedSupported;
    uint8_t pointCount;
    uint8_t reserved;
    int32_t minAcousticLimit;
    int32_t minFanSpeed;
    int32_t targetFanSpeed;
    RgIntRange speedRange;
    RgIntRange temperatureRange;
    RgIntRange minAcousticRange;
    RgIntRange minFanSpeedRange;
    RgIntRange targetFanSpeedRange;
    RgFanCurvePoint points[8];
};

struct RgGpuTuningCapabilities {
    uint8_t manualGfxTuningSupported;
    uint8_t isGfxTuningV2; // 1 = RDNA+ (IADLXManualGraphicsTuning2), 0 = pre-Navi
    uint8_t manualVramTuningSupported;
    uint8_t isVramTuningV2;
    uint8_t vramMemoryTimingSupported;
    uint8_t manualPowerTuningSupported;
    uint8_t tdcLimitSupported;
    uint8_t manualFanTuningSupported;
    uint8_t autoTuningSupported;
    uint8_t presetTuningSupported;
    uint8_t atFactory;
    uint8_t reserved;
    RgIntRange gfxMinFreqRange;
    RgIntRange gfxMaxFreqRange;
    RgIntRange gfxVoltageRange;
    RgIntRange vramMaxFreqRange;
    RgIntRange powerLimitRange;
    RgIntRange tdcLimitRange;
    RgIntRange fanSpeedRange;
    RgIntRange fanTempRange;
    uint8_t zeroRpmSupported;
    uint8_t minAcousticSupported;
    uint8_t minFanSpeedSupported;
    uint8_t targetFanSpeedSupported;
    RgIntRange minAcousticRange;
    RgIntRange minFanSpeedRange;
    RgIntRange targetFanSpeedRange;
};

struct RgManualTuningState {
    int32_t gfxMinFreqMHz;
    int32_t gfxMaxFreqMHz;
    int32_t gfxVoltageMv;
    int32_t vramMaxFreqMHz;
    int32_t vramMemoryTiming; // ADLX_MEMORYTIMING_DESCRIPTION
    int32_t powerLimitPercent;
    int32_t tdcLimitPercent;
    uint8_t gfxValid;
    uint8_t vramValid;
    uint8_t powerValid;
    uint8_t memoryTimingValid;
    uint8_t tdcValid;
    uint8_t atFactory;
    uint8_t reserved[2];
};

struct Rg3DSettingsState {
    uint8_t antiLagSupported;
    uint8_t antiLagEnabled;
    uint8_t chillSupported;
    uint8_t chillEnabled;
    int32_t chillMinFps;
    int32_t chillMaxFps;
    RgIntRange chillFpsRange;

    uint8_t boostSupported;
    uint8_t boostEnabled;
    int32_t boostResolution;
    RgIntRange boostResolutionRange;

    uint8_t imageSharpeningSupported;
    uint8_t imageSharpeningEnabled;
    int32_t imageSharpeningPercent;
    RgIntRange imageSharpeningRange;

    uint8_t enhancedSyncSupported;
    uint8_t enhancedSyncEnabled;

    uint8_t waitForVerticalRefreshSupported;
    uint8_t waitForVerticalRefreshEnabled;
    int32_t waitForVerticalRefreshMode; // ADLX_WAIT_FOR_VERTICAL_REFRESH_MODE

    uint8_t frtcSupported;
    uint8_t frtcEnabled;
    int32_t frtcFps;
    RgIntRange frtcFpsRange;

    uint8_t afmfSupported;
    uint8_t afmfEnabled;
};

struct RgTuningChangedNotification {
    int32_t gpuUniqueId;
    int32_t syncOrigin; // ADLX_SYNC_ORIGIN (1=Unknown, 2=Internal, 3=External)
    uint8_t isAutomaticTuningChanged;
    uint8_t isPresetTuningChanged;
    uint8_t isManualGfxChanged;
    uint8_t isManualVramChanged;
    uint8_t isManualFanChanged;
    uint8_t isManualPowerChanged;
    uint8_t reserved[2];
};

#pragma pack(pop)

typedef void(__stdcall* RgTuningChangedCallback)(const RgTuningChangedNotification* notification);

// Lifecycle & versioning
RG_API int32_t __stdcall RgInitialize(uint64_t* outFullVersion, char* outVersionStr, int32_t versionStrCap);
RG_API int32_t __stdcall RgTerminate();
RG_API int32_t __stdcall RgGetGpuCount(int32_t* outCount);
RG_API int32_t __stdcall RgGetGpuInfo(int32_t gpuIndex, RgGpuInfo* outInfo);

// Read-only telemetry & capabilities
RG_API int32_t __stdcall RgGetCurrentGpuMetrics(int32_t gpuIndex, RgGpuMetrics* outMetrics);
RG_API int32_t __stdcall RgGetTuningCapabilities(int32_t gpuIndex, RgGpuTuningCapabilities* outCaps);
RG_API int32_t __stdcall RgGetFanTuningState(int32_t gpuIndex, RgFanTuningState* outState);
RG_API int32_t __stdcall RgGetDefaultFanTuningState(int32_t gpuIndex, RgFanTuningState* outState);
RG_API int32_t __stdcall RgGetManualTuningState(int32_t gpuIndex, RgManualTuningState* outState);
RG_API int32_t __stdcall RgGetDefaultManualTuningState(int32_t gpuIndex, RgManualTuningState* outState);
RG_API int32_t __stdcall RgGet3DSettingsState(int32_t gpuIndex, Rg3DSettingsState* outState);

// Write operations (only invoked via explicit user/guardian action after validation)
RG_API int32_t __stdcall RgSetFanTuningState(int32_t gpuIndex, const RgFanCurvePoint* points, int32_t pointCount, uint8_t enableZeroRpm, int32_t minAcousticLimit, uint8_t applyMinAcoustic, int32_t* outValidationErrorIndex);
RG_API int32_t __stdcall RgSetManualGfxTuning(int32_t gpuIndex, int32_t minFreqMHz, int32_t maxFreqMHz, int32_t voltageMv);
RG_API int32_t __stdcall RgSetManualVramTuning(int32_t gpuIndex, int32_t maxFreqMHz, int32_t memoryTiming);
RG_API int32_t __stdcall RgSetManualPowerTuning(int32_t gpuIndex, int32_t powerLimitPercent);
RG_API int32_t __stdcall RgResetGpuTuningToFactory(int32_t gpuIndex);
RG_API int32_t __stdcall RgSet3DSettingsState(int32_t gpuIndex, const Rg3DSettingsState* state);

// Event listener registration
RG_API int32_t __stdcall RgRegisterTuningChangedCallback(RgTuningChangedCallback callback);
RG_API int32_t __stdcall RgUnregisterTuningChangedCallback();

} // extern "C"
