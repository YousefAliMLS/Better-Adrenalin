#ifndef RADEONGUARD_ADLXBRIDGE_EXPORTS
#define RADEONGUARD_ADLXBRIDGE_EXPORTS
#endif
#include "AdlxBridge.h"

#include "SDK/ADLXHelper/Windows/Cpp/ADLXHelper.h"
#include "SDK/Include/ISystem.h"
#include "SDK/Include/ISystem1.h"
#include "SDK/Include/ISystem2.h"
#include "SDK/Include/IPerformanceMonitoring.h"
#include "SDK/Include/IGPUTuning.h"
#include "SDK/Include/IGPUTuning1.h"
#include "SDK/Include/IGPUManualFanTuning.h"
#include "SDK/Include/IGPUManualGFXTuning.h"
#include "SDK/Include/IGPUManualVRAMTuning.h"
#include "SDK/Include/IGPUManualPowerTuning.h"
#include "SDK/Include/I3DSettings.h"
#include "SDK/Include/I3DSettings1.h"

#include <algorithm>
#include <atomic>
#include <cstring>
#include <mutex>

using namespace adlx;

namespace {

class CompatibleAdlxHelper : public ADLXHelper {
public:
    ADLX_RESULT InitializeNegotiated() {
        ADLX_RESULT res = Initialize();
        if (res == ADLX_BAD_VER && m_hDLLHandle != nullptr && m_fullVersionFn != nullptr) {
            adlx_uint64 dllFullVer = 0;
            if (ADLX_SUCCEEDED(m_fullVersionFn(&dllFullVer)) && dllFullVer > 0) {
                adlx_uint64 targetVer = (std::min)(static_cast<adlx_uint64>(ADLX_FULL_VERSION), dllFullVer);
                m_ADLXFullVersion = dllFullVer;
                if (m_versionFn) {
                    m_versionFn(&m_ADLXVersion);
                }
                if (m_init2Fn) {
                    res = m_init2Fn(targetVer, &m_pSystemServices, &m_pAdlMapping);
                } else if (m_initFn) {
                    res = m_initFn(targetVer, &m_pSystemServices);
                }
            }
        }
        return res;
    }
};

std::mutex g_lock;
CompatibleAdlxHelper g_adlx;
bool g_initialized = false;
std::atomic<RgTuningChangedCallback> g_tuningCallback{nullptr};

class TuningChangedListener : public IADLXGPUTuningChangedListener {
public:
    adlx_bool ADLX_STD_CALL OnGPUTuningChanged(IADLXGPUTuningChangedEvent* pEvent) override {
        auto cb = g_tuningCallback.load(std::memory_order_acquire);
        if (cb != nullptr && pEvent != nullptr) {
            RgTuningChangedNotification note{};
            note.syncOrigin = static_cast<int32_t>(pEvent->GetOrigin());
            note.isAutomaticTuningChanged = pEvent->IsAutomaticTuningChanged() ? 1 : 0;
            note.isPresetTuningChanged = pEvent->IsPresetTuningChanged() ? 1 : 0;
            note.isManualGfxChanged = pEvent->IsManualGPUCLKTuningChanged() ? 1 : 0;
            note.isManualVramChanged = pEvent->IsManualVRAMTuningChanged() ? 1 : 0;
            note.isManualFanChanged = pEvent->IsManualFanTuningChanged() ? 1 : 0;
            note.isManualPowerChanged = pEvent->IsManualPowerTuningChanged() ? 1 : 0;

            IADLXGPUPtr gpu;
            if (ADLX_SUCCEEDED(pEvent->GetGPU(&gpu)) && gpu != nullptr) {
                adlx_int uid = 0;
                if (ADLX_SUCCEEDED(gpu->UniqueId(&uid))) {
                    note.gpuUniqueId = uid;
                }
            }
            cb(&note);
        }
        return true;
    }
};

TuningChangedListener g_tuningListener;
IADLXGPUTuningChangedHandlingPtr g_tuningChangedHandling;
bool g_listenerRegistered = false;

template <size_t N>
void SafeCopyStr(char (&dest)[N], const char* src) {
    if (src == nullptr) {
        dest[0] = '\0';
        return;
    }
    strncpy_s(dest, N, src, _TRUNCATE);
}

void FillRange(RgIntRange& dst, const ADLX_IntRange& src, bool supported = true) {
    dst.minValue = src.minValue;
    dst.maxValue = src.maxValue;
    dst.step = src.step;
    dst.supported = supported ? 1 : 0;
}

ADLX_RESULT GetGpuByIndex(int32_t gpuIndex, IADLXGPUPtr& outGpu) {
    if (!g_initialized || g_adlx.GetSystemServices() == nullptr) {
        return ADLX_TERMINATED;
    }
    IADLXGPUListPtr gpus;
    ADLX_RESULT res = g_adlx.GetSystemServices()->GetGPUs(&gpus);
    if (ADLX_FAILED(res) || gpus == nullptr) {
        return res;
    }
    if (gpuIndex < 0 || static_cast<adlx_uint>(gpuIndex) >= gpus->Size()) {
        return ADLX_INVALID_ARGS;
    }
    return gpus->At(static_cast<adlx_uint>(gpuIndex), &outGpu);
}

void ReadFanStatesInto(IADLXManualFanTuningStateList* states, RgFanTuningState* outState) {
    if (states == nullptr || outState == nullptr) return;
    adlx_uint count = states->Size();
    if (count > 8) count = 8;
    outState->pointCount = static_cast<uint8_t>(count);
    for (adlx_uint i = 0; i < count; ++i) {
        IADLXManualFanTuningStatePtr st;
        if (ADLX_SUCCEEDED(states->At(i, &st)) && st != nullptr) {
            adlx_int temp = 0, spd = 0;
            st->GetTemperature(&temp);
            st->GetFanSpeed(&spd);
            outState->points[i].temperatureC = temp;
            outState->points[i].fanSpeedPercent = spd;
        }
    }
}

} // namespace

extern "C" {

RG_API int32_t __stdcall RgInitialize(uint64_t* outFullVersion, char* outVersionStr, int32_t versionStrCap) {
    std::lock_guard<std::mutex> guard(g_lock);
    if (g_initialized) {
        if (outFullVersion) *outFullVersion = g_adlx.QueryFullVersion();
        if (outVersionStr && versionStrCap > 0 && g_adlx.QueryVersion()) {
            strncpy_s(outVersionStr, static_cast<size_t>(versionStrCap), g_adlx.QueryVersion(), _TRUNCATE);
        }
        return ADLX_OK;
    }

    ADLX_RESULT res = g_adlx.InitializeNegotiated();
    if (ADLX_FAILED(res)) {
        return res;
    }

    g_initialized = true;
    if (outFullVersion) {
        *outFullVersion = g_adlx.QueryFullVersion();
    }
    if (outVersionStr && versionStrCap > 0) {
        const char* ver = g_adlx.QueryVersion();
        if (ver != nullptr) {
            strncpy_s(outVersionStr, static_cast<size_t>(versionStrCap), ver, _TRUNCATE);
        } else {
            outVersionStr[0] = '\0';
        }
    }
    return ADLX_OK;
}

RG_API int32_t __stdcall RgTerminate() {
    std::lock_guard<std::mutex> guard(g_lock);
    if (!g_initialized) {
        return ADLX_OK;
    }

    g_tuningCallback.store(nullptr, std::memory_order_release);
    if (g_listenerRegistered && g_tuningChangedHandling != nullptr) {
        g_tuningChangedHandling->RemoveGPUTuningEventListener(&g_tuningListener);
        g_listenerRegistered = false;
    }
    g_tuningChangedHandling = nullptr;

    ADLX_RESULT res = g_adlx.Terminate();
    g_initialized = false;
    return res;
}

RG_API int32_t __stdcall RgGetGpuCount(int32_t* outCount) {
    if (!outCount) return ADLX_INVALID_ARGS;
    std::lock_guard<std::mutex> guard(g_lock);
    if (!g_initialized || g_adlx.GetSystemServices() == nullptr) return ADLX_TERMINATED;

    IADLXGPUListPtr gpus;
    ADLX_RESULT res = g_adlx.GetSystemServices()->GetGPUs(&gpus);
    if (ADLX_FAILED(res) || gpus == nullptr) return res;
    *outCount = static_cast<int32_t>(gpus->Size());
    return ADLX_OK;
}

RG_API int32_t __stdcall RgGetGpuInfo(int32_t gpuIndex, RgGpuInfo* outInfo) {
    if (!outInfo) return ADLX_INVALID_ARGS;
    std::memset(outInfo, 0, sizeof(RgGpuInfo));

    std::lock_guard<std::mutex> guard(g_lock);
    IADLXGPUPtr gpu;
    ADLX_RESULT res = GetGpuByIndex(gpuIndex, gpu);
    if (ADLX_FAILED(res) || gpu == nullptr) return res;

    outInfo->gpuIndex = gpuIndex;
    adlx_int uid = 0;
    if (ADLX_SUCCEEDED(gpu->UniqueId(&uid))) outInfo->uniqueId = uid;

    adlx_uint vramMb = 0;
    if (ADLX_SUCCEEDED(gpu->TotalVRAM(&vramMb))) outInfo->totalVramMb = vramMb;

    ADLX_GPU_TYPE gpuType = GPUTYPE_UNDEFINED;
    if (ADLX_SUCCEEDED(gpu->Type(&gpuType))) outInfo->gpuType = static_cast<int32_t>(gpuType);

    ADLX_ASIC_FAMILY_TYPE asicType = ASIC_UNDEFINED;
    if (ADLX_SUCCEEDED(gpu->ASICFamilyType(&asicType))) outInfo->asicFamilyType = static_cast<int32_t>(asicType);

    const char* str = nullptr;
    if (ADLX_SUCCEEDED(gpu->Name(&str))) SafeCopyStr(outInfo->name, str);
    if (ADLX_SUCCEEDED(gpu->VendorId(&str))) SafeCopyStr(outInfo->vendorId, str);
    if (ADLX_SUCCEEDED(gpu->DeviceId(&str))) SafeCopyStr(outInfo->deviceId, str);
    if (ADLX_SUCCEEDED(gpu->RevisionId(&str))) SafeCopyStr(outInfo->revisionId, str);
    if (ADLX_SUCCEEDED(gpu->SubSystemId(&str))) SafeCopyStr(outInfo->subSystemId, str);
    if (ADLX_SUCCEEDED(gpu->SubSystemVendorId(&str))) SafeCopyStr(outInfo->subSystemVendorId, str);
    if (ADLX_SUCCEEDED(gpu->PNPString(&str))) SafeCopyStr(outInfo->pnpString, str);
    if (ADLX_SUCCEEDED(gpu->DriverPath(&str))) SafeCopyStr(outInfo->driverPath, str);
    if (ADLX_SUCCEEDED(gpu->VRAMType(&str))) SafeCopyStr(outInfo->vramType, str);

    const char* part = nullptr;
    const char* ver = nullptr;
    const char* date = nullptr;
    if (ADLX_SUCCEEDED(gpu->BIOSInfo(&part, &ver, &date))) {
        SafeCopyStr(outInfo->biosPartNumber, part);
        SafeCopyStr(outInfo->biosVersion, ver);
        SafeCopyStr(outInfo->biosDate, date);
    }

    IADLXGPU1Ptr gpu1(gpu);
    if (gpu1 != nullptr) {
        ADLX_PCI_BUS_TYPE busType = UNDEFINED;
        if (ADLX_SUCCEEDED(gpu1->PCIBusType(&busType))) outInfo->pciBusType = static_cast<int32_t>(busType);
        adlx_uint lanes = 0;
        if (ADLX_SUCCEEDED(gpu1->PCIBusLaneWidth(&lanes))) outInfo->pciLaneWidth = lanes;
        if (ADLX_SUCCEEDED(gpu1->ProductName(&str))) SafeCopyStr(outInfo->productName, str);
    }

    IADLXGPU2Ptr gpu2(gpu);
    if (gpu2 != nullptr) {
        if (ADLX_SUCCEEDED(gpu2->DriverVersion(&str))) SafeCopyStr(outInfo->driverVersion, str);
        if (ADLX_SUCCEEDED(gpu2->AMDWindowsDriverVersion(&str))) SafeCopyStr(outInfo->amdWindowsDriverVersion, str);
        if (ADLX_SUCCEEDED(gpu2->AMDSoftwareVersion(&str))) SafeCopyStr(outInfo->amdSoftwareVersion, str);
    }

    return ADLX_OK;
}

RG_API int32_t __stdcall RgGetCurrentGpuMetrics(int32_t gpuIndex, RgGpuMetrics* outMetrics) {
    if (!outMetrics) return ADLX_INVALID_ARGS;
    std::memset(outMetrics, 0, sizeof(RgGpuMetrics));

    std::lock_guard<std::mutex> guard(g_lock);
    IADLXGPUPtr gpu;
    ADLX_RESULT res = GetGpuByIndex(gpuIndex, gpu);
    if (ADLX_FAILED(res) || gpu == nullptr) return res;

    IADLXPerformanceMonitoringServicesPtr perf;
    res = g_adlx.GetSystemServices()->GetPerformanceMonitoringServices(&perf);
    if (ADLX_FAILED(res) || perf == nullptr) return res;

    IADLXGPUMetricsPtr m;
    res = perf->GetCurrentGPUMetrics(gpu, &m);
    if (ADLX_FAILED(res) || m == nullptr) return res;

    adlx_int64 ts = 0;
    if (ADLX_SUCCEEDED(m->TimeStamp(&ts))) outMetrics->timestampMs = ts;

    adlx_double dVal = 0.0;
    adlx_int iVal = 0;
    uint32_t mask = 0;

    if (ADLX_SUCCEEDED(m->GPUUsage(&dVal))) { outMetrics->gpuUsagePercent = dVal; mask |= (1u << 0); }
    if (ADLX_SUCCEEDED(m->GPUClockSpeed(&iVal))) { outMetrics->gpuClockMHz = iVal; mask |= (1u << 1); }
    if (ADLX_SUCCEEDED(m->GPUVRAMClockSpeed(&iVal))) { outMetrics->vramClockMHz = iVal; mask |= (1u << 2); }
    if (ADLX_SUCCEEDED(m->GPUTemperature(&dVal))) { outMetrics->edgeTemperatureC = dVal; mask |= (1u << 3); }
    if (ADLX_SUCCEEDED(m->GPUHotspotTemperature(&dVal))) { outMetrics->hotspotTemperatureC = dVal; mask |= (1u << 4); }
    if (ADLX_SUCCEEDED(m->GPUPower(&dVal))) { outMetrics->gpuPowerW = dVal; mask |= (1u << 5); }
    if (ADLX_SUCCEEDED(m->GPUTotalBoardPower(&dVal))) { outMetrics->totalBoardPowerW = dVal; mask |= (1u << 6); }
    if (ADLX_SUCCEEDED(m->GPUFanSpeed(&iVal))) { outMetrics->fanSpeedRpm = iVal; mask |= (1u << 7); }
    if (ADLX_SUCCEEDED(m->GPUVRAM(&iVal))) { outMetrics->vramUsedMb = iVal; mask |= (1u << 8); }
    if (ADLX_SUCCEEDED(m->GPUVoltage(&iVal))) { outMetrics->voltageMv = iVal; mask |= (1u << 9); }
    if (ADLX_SUCCEEDED(m->GPUIntakeTemperature(&dVal))) { outMetrics->intakeTemperatureC = dVal; mask |= (1u << 10); }

    outMetrics->validMask = mask;
    return ADLX_OK;
}

RG_API int32_t __stdcall RgGetTuningCapabilities(int32_t gpuIndex, RgGpuTuningCapabilities* outCaps) {
    if (!outCaps) return ADLX_INVALID_ARGS;
    std::memset(outCaps, 0, sizeof(RgGpuTuningCapabilities));

    std::lock_guard<std::mutex> guard(g_lock);
    IADLXGPUPtr gpu;
    ADLX_RESULT res = GetGpuByIndex(gpuIndex, gpu);
    if (ADLX_FAILED(res) || gpu == nullptr) return res;

    IADLXGPUTuningServicesPtr tuning;
    res = g_adlx.GetSystemServices()->GetGPUTuningServices(&tuning);
    if (ADLX_FAILED(res) || tuning == nullptr) return res;

    adlx_bool b = false;
    if (ADLX_SUCCEEDED(tuning->IsAtFactory(gpu, &b))) outCaps->atFactory = b ? 1 : 0;
    if (ADLX_SUCCEEDED(tuning->IsSupportedAutoTuning(gpu, &b))) outCaps->autoTuningSupported = b ? 1 : 0;
    if (ADLX_SUCCEEDED(tuning->IsSupportedPresetTuning(gpu, &b))) outCaps->presetTuningSupported = b ? 1 : 0;

    if (ADLX_SUCCEEDED(tuning->IsSupportedManualGFXTuning(gpu, &b)) && b) {
        outCaps->manualGfxTuningSupported = 1;
        IADLXInterfacePtr ifc;
        if (ADLX_SUCCEEDED(tuning->GetManualGFXTuning(gpu, &ifc)) && ifc != nullptr) {
            IADLXManualGraphicsTuning2Ptr gfx2(ifc);
            if (gfx2 != nullptr) {
                outCaps->isGfxTuningV2 = 1;
                ADLX_IntRange r{};
                if (ADLX_SUCCEEDED(gfx2->GetGPUMinFrequencyRange(&r))) FillRange(outCaps->gfxMinFreqRange, r);
                if (ADLX_SUCCEEDED(gfx2->GetGPUMaxFrequencyRange(&r))) FillRange(outCaps->gfxMaxFreqRange, r);
                if (ADLX_SUCCEEDED(gfx2->GetGPUVoltageRange(&r))) FillRange(outCaps->gfxVoltageRange, r);
            } else {
                IADLXManualGraphicsTuning1Ptr gfx1(ifc);
                if (gfx1 != nullptr) {
                    ADLX_IntRange fr{}, vr{};
                    if (ADLX_SUCCEEDED(gfx1->GetGPUTuningRanges(&fr, &vr))) {
                        FillRange(outCaps->gfxMaxFreqRange, fr);
                        FillRange(outCaps->gfxVoltageRange, vr);
                    }
                }
            }
        }
    }

    if (ADLX_SUCCEEDED(tuning->IsSupportedManualVRAMTuning(gpu, &b)) && b) {
        outCaps->manualVramTuningSupported = 1;
        IADLXInterfacePtr ifc;
        if (ADLX_SUCCEEDED(tuning->GetManualVRAMTuning(gpu, &ifc)) && ifc != nullptr) {
            IADLXManualVRAMTuning2Ptr vram2(ifc);
            if (vram2 != nullptr) {
                outCaps->isVramTuningV2 = 1;
                adlx_bool mtSupported = false;
                if (ADLX_SUCCEEDED(vram2->IsSupportedMemoryTiming(&mtSupported))) {
                    outCaps->vramMemoryTimingSupported = mtSupported ? 1 : 0;
                }
                ADLX_IntRange r{};
                if (ADLX_SUCCEEDED(vram2->GetMaxVRAMFrequencyRange(&r))) FillRange(outCaps->vramMaxFreqRange, r);
            }
        }
    }

    if (ADLX_SUCCEEDED(tuning->IsSupportedManualPowerTuning(gpu, &b)) && b) {
        outCaps->manualPowerTuningSupported = 1;
        IADLXInterfacePtr ifc;
        if (ADLX_SUCCEEDED(tuning->GetManualPowerTuning(gpu, &ifc)) && ifc != nullptr) {
            IADLXManualPowerTuningPtr pwr(ifc);
            if (pwr != nullptr) {
                ADLX_IntRange r{};
                if (ADLX_SUCCEEDED(pwr->GetPowerLimitRange(&r))) FillRange(outCaps->powerLimitRange, r);
                adlx_bool tdcSupported = false;
                if (ADLX_SUCCEEDED(pwr->IsSupportedTDCLimit(&tdcSupported)) && tdcSupported) {
                    outCaps->tdcLimitSupported = 1;
                    if (ADLX_SUCCEEDED(pwr->GetTDCLimitRange(&r))) FillRange(outCaps->tdcLimitRange, r);
                }
            }
        }
    }

    if (ADLX_SUCCEEDED(tuning->IsSupportedManualFanTuning(gpu, &b)) && b) {
        outCaps->manualFanTuningSupported = 1;
        IADLXInterfacePtr ifc;
        if (ADLX_SUCCEEDED(tuning->GetManualFanTuning(gpu, &ifc)) && ifc != nullptr) {
            IADLXManualFanTuningPtr fan(ifc);
            if (fan != nullptr) {
                ADLX_IntRange sr{}, tr{};
                if (ADLX_SUCCEEDED(fan->GetFanTuningRanges(&sr, &tr))) {
                    FillRange(outCaps->fanSpeedRange, sr);
                    FillRange(outCaps->fanTempRange, tr);
                }
                adlx_bool sup = false;
                if (ADLX_SUCCEEDED(fan->IsSupportedZeroRPM(&sup))) outCaps->zeroRpmSupported = sup ? 1 : 0;
                if (ADLX_SUCCEEDED(fan->IsSupportedMinAcousticLimit(&sup)) && sup) {
                    outCaps->minAcousticSupported = 1;
                    ADLX_IntRange r{};
                    if (ADLX_SUCCEEDED(fan->GetMinAcousticLimitRange(&r))) FillRange(outCaps->minAcousticRange, r);
                }
                if (ADLX_SUCCEEDED(fan->IsSupportedMinFanSpeed(&sup)) && sup) {
                    outCaps->minFanSpeedSupported = 1;
                    ADLX_IntRange r{};
                    if (ADLX_SUCCEEDED(fan->GetMinFanSpeedRange(&r))) FillRange(outCaps->minFanSpeedRange, r);
                }
                if (ADLX_SUCCEEDED(fan->IsSupportedTargetFanSpeed(&sup)) && sup) {
                    outCaps->targetFanSpeedSupported = 1;
                    ADLX_IntRange r{};
                    if (ADLX_SUCCEEDED(fan->GetTargetFanSpeedRange(&r))) FillRange(outCaps->targetFanSpeedRange, r);
                }
            }
        }
    }

    return ADLX_OK;
}

static int32_t ReadFanStateInternal(int32_t gpuIndex, RgFanTuningState* outState, bool readDefault) {
    if (!outState) return ADLX_INVALID_ARGS;
    std::memset(outState, 0, sizeof(RgFanTuningState));

    std::lock_guard<std::mutex> guard(g_lock);
    IADLXGPUPtr gpu;
    ADLX_RESULT res = GetGpuByIndex(gpuIndex, gpu);
    if (ADLX_FAILED(res) || gpu == nullptr) return res;

    IADLXGPUTuningServicesPtr tuning;
    res = g_adlx.GetSystemServices()->GetGPUTuningServices(&tuning);
    if (ADLX_FAILED(res) || tuning == nullptr) return res;

    adlx_bool supported = false;
    res = tuning->IsSupportedManualFanTuning(gpu, &supported);
    if (ADLX_FAILED(res) || !supported) {
        outState->supported = 0;
        return ADLX_SUCCEEDED(res) ? ADLX_NOT_SUPPORTED : res;
    }
    outState->supported = 1;

    IADLXInterfacePtr ifc;
    res = tuning->GetManualFanTuning(gpu, &ifc);
    if (ADLX_FAILED(res) || ifc == nullptr) return res;

    IADLXManualFanTuningPtr fan(ifc);
    if (fan == nullptr) return ADLX_UNKNOWN_INTERFACE;

    ADLX_IntRange sr{}, tr{};
    if (ADLX_SUCCEEDED(fan->GetFanTuningRanges(&sr, &tr))) {
        FillRange(outState->speedRange, sr);
        FillRange(outState->temperatureRange, tr);
    }

    adlx_bool sup = false, flag = false;
    adlx_int val = 0;

    if (ADLX_SUCCEEDED(fan->IsSupportedZeroRPM(&sup)) && sup) {
        outState->zeroRpmSupported = 1;
        if (readDefault) {
            IADLXManualFanTuning1Ptr fan1(fan);
            if (fan1 != nullptr && ADLX_SUCCEEDED(fan1->GetDefaultZeroRPMState(&flag))) {
                outState->zeroRpmEnabled = flag ? 1 : 0;
            }
        } else if (ADLX_SUCCEEDED(fan->GetZeroRPMState(&flag))) {
            outState->zeroRpmEnabled = flag ? 1 : 0;
        }
    }

    if (ADLX_SUCCEEDED(fan->IsSupportedMinAcousticLimit(&sup)) && sup) {
        outState->minAcousticSupported = 1;
        ADLX_IntRange r{};
        if (ADLX_SUCCEEDED(fan->GetMinAcousticLimitRange(&r))) FillRange(outState->minAcousticRange, r);
        if (readDefault) {
            IADLXManualFanTuning1Ptr fan1(fan);
            if (fan1 != nullptr && ADLX_SUCCEEDED(fan1->GetMinAcousticLimitDefault(&val))) outState->minAcousticLimit = val;
        } else if (ADLX_SUCCEEDED(fan->GetMinAcousticLimit(&val))) {
            outState->minAcousticLimit = val;
        }
    }

    if (ADLX_SUCCEEDED(fan->IsSupportedMinFanSpeed(&sup)) && sup) {
        outState->minFanSpeedSupported = 1;
        ADLX_IntRange r{};
        if (ADLX_SUCCEEDED(fan->GetMinFanSpeedRange(&r))) FillRange(outState->minFanSpeedRange, r);
        if (readDefault) {
            IADLXManualFanTuning1Ptr fan1(fan);
            if (fan1 != nullptr && ADLX_SUCCEEDED(fan1->GetMinFanSpeedDefault(&val))) outState->minFanSpeed = val;
        } else if (ADLX_SUCCEEDED(fan->GetMinFanSpeed(&val))) {
            outState->minFanSpeed = val;
        }
    }

    if (ADLX_SUCCEEDED(fan->IsSupportedTargetFanSpeed(&sup)) && sup) {
        outState->targetFanSpeedSupported = 1;
        ADLX_IntRange r{};
        if (ADLX_SUCCEEDED(fan->GetTargetFanSpeedRange(&r))) FillRange(outState->targetFanSpeedRange, r);
        if (readDefault) {
            IADLXManualFanTuning1Ptr fan1(fan);
            if (fan1 != nullptr && ADLX_SUCCEEDED(fan1->GetTargetFanSpeedDefault(&val))) outState->targetFanSpeed = val;
        } else if (ADLX_SUCCEEDED(fan->GetTargetFanSpeed(&val))) {
            outState->targetFanSpeed = val;
        }
    }

    IADLXManualFanTuningStateListPtr states;
    if (readDefault) {
        IADLXManualFanTuning1Ptr fan1(fan);
        if (fan1 == nullptr) return ADLX_UNKNOWN_INTERFACE;
        res = fan1->GetDefaultFanTuningStates(&states);
    } else {
        res = fan->GetFanTuningStates(&states);
    }
    if (ADLX_SUCCEEDED(res) && states != nullptr) {
        ReadFanStatesInto(states, outState);
    }
    return res;
}

RG_API int32_t __stdcall RgGetFanTuningState(int32_t gpuIndex, RgFanTuningState* outState) {
    return ReadFanStateInternal(gpuIndex, outState, false);
}

RG_API int32_t __stdcall RgGetDefaultFanTuningState(int32_t gpuIndex, RgFanTuningState* outState) {
    return ReadFanStateInternal(gpuIndex, outState, true);
}

static int32_t ReadManualTuningInternal(int32_t gpuIndex, RgManualTuningState* outState, bool readDefault) {
    if (!outState) return ADLX_INVALID_ARGS;
    std::memset(outState, 0, sizeof(RgManualTuningState));

    std::lock_guard<std::mutex> guard(g_lock);
    IADLXGPUPtr gpu;
    ADLX_RESULT res = GetGpuByIndex(gpuIndex, gpu);
    if (ADLX_FAILED(res) || gpu == nullptr) return res;

    IADLXGPUTuningServicesPtr tuning;
    res = g_adlx.GetSystemServices()->GetGPUTuningServices(&tuning);
    if (ADLX_FAILED(res) || tuning == nullptr) return res;

    adlx_bool b = false;
    if (!readDefault && ADLX_SUCCEEDED(tuning->IsAtFactory(gpu, &b))) {
        outState->atFactory = b ? 1 : 0;
    }

    if (ADLX_SUCCEEDED(tuning->IsSupportedManualGFXTuning(gpu, &b)) && b) {
        IADLXInterfacePtr ifc;
        if (ADLX_SUCCEEDED(tuning->GetManualGFXTuning(gpu, &ifc)) && ifc != nullptr) {
            IADLXManualGraphicsTuning2Ptr gfx2(ifc);
            if (gfx2 != nullptr) {
                adlx_int minF = 0, maxF = 0, volt = 0;
                if (readDefault) {
                    IADLXManualGraphicsTuning2_1Ptr gfx21(gfx2);
                    if (gfx21 != nullptr) {
                        if (ADLX_SUCCEEDED(gfx21->GetGPUMinFrequencyDefault(&minF)) &&
                            ADLX_SUCCEEDED(gfx21->GetGPUMaxFrequencyDefault(&maxF)) &&
                            ADLX_SUCCEEDED(gfx21->GetGPUVoltageDefault(&volt))) {
                            outState->gfxMinFreqMHz = minF;
                            outState->gfxMaxFreqMHz = maxF;
                            outState->gfxVoltageMv = volt;
                            outState->gfxValid = 1;
                        }
                    }
                } else {
                    if (ADLX_SUCCEEDED(gfx2->GetGPUMinFrequency(&minF)) &&
                        ADLX_SUCCEEDED(gfx2->GetGPUMaxFrequency(&maxF)) &&
                        ADLX_SUCCEEDED(gfx2->GetGPUVoltage(&volt))) {
                        outState->gfxMinFreqMHz = minF;
                        outState->gfxMaxFreqMHz = maxF;
                        outState->gfxVoltageMv = volt;
                        outState->gfxValid = 1;
                    }
                }
            }
        }
    }

    if (ADLX_SUCCEEDED(tuning->IsSupportedManualVRAMTuning(gpu, &b)) && b) {
        IADLXInterfacePtr ifc;
        if (ADLX_SUCCEEDED(tuning->GetManualVRAMTuning(gpu, &ifc)) && ifc != nullptr) {
            IADLXManualVRAMTuning2Ptr vram2(ifc);
            if (vram2 != nullptr) {
                adlx_int vramFreq = 0;
                if (readDefault) {
                    IADLXManualVRAMTuning2_1Ptr vram21(vram2);
                    if (vram21 != nullptr && ADLX_SUCCEEDED(vram21->GetMaxVRAMFrequencyDefault(&vramFreq))) {
                        outState->vramMaxFreqMHz = vramFreq;
                        outState->vramValid = 1;
                    }
                } else if (ADLX_SUCCEEDED(vram2->GetMaxVRAMFrequency(&vramFreq))) {
                    outState->vramMaxFreqMHz = vramFreq;
                    outState->vramValid = 1;
                }

                ADLX_MEMORYTIMING_DESCRIPTION mt = MEMORYTIMING_DEFAULT;
                if (!readDefault && ADLX_SUCCEEDED(vram2->GetMemoryTimingDescription(&mt))) {
                    outState->vramMemoryTiming = static_cast<int32_t>(mt);
                    outState->memoryTimingValid = 1;
                } else if (readDefault) {
                    outState->vramMemoryTiming = static_cast<int32_t>(MEMORYTIMING_DEFAULT);
                    outState->memoryTimingValid = 1;
                }
            }
        }
    }

    if (ADLX_SUCCEEDED(tuning->IsSupportedManualPowerTuning(gpu, &b)) && b) {
        IADLXInterfacePtr ifc;
        if (ADLX_SUCCEEDED(tuning->GetManualPowerTuning(gpu, &ifc)) && ifc != nullptr) {
            IADLXManualPowerTuningPtr pwr(ifc);
            if (pwr != nullptr) {
                adlx_int pwrLim = 0;
                if (readDefault) {
                    IADLXManualPowerTuning1Ptr pwr1(pwr);
                    if (pwr1 != nullptr && ADLX_SUCCEEDED(pwr1->GetPowerLimitDefault(&pwrLim))) {
                        outState->powerLimitPercent = pwrLim;
                        outState->powerValid = 1;
                    }
                } else if (ADLX_SUCCEEDED(pwr->GetPowerLimit(&pwrLim))) {
                    outState->powerLimitPercent = pwrLim;
                    outState->powerValid = 1;
                }
            }
        }
    }

    return ADLX_OK;
}

RG_API int32_t __stdcall RgGetManualTuningState(int32_t gpuIndex, RgManualTuningState* outState) {
    return ReadManualTuningInternal(gpuIndex, outState, false);
}

RG_API int32_t __stdcall RgGetDefaultManualTuningState(int32_t gpuIndex, RgManualTuningState* outState) {
    return ReadManualTuningInternal(gpuIndex, outState, true);
}

RG_API int32_t __stdcall RgGet3DSettingsState(int32_t gpuIndex, Rg3DSettingsState* outState) {
    if (!outState) return ADLX_INVALID_ARGS;
    std::memset(outState, 0, sizeof(Rg3DSettingsState));

    std::lock_guard<std::mutex> guard(g_lock);
    IADLXGPUPtr gpu;
    ADLX_RESULT res = GetGpuByIndex(gpuIndex, gpu);
    if (ADLX_FAILED(res) || gpu == nullptr) return res;

    IADLX3DSettingsServicesPtr svc;
    res = g_adlx.GetSystemServices()->Get3DSettingsServices(&svc);
    if (ADLX_FAILED(res) || svc == nullptr) return res;

    adlx_bool sup = false, en = false;
    adlx_int val = 0, val2 = 0;
    ADLX_IntRange r{};

    {
        IADLX3DAntiLagPtr p;
        if (ADLX_SUCCEEDED(svc->GetAntiLag(gpu, &p)) && p != nullptr) {
            if (ADLX_SUCCEEDED(p->IsSupported(&sup)) && sup) {
                outState->antiLagSupported = 1;
                if (ADLX_SUCCEEDED(p->IsEnabled(&en))) outState->antiLagEnabled = en ? 1 : 0;
            }
        }
    }
    {
        IADLX3DChillPtr p;
        if (ADLX_SUCCEEDED(svc->GetChill(gpu, &p)) && p != nullptr) {
            if (ADLX_SUCCEEDED(p->IsSupported(&sup)) && sup) {
                outState->chillSupported = 1;
                if (ADLX_SUCCEEDED(p->IsEnabled(&en))) outState->chillEnabled = en ? 1 : 0;
                if (ADLX_SUCCEEDED(p->GetMinFPS(&val))) outState->chillMinFps = val;
                if (ADLX_SUCCEEDED(p->GetMaxFPS(&val2))) outState->chillMaxFps = val2;
                if (ADLX_SUCCEEDED(p->GetFPSRange(&r))) FillRange(outState->chillFpsRange, r);
            }
        }
    }
    {
        IADLX3DBoostPtr p;
        if (ADLX_SUCCEEDED(svc->GetBoost(gpu, &p)) && p != nullptr) {
            if (ADLX_SUCCEEDED(p->IsSupported(&sup)) && sup) {
                outState->boostSupported = 1;
                if (ADLX_SUCCEEDED(p->IsEnabled(&en))) outState->boostEnabled = en ? 1 : 0;
                if (ADLX_SUCCEEDED(p->GetResolution(&val))) outState->boostResolution = val;
                if (ADLX_SUCCEEDED(p->GetResolutionRange(&r))) FillRange(outState->boostResolutionRange, r);
            }
        }
    }
    {
        IADLX3DImageSharpeningPtr p;
        if (ADLX_SUCCEEDED(svc->GetImageSharpening(gpu, &p)) && p != nullptr) {
            if (ADLX_SUCCEEDED(p->IsSupported(&sup)) && sup) {
                outState->imageSharpeningSupported = 1;
                if (ADLX_SUCCEEDED(p->IsEnabled(&en))) outState->imageSharpeningEnabled = en ? 1 : 0;
                if (ADLX_SUCCEEDED(p->GetSharpness(&val))) outState->imageSharpeningPercent = val;
                if (ADLX_SUCCEEDED(p->GetSharpnessRange(&r))) FillRange(outState->imageSharpeningRange, r);
            }
        }
    }
    {
        IADLX3DEnhancedSyncPtr p;
        if (ADLX_SUCCEEDED(svc->GetEnhancedSync(gpu, &p)) && p != nullptr) {
            if (ADLX_SUCCEEDED(p->IsSupported(&sup)) && sup) {
                outState->enhancedSyncSupported = 1;
                if (ADLX_SUCCEEDED(p->IsEnabled(&en))) outState->enhancedSyncEnabled = en ? 1 : 0;
            }
        }
    }
    {
        IADLX3DWaitForVerticalRefreshPtr p;
        if (ADLX_SUCCEEDED(svc->GetWaitForVerticalRefresh(gpu, &p)) && p != nullptr) {
            if (ADLX_SUCCEEDED(p->IsSupported(&sup)) && sup) {
                outState->waitForVerticalRefreshSupported = 1;
                if (ADLX_SUCCEEDED(p->IsEnabled(&en))) outState->waitForVerticalRefreshEnabled = en ? 1 : 0;
                ADLX_WAIT_FOR_VERTICAL_REFRESH_MODE mode = WFVR_OFF_UNLESS_APP_SPECIFIES;
                if (ADLX_SUCCEEDED(p->GetMode(&mode))) outState->waitForVerticalRefreshMode = static_cast<int32_t>(mode);
            }
        }
    }
    {
        IADLX3DFrameRateTargetControlPtr p;
        if (ADLX_SUCCEEDED(svc->GetFrameRateTargetControl(gpu, &p)) && p != nullptr) {
            if (ADLX_SUCCEEDED(p->IsSupported(&sup)) && sup) {
                outState->frtcSupported = 1;
                if (ADLX_SUCCEEDED(p->IsEnabled(&en))) outState->frtcEnabled = en ? 1 : 0;
                if (ADLX_SUCCEEDED(p->GetFPS(&val))) outState->frtcFps = val;
                if (ADLX_SUCCEEDED(p->GetFPSRange(&r))) FillRange(outState->frtcFpsRange, r);
            }
        }
    }
    {
        IADLX3DSettingsServices1Ptr svc1(svc);
        if (svc1 != nullptr) {
            IADLX3DAMDFluidMotionFramesPtr p;
            if (ADLX_SUCCEEDED(svc1->GetAMDFluidMotionFrames(&p)) && p != nullptr) {
                if (ADLX_SUCCEEDED(p->IsSupported(&sup)) && sup) {
                    outState->afmfSupported = 1;
                    if (ADLX_SUCCEEDED(p->IsEnabled(&en))) outState->afmfEnabled = en ? 1 : 0;
                }
            }
        }
    }

    return ADLX_OK;
}

RG_API int32_t __stdcall RgSetFanTuningState(
    int32_t gpuIndex,
    const RgFanCurvePoint* points,
    int32_t pointCount,
    uint8_t enableZeroRpm,
    int32_t minAcousticLimit,
    uint8_t applyMinAcoustic,
    int32_t* outValidationErrorIndex)
{
    if (outValidationErrorIndex) *outValidationErrorIndex = -1;
    if (!points || pointCount <= 0 || pointCount > 8) return ADLX_INVALID_ARGS;

    std::lock_guard<std::mutex> guard(g_lock);
    IADLXGPUPtr gpu;
    ADLX_RESULT res = GetGpuByIndex(gpuIndex, gpu);
    if (ADLX_FAILED(res) || gpu == nullptr) return res;

    IADLXGPUTuningServicesPtr tuning;
    res = g_adlx.GetSystemServices()->GetGPUTuningServices(&tuning);
    if (ADLX_FAILED(res) || tuning == nullptr) return res;

    IADLXInterfacePtr ifc;
    res = tuning->GetManualFanTuning(gpu, &ifc);
    if (ADLX_FAILED(res) || ifc == nullptr) return res;

    IADLXManualFanTuningPtr fan(ifc);
    if (fan == nullptr) return ADLX_UNKNOWN_INTERFACE;

    IADLXManualFanTuningStateListPtr states;
    res = fan->GetEmptyFanTuningStates(&states);
    if (ADLX_FAILED(res) || states == nullptr) return res;

    adlx_uint hwCount = states->Size();
    if (static_cast<adlx_uint>(pointCount) != hwCount) {
        return ADLX_INVALID_ARGS;
    }

    for (adlx_uint i = 0; i < hwCount; ++i) {
        IADLXManualFanTuningStatePtr st;
        res = states->At(i, &st);
        if (ADLX_FAILED(res) || st == nullptr) return res;
        res = st->SetTemperature(points[i].temperatureC);
        if (ADLX_FAILED(res)) return res;
        res = st->SetFanSpeed(points[i].fanSpeedPercent);
        if (ADLX_FAILED(res)) return res;
    }

    adlx_int errIdx = -1;
    res = fan->IsValidFanTuningStates(states, &errIdx);
    if (outValidationErrorIndex) *outValidationErrorIndex = errIdx;
    if (ADLX_FAILED(res) || errIdx != -1) {
        return ADLX_FAILED(res) ? res : ADLX_INVALID_ARGS;
    }

    res = fan->SetFanTuningStates(states);
    if (ADLX_FAILED(res)) return res;

    adlx_bool zeroSupported = false;
    if (ADLX_SUCCEEDED(fan->IsSupportedZeroRPM(&zeroSupported)) && zeroSupported) {
        ADLX_RESULT zRes = fan->SetZeroRPMState(enableZeroRpm != 0);
        if (ADLX_FAILED(zRes)) return zRes;
    }

    if (applyMinAcoustic != 0) {
        adlx_bool acSupported = false;
        if (ADLX_SUCCEEDED(fan->IsSupportedMinAcousticLimit(&acSupported)) && acSupported) {
            ADLX_RESULT aRes = fan->SetMinAcousticLimit(minAcousticLimit);
            if (ADLX_FAILED(aRes)) return aRes;
        }
    }

    return ADLX_OK;
}

RG_API int32_t __stdcall RgSetManualGfxTuning(int32_t gpuIndex, int32_t minFreqMHz, int32_t maxFreqMHz, int32_t voltageMv) {
    std::lock_guard<std::mutex> guard(g_lock);
    IADLXGPUPtr gpu;
    ADLX_RESULT res = GetGpuByIndex(gpuIndex, gpu);
    if (ADLX_FAILED(res) || gpu == nullptr) return res;

    IADLXGPUTuningServicesPtr tuning;
    res = g_adlx.GetSystemServices()->GetGPUTuningServices(&tuning);
    if (ADLX_FAILED(res) || tuning == nullptr) return res;

    IADLXInterfacePtr ifc;
    res = tuning->GetManualGFXTuning(gpu, &ifc);
    if (ADLX_FAILED(res) || ifc == nullptr) return res;

    IADLXManualGraphicsTuning2Ptr gfx2(ifc);
    if (gfx2 == nullptr) return ADLX_NOT_SUPPORTED;

    res = gfx2->SetGPUMinFrequency(minFreqMHz);
    if (ADLX_FAILED(res)) return res;
    res = gfx2->SetGPUMaxFrequency(maxFreqMHz);
    if (ADLX_FAILED(res)) return res;
    res = gfx2->SetGPUVoltage(voltageMv);
    return res;
}

RG_API int32_t __stdcall RgSetManualVramTuning(int32_t gpuIndex, int32_t maxFreqMHz, int32_t memoryTiming) {
    std::lock_guard<std::mutex> guard(g_lock);
    IADLXGPUPtr gpu;
    ADLX_RESULT res = GetGpuByIndex(gpuIndex, gpu);
    if (ADLX_FAILED(res) || gpu == nullptr) return res;

    IADLXGPUTuningServicesPtr tuning;
    res = g_adlx.GetSystemServices()->GetGPUTuningServices(&tuning);
    if (ADLX_FAILED(res) || tuning == nullptr) return res;

    IADLXInterfacePtr ifc;
    res = tuning->GetManualVRAMTuning(gpu, &ifc);
    if (ADLX_FAILED(res) || ifc == nullptr) return res;

    IADLXManualVRAMTuning2Ptr vram2(ifc);
    if (vram2 == nullptr) return ADLX_NOT_SUPPORTED;

    adlx_bool mtSupported = false;
    if (ADLX_SUCCEEDED(vram2->IsSupportedMemoryTiming(&mtSupported)) && mtSupported) {
        res = vram2->SetMemoryTimingDescription(static_cast<ADLX_MEMORYTIMING_DESCRIPTION>(memoryTiming));
        if (ADLX_FAILED(res)) return res;
    }

    return vram2->SetMaxVRAMFrequency(maxFreqMHz);
}

RG_API int32_t __stdcall RgSetManualPowerTuning(int32_t gpuIndex, int32_t powerLimitPercent) {
    std::lock_guard<std::mutex> guard(g_lock);
    IADLXGPUPtr gpu;
    ADLX_RESULT res = GetGpuByIndex(gpuIndex, gpu);
    if (ADLX_FAILED(res) || gpu == nullptr) return res;

    IADLXGPUTuningServicesPtr tuning;
    res = g_adlx.GetSystemServices()->GetGPUTuningServices(&tuning);
    if (ADLX_FAILED(res) || tuning == nullptr) return res;

    IADLXInterfacePtr ifc;
    res = tuning->GetManualPowerTuning(gpu, &ifc);
    if (ADLX_FAILED(res) || ifc == nullptr) return res;

    IADLXManualPowerTuningPtr pwr(ifc);
    if (pwr == nullptr) return ADLX_UNKNOWN_INTERFACE;

    return pwr->SetPowerLimit(powerLimitPercent);
}

RG_API int32_t __stdcall RgResetGpuTuningToFactory(int32_t gpuIndex) {
    std::lock_guard<std::mutex> guard(g_lock);
    IADLXGPUPtr gpu;
    ADLX_RESULT res = GetGpuByIndex(gpuIndex, gpu);
    if (ADLX_FAILED(res) || gpu == nullptr) return res;

    IADLXGPUTuningServicesPtr tuning;
    res = g_adlx.GetSystemServices()->GetGPUTuningServices(&tuning);
    if (ADLX_FAILED(res) || tuning == nullptr) return res;

    return tuning->ResetToFactory(gpu);
}

RG_API int32_t __stdcall RgSet3DSettingsState(int32_t gpuIndex, const Rg3DSettingsState* state) {
    if (!state) return ADLX_INVALID_ARGS;

    std::lock_guard<std::mutex> guard(g_lock);
    IADLXGPUPtr gpu;
    ADLX_RESULT res = GetGpuByIndex(gpuIndex, gpu);
    if (ADLX_FAILED(res) || gpu == nullptr) return res;

    IADLX3DSettingsServicesPtr svc;
    res = g_adlx.GetSystemServices()->Get3DSettingsServices(&svc);
    if (ADLX_FAILED(res) || svc == nullptr) return res;

    adlx_bool sup = false;

    // Note: Anti-Lag and Chill are mutually exclusive in ADLX; apply Chill first if enabled, else Anti-Lag.
    if (state->chillEnabled) {
        IADLX3DChillPtr chill;
        if (ADLX_SUCCEEDED(svc->GetChill(gpu, &chill)) && chill != nullptr &&
            ADLX_SUCCEEDED(chill->IsSupported(&sup)) && sup) {
            chill->SetEnabled(true);
            chill->SetMinFPS(state->chillMinFps);
            chill->SetMaxFPS(state->chillMaxFps);
        }
    } else {
        IADLX3DChillPtr chill;
        if (ADLX_SUCCEEDED(svc->GetChill(gpu, &chill)) && chill != nullptr &&
            ADLX_SUCCEEDED(chill->IsSupported(&sup)) && sup) {
            chill->SetEnabled(false);
        }
        IADLX3DAntiLagPtr al;
        if (ADLX_SUCCEEDED(svc->GetAntiLag(gpu, &al)) && al != nullptr &&
            ADLX_SUCCEEDED(al->IsSupported(&sup)) && sup) {
            al->SetEnabled(state->antiLagEnabled != 0);
        }
    }

    {
        IADLX3DBoostPtr boost;
        if (ADLX_SUCCEEDED(svc->GetBoost(gpu, &boost)) && boost != nullptr &&
            ADLX_SUCCEEDED(boost->IsSupported(&sup)) && sup) {
            boost->SetEnabled(state->boostEnabled != 0);
            if (state->boostEnabled != 0 && state->boostResolution > 0) {
                boost->SetResolution(state->boostResolution);
            }
        }
    }

    {
        IADLX3DImageSharpeningPtr ris;
        if (ADLX_SUCCEEDED(svc->GetImageSharpening(gpu, &ris)) && ris != nullptr &&
            ADLX_SUCCEEDED(ris->IsSupported(&sup)) && sup) {
            ris->SetEnabled(state->imageSharpeningEnabled != 0);
            if (state->imageSharpeningEnabled != 0) {
                ris->SetSharpness(state->imageSharpeningPercent);
            }
        }
    }

    // Enhanced Sync and VSync are mutually exclusive
    if (state->enhancedSyncEnabled) {
        IADLX3DEnhancedSyncPtr es;
        if (ADLX_SUCCEEDED(svc->GetEnhancedSync(gpu, &es)) && es != nullptr &&
            ADLX_SUCCEEDED(es->IsSupported(&sup)) && sup) {
            es->SetEnabled(true);
        }
    } else {
        IADLX3DEnhancedSyncPtr es;
        if (ADLX_SUCCEEDED(svc->GetEnhancedSync(gpu, &es)) && es != nullptr &&
            ADLX_SUCCEEDED(es->IsSupported(&sup)) && sup) {
            es->SetEnabled(false);
        }
        IADLX3DWaitForVerticalRefreshPtr vsync;
        if (ADLX_SUCCEEDED(svc->GetWaitForVerticalRefresh(gpu, &vsync)) && vsync != nullptr &&
            ADLX_SUCCEEDED(vsync->IsSupported(&sup)) && sup) {
            vsync->SetMode(static_cast<ADLX_WAIT_FOR_VERTICAL_REFRESH_MODE>(state->waitForVerticalRefreshMode));
        }
    }

    {
        IADLX3DFrameRateTargetControlPtr frtc;
        if (ADLX_SUCCEEDED(svc->GetFrameRateTargetControl(gpu, &frtc)) && frtc != nullptr &&
            ADLX_SUCCEEDED(frtc->IsSupported(&sup)) && sup) {
            frtc->SetEnabled(state->frtcEnabled != 0);
            if (state->frtcEnabled != 0 && state->frtcFps > 0) {
                frtc->SetFPS(state->frtcFps);
            }
        }
    }

    return ADLX_OK;
}

RG_API int32_t __stdcall RgRegisterTuningChangedCallback(RgTuningChangedCallback callback) {
    std::lock_guard<std::mutex> guard(g_lock);
    if (!g_initialized || g_adlx.GetSystemServices() == nullptr) return ADLX_TERMINATED;

    g_tuningCallback.store(callback, std::memory_order_release);
    if (!g_listenerRegistered) {
        IADLXGPUTuningServicesPtr tuning;
        ADLX_RESULT res = g_adlx.GetSystemServices()->GetGPUTuningServices(&tuning);
        if (ADLX_FAILED(res) || tuning == nullptr) return res;

        res = tuning->GetGPUTuningChangedHandling(&g_tuningChangedHandling);
        if (ADLX_FAILED(res) || g_tuningChangedHandling == nullptr) return res;

        res = g_tuningChangedHandling->AddGPUTuningEventListener(&g_tuningListener);
        if (ADLX_FAILED(res)) {
            g_tuningChangedHandling = nullptr;
            return res;
        }
        g_listenerRegistered = true;
    }
    return ADLX_OK;
}

RG_API int32_t __stdcall RgUnregisterTuningChangedCallback() {
    std::lock_guard<std::mutex> guard(g_lock);
    g_tuningCallback.store(nullptr, std::memory_order_release);
    if (g_listenerRegistered && g_tuningChangedHandling != nullptr) {
        ADLX_RESULT res = g_tuningChangedHandling->RemoveGPUTuningEventListener(&g_tuningListener);
        g_listenerRegistered = false;
        g_tuningChangedHandling = nullptr;
        return res;
    }
    return ADLX_OK;
}

} // extern "C"
