using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using Application = System.Windows.Application;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using RadeonGuard.Core.IPC;
using RadeonGuard.Core.Models;
using RadeonGuard.Core.Validation;
using RadeonGuard.Diagnostics;
using RadeonGuard.Persistence;
using RadeonGuard.UI.Controls;
using RadeonGuard.UI.Services;

namespace RadeonGuard.UI.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly RadeonGuardRepository _repo = new();
    private readonly BackupManager _backups = new();
    private readonly ProfilePortabilityService _portability;
    private readonly GuardianIpcClient _ipc = new();
    private readonly CancellationTokenSource _pollCts = new();
    private Task? _pollTask;
    private bool _suppressAutoStartSave;

    private const string SettingAutoStartDisabled = "AutoStartDisabledByUser";
    private const string SettingActiveGlobalFanId = "ActiveGlobalFanId";
    private const string SettingHasUserManagedConfig = "HasUserManagedConfig";

    [ObservableProperty] private string _statusBanner = "Connecting to RadeonGuard Guardian...";
    [ObservableProperty] private bool _guardianConnected;
    [ObservableProperty] private string _adlxVersion = "—";
    [ObservableProperty] private string _hardwareSyncState = "MONITORING";
    [ObservableProperty] private string _activeTuningName = "—";
    [ObservableProperty] private string _activeFanProfileId = string.Empty;
    [ObservableProperty] private string _activeFanName = "—";
    [ObservableProperty] private string _activeGameText = "None (Global Profile Active)";
    [ObservableProperty] private bool _autoStartWithWindows;

    // GPU Hardware Info
    [ObservableProperty] private string _gpuName = "AMD Radeon RX 6800 XT (Detecting...)";
    [ObservableProperty] private string _gpuDriverVersion = "—";
    [ObservableProperty] private string _gpuVramInfo = "—";
    [ObservableProperty] private string _gpuHardwareKey = string.Empty;
    [ObservableProperty] private string _gpuBiosVersion = "—";

    // Live Telemetry
    [ObservableProperty] private double _edgeTempC;
    [ObservableProperty] private double _hotspotTempC;
    [ObservableProperty] private double _gpuUsagePercent;
    [ObservableProperty] private int _gpuClockMHz;
    [ObservableProperty] private int _vramClockMHz;
    [ObservableProperty] private int _vramUsedMb;
    [ObservableProperty] private double _boardPowerW;
    [ObservableProperty] private int _voltageMv;
    [ObservableProperty] private int _fanRpm;

    public ObservableCollection<double> TempHistory { get; } = new();
    public ObservableCollection<double> UsageHistory { get; } = new();
    public ObservableCollection<double> PowerHistory { get; } = new();

    // Capabilities
    [ObservableProperty] private GpuTuningCapabilities? _capabilities;
    [ObservableProperty] private string _hardwareRangesSummary = "Waiting for ADLX capability query...";

    // Fan Control
    public ObservableCollection<FanProfile> FanProfiles { get; } = new();
    [ObservableProperty] private FanProfile? _selectedFanProfile;
    [ObservableProperty] private string _editFanName = string.Empty;
    [ObservableProperty] private bool _editFanZeroRpm = true;
    [ObservableProperty] private int _editFanMinAcoustic = 1500;
    [ObservableProperty] private bool _editFanUseMinAcoustic;
    public ObservableCollection<EditableFanPoint> EditFanPoints { get; } = new();
    [ObservableProperty] private string _fanValidationMessage = string.Empty;
    [ObservableProperty] private bool _fanValidationHasErrors;

    // GPU Tuning
    public ObservableCollection<TuningProfile> TuningProfiles { get; } = new();
    [ObservableProperty] private TuningProfile? _selectedTuningProfile;
    [ObservableProperty] private string _editTuningName = string.Empty;
    [ObservableProperty] private bool _editTuningIsGlobal;
    [ObservableProperty] private bool _editTuningIsQuarantined;
    [ObservableProperty] private string _editTuningQuarantineReason = string.Empty;
    [ObservableProperty] private bool _editEnableClock;
    [ObservableProperty] private int _editMinCoreClock = 500;
    [ObservableProperty] private int _editMaxCoreClock = 2400;
    [ObservableProperty] private bool _editEnableVoltage;
    [ObservableProperty] private int _editCoreVoltage = 1150;
    [ObservableProperty] private bool _editEnableVram;
    [ObservableProperty] private int _editMaxVramClock = 2000;
    [ObservableProperty] private VramTimingMode _editVramTiming = VramTimingMode.Default;
    [ObservableProperty] private bool _editEnablePower;
    [ObservableProperty] private int _editPowerLimitDelta;
    [ObservableProperty] private string _editLinkedFanProfileId = string.Empty;
    [ObservableProperty] private bool _editAntiLag;
    [ObservableProperty] private bool _editChill;
    [ObservableProperty] private int _editChillMinFps = 60;
    [ObservableProperty] private int _editChillMaxFps = 144;
    [ObservableProperty] private bool _editImageSharpening;
    [ObservableProperty] private int _editSharpeningPercent = 80;
    [ObservableProperty] private bool _editEnhancedSync;
    [ObservableProperty] private bool _editFrtc;
    [ObservableProperty] private int _editFrtcFps = 144;
    [ObservableProperty] private string _tuningFeedbackMessage = string.Empty;

    // Game Profiles & Denylist
    public ObservableCollection<GameProfile> GameProfiles { get; } = new();
    public ObservableCollection<GameFanOverrideOption> GameFanOverrideOptions { get; } = new();
    [ObservableProperty] private GameProfile? _selectedGameProfile;
    [ObservableProperty] private string _editGameTitle = string.Empty;
    [ObservableProperty] private string _editGameExePath = string.Empty;
    [ObservableProperty] private string _editGameTuningId = string.Empty;
    [ObservableProperty] private string? _editGameFanOverrideId;
    [ObservableProperty] private bool _editGameEnabled = true;
    [ObservableProperty] private bool _editGameLockRunning = true;
    [ObservableProperty] private ZeroRpmOverrideMode _editGameZeroRpmOverride = ZeroRpmOverrideMode.InheritFromFanProfile;
    [ObservableProperty] private string _gameImportFeedback = string.Empty;

    public ObservableCollection<DenylistEntry> Denylist { get; } = new();
    [ObservableProperty] private string _newDenylistExe = string.Empty;
    [ObservableProperty] private string _newDenylistReason = "User excluded non-game";

    // History & Backups
    public ObservableCollection<ProfileRevision> Revisions { get; } = new();
    public ObservableCollection<BackupInfo> Backups { get; } = new();
    [ObservableProperty] private string _historyFeedback = string.Empty;

    // Diagnostics
    public ObservableCollection<AppliedStateRecord> AuditLog { get; } = new();
    public ObservableCollection<string> FlightLogs { get; } = new();
    public ObservableCollection<string> ConflictingProcesses { get; } = new();
    public ObservableCollection<string> DiagnosticRecommendations { get; } = new();
    public ObservableCollection<string> TdrEvents { get; } = new();
    [ObservableProperty] private bool _autoReapplyOnConflict = true;

    public Array VramTimingModes => Enum.GetValues(typeof(VramTimingMode));

    public IReadOnlyList<ZeroRpmOverrideOption> ZeroRpmOverrideOptions { get; } =
    [
        new(ZeroRpmOverrideMode.InheritFromFanProfile, "Inherit from Fan Profile"),
        new(ZeroRpmOverrideMode.ForceEnabled, "Force Enabled (Zero RPM On)"),
        new(ZeroRpmOverrideMode.ForceDisabled, "Force Disabled (Fans Always Spin)")
    ];

    public MainViewModel()
    {
        _portability = new ProfilePortabilityService(_repo);
    }

    public async Task InitializeAsync()
    {
        await _repo.InitializeAsync(StoragePaths.GetBackupsDirectory()).ConfigureAwait(true);
        GuardianIpcClient.EnsureGuardianStarted();

        string? hasManaged = await _repo.GetSettingAsync(SettingHasUserManagedConfig).ConfigureAwait(true);
        string? autoDisabled = await _repo.GetSettingAsync(SettingAutoStartDisabled).ConfigureAwait(true);
        if (!string.Equals(autoDisabled, "true", StringComparison.OrdinalIgnoreCase) &&
            (string.Equals(hasManaged, "true", StringComparison.OrdinalIgnoreCase) || StartupRegistrationService.IsGuardianAutoStartEnabled()))
        {
            StartupRegistrationService.SetGuardianAutoStart(true);
        }

        _suppressAutoStartSave = true;
        AutoStartWithWindows = StartupRegistrationService.IsGuardianAutoStartEnabled();
        _suppressAutoStartSave = false;

        await LoadAllDataAsync().ConfigureAwait(true);
        _pollTask = Task.Run(() => PollLoopAsync(_pollCts.Token));
        _ = RunDiagnosticsAsync();
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var snap = await _ipc.GetStatusAsync(ct).ConfigureAwait(false);
                if (snap is not null)
                {
                    await Application.Current.Dispatcher.InvokeAsync(() => ApplySnapshot(snap));
                }
                else
                {
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        GuardianConnected = false;
                        StatusBanner = "Guardian daemon offline — click 'Start Guardian' to enable telemetry & drift protection.";
                    });
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch { }

            await Task.Delay(1000, ct).ConfigureAwait(false);
        }
    }

    private void ApplySnapshot(GuardianStatusSnapshot snap)
    {
        GuardianConnected = snap.GuardianRunning;
        AdlxVersion = string.IsNullOrEmpty(snap.AdlxVersion) ? "Unavailable" : $"ADLX {snap.AdlxVersion}";
        HardwareSyncState = snap.HardwareSyncState;
        ActiveTuningName = snap.ActiveTuningProfileName;
        ActiveFanProfileId = snap.ActiveFanProfileId;
        ActiveFanName = snap.ActiveFanProfileName;
        ActiveGameText = string.IsNullOrEmpty(snap.ActiveGameTitle)
            ? "None (Global Profile Active)"
            : $"{snap.ActiveGameTitle} (PID {snap.ActiveGamePid})";
        AutoReapplyOnConflict = snap.ConflictPolicy == ConflictResolutionPolicy.AutoReapply;

        if (snap.GpuInfo is { } gpu)
        {
            GpuName = gpu.Name;
            GpuDriverVersion = $"{gpu.DriverVersion} (Adrenalin {gpu.AmdSoftwareVersion})";
            GpuVramInfo = $"{gpu.TotalVramMb} MB {gpu.VramType} • PCIe Gen{gpu.PciBusType} x{gpu.PciLaneWidth}";
            GpuHardwareKey = gpu.HardwareKey;
            GpuBiosVersion = $"{gpu.BiosVersion} ({gpu.BiosDate})";
        }

        if (snap.Capabilities is { } caps)
        {
            Capabilities = caps;
            HardwareRangesSummary =
                $"Core: {caps.GfxMinFreqRange.MinValue}–{caps.GfxMaxFreqRange.MaxValue} MHz | " +
                $"Volt: {caps.GfxVoltageRange.MinValue}–{caps.GfxVoltageRange.MaxValue} mV | " +
                $"VRAM: {caps.VramMaxFreqRange.MinValue}–{caps.VramMaxFreqRange.MaxValue} MHz | " +
                $"Power: {caps.PowerLimitRange.MinValue}%..+{caps.PowerLimitRange.MaxValue}% | " +
                $"Fan: {caps.FanSpeedRange.MinValue}–{caps.FanSpeedRange.MaxValue}%";
        }

        if (snap.LatestMetrics is { } m)
        {
            EdgeTempC = m.EdgeTemperatureC;
            HotspotTempC = m.HotspotTemperatureC;
            GpuUsagePercent = m.GpuUsagePercent;
            GpuClockMHz = m.GpuClockMHz;
            VramClockMHz = m.VramClockMHz;
            VramUsedMb = m.VramUsedMb;
            BoardPowerW = m.HasTotalBoardPower ? m.TotalBoardPowerW : m.GpuPowerW;
            VoltageMv = m.VoltageMv;
            FanRpm = m.FanSpeedRpm;

            PushSpark(TempHistory, m.HotspotTemperatureC);
            PushSpark(UsageHistory, m.GpuUsagePercent);
            PushSpark(PowerHistory, BoardPowerW);
        }

        StatusBanner = $"Guardian Active • {AdlxVersion} • Sync State: {HardwareSyncState}";
    }

    private static void PushSpark(ObservableCollection<double> col, double value)
    {
        col.Add(value);
        if (col.Count > 60) col.RemoveAt(0);
    }

    [RelayCommand]
    private void StartGuardian()
    {
        bool ok = GuardianIpcClient.EnsureGuardianStarted();
        StatusBanner = ok ? "Launched RadeonGuard.Guardian.exe..." : "Could not locate RadeonGuard.Guardian.exe.";
    }

    [RelayCommand]
    public async Task LoadAllDataAsync()
    {
        string hwKey = string.IsNullOrEmpty(GpuHardwareKey) ? "UNKNOWN_GPU" : GpuHardwareKey;
        await _repo.EnsureDefaultProfilesAsync(hwKey).ConfigureAwait(true);

        string? storedActiveFanId = await _repo.GetSettingAsync(SettingActiveGlobalFanId).ConfigureAwait(true);
        string targetFanId = !string.IsNullOrEmpty(ActiveFanProfileId) ? ActiveFanProfileId : (storedActiveFanId ?? string.Empty);

        var fans = await _repo.GetFanProfilesAsync().ConfigureAwait(true);
        FanProfiles.Clear();
        GameFanOverrideOptions.Clear();
        GameFanOverrideOptions.Add(new GameFanOverrideOption(string.Empty, "(None - Use Active Global Fan Profile)"));
        foreach (var f in fans)
        {
            FanProfiles.Add(f);
            GameFanOverrideOptions.Add(new GameFanOverrideOption(f.FanProfileId, f.Name));
        }
        if (SelectedFanProfile is null || FanProfiles.All(f => f.FanProfileId != SelectedFanProfile.FanProfileId))
        {
            SelectedFanProfile = FanProfiles.FirstOrDefault(f => f.FanProfileId == targetFanId)
                                 ?? FanProfiles.FirstOrDefault();
        }
        if (string.IsNullOrEmpty(ActiveFanProfileId) && !string.IsNullOrEmpty(targetFanId))
        {
            ActiveFanProfileId = targetFanId;
            var activeFan = FanProfiles.FirstOrDefault(f => f.FanProfileId == targetFanId);
            if (activeFan is not null) ActiveFanName = activeFan.Name;
        }

        var tunings = await _repo.GetTuningProfilesAsync().ConfigureAwait(true);
        TuningProfiles.Clear();
        foreach (var t in tunings) TuningProfiles.Add(t);
        if (SelectedTuningProfile is null || TuningProfiles.All(t => t.TuningProfileId != SelectedTuningProfile.TuningProfileId))
        {
            SelectedTuningProfile = TuningProfiles.FirstOrDefault();
        }

        var games = await _repo.GetGameProfilesAsync().ConfigureAwait(true);
        GameProfiles.Clear();
        foreach (var g in games) GameProfiles.Add(g);
        if (SelectedGameProfile is null || GameProfiles.All(g => g.GameProfileId != SelectedGameProfile.GameProfileId))
        {
            SelectedGameProfile = GameProfiles.FirstOrDefault();
        }

        var deny = await _repo.GetDenylistAsync().ConfigureAwait(true);
        Denylist.Clear();
        foreach (var d in deny) Denylist.Add(d);

        await RefreshHistoryAndBackupsAsync().ConfigureAwait(true);
    }

    partial void OnSelectedFanProfileChanged(FanProfile? value)
    {
        if (value is null) return;
        EditFanName = value.Name;
        EditFanZeroRpm = value.ZeroRpmEnabled;
        EditFanUseMinAcoustic = value.MinAcousticLimitMHz.HasValue;
        EditFanMinAcoustic = value.MinAcousticLimitMHz ?? 1500;
        EditFanPoints.Clear();
        for (int i = 0; i < value.Points.Count; i++)
        {
            EditFanPoints.Add(new EditableFanPoint
            {
                Index = i + 1,
                TemperatureC = value.Points[i].TemperatureC,
                FanSpeedPercent = value.Points[i].FanSpeedPercent
            });
        }
        ValidateCurrentFanDraft();
    }

    public bool ValidateCurrentFanDraft()
    {
        var draft = BuildFanDraft();
        var res = ProfileValidator.ValidateFanProfile(draft, Capabilities);
        FanValidationHasErrors = !res.IsValid;
        FanValidationMessage = res.IsValid
            ? "Fan curve validated against hardware constraints."
            : string.Join(" ", res.Errors);
        return res.IsValid;
    }

    private FanProfile BuildFanDraft()
    {
        return new FanProfile
        {
            FanProfileId = SelectedFanProfile?.FanProfileId ?? Guid.NewGuid().ToString("B"),
            GpuHardwareKey = string.IsNullOrEmpty(GpuHardwareKey) ? (SelectedFanProfile?.GpuHardwareKey ?? "UNKNOWN_GPU") : GpuHardwareKey,
            Name = EditFanName.Trim(),
            IsDefault = SelectedFanProfile?.IsDefault ?? false,
            ZeroRpmEnabled = EditFanZeroRpm,
            MinAcousticLimitMHz = EditFanUseMinAcoustic ? EditFanMinAcoustic : null,
            Points = EditFanPoints.Select(p => new FanCurvePoint(p.TemperatureC, p.FanSpeedPercent)).ToList()
        };
    }

    [RelayCommand]
    private async Task NewFanProfileAsync()
    {
        var pts = new[]
        {
            new FanCurvePoint(30, 15),
            new FanCurvePoint(50, 30),
            new FanCurvePoint(65, 50),
            new FanCurvePoint(78, 70),
            new FanCurvePoint(90, 90)
        };
        var nf = new FanProfile
        {
            GpuHardwareKey = string.IsNullOrEmpty(GpuHardwareKey) ? "UNKNOWN_GPU" : GpuHardwareKey,
            Name = $"Custom Fan {FanProfiles.Count + 1}",
            ZeroRpmEnabled = true,
            Points = pts
        };
        var saved = await _repo.SaveFanProfileAsync(nf, "UserCreated").ConfigureAwait(true);
        await LoadAllDataAsync().ConfigureAwait(true);
        SelectedFanProfile = FanProfiles.FirstOrDefault(f => f.FanProfileId == saved.FanProfileId);
        FanValidationHasErrors = false;
        FanValidationMessage = $"Created '{saved.Name}'. Customize the curve and click 'Save & Apply to GPU' to stick it onto your GPU.";
    }

    [RelayCommand]
    private async Task DuplicateFanProfileAsync()
    {
        var draft = BuildFanDraft();
        var copy = draft with
        {
            FanProfileId = Guid.NewGuid().ToString("B"),
            Name = $"{draft.Name} (Copy)",
            IsDefault = false,
            RevisionNumber = 1
        };
        var saved = await _repo.SaveFanProfileAsync(copy, "UserDuplicated").ConfigureAwait(true);
        await LoadAllDataAsync().ConfigureAwait(true);
        SelectedFanProfile = FanProfiles.FirstOrDefault(f => f.FanProfileId == saved.FanProfileId);
        FanValidationHasErrors = false;
        FanValidationMessage = $"Duplicated '{draft.Name}' -> '{saved.Name}'.";
    }

    [RelayCommand]
    private async Task ExportSelectedFanProfileAsync()
    {
        if (!ValidateCurrentFanDraft()) return;
        var profile = SelectedFanProfile ?? BuildFanDraft();
        string safeName = string.Concat(profile.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "RadeonGuard JSON (*.json)|*.json|AMD WattMan XML (*.xml)|*.xml",
            FileName = $"{safeName}.fanprofile.json"
        };
        if (dlg.ShowDialog() != true) return;

        if (Path.GetExtension(dlg.FileName).Equals(".xml", StringComparison.OrdinalIgnoreCase) || dlg.FilterIndex == 2)
        {
            await _portability.ExportFanProfileToXmlAsync(profile, dlg.FileName).ConfigureAwait(true);
            FanValidationHasErrors = false;
            FanValidationMessage = $"Exported fan profile '{profile.Name}' as AMD WattMan XML to {Path.GetFileName(dlg.FileName)}.";
        }
        else
        {
            await _portability.ExportFanProfilesAsync([profile], dlg.FileName).ConfigureAwait(true);
            FanValidationHasErrors = false;
            FanValidationMessage = $"Exported fan profile '{profile.Name}' (SHA-256 verified JSON) to {Path.GetFileName(dlg.FileName)}.";
        }
    }

    [RelayCommand]
    private async Task ExportAllFanProfilesAsync()
    {
        if (FanProfiles.Count == 0) return;
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "RadeonGuard Fan Profiles (*.json)|*.json",
            FileName = $"radeonguard_fan_profiles_{DateTime.Now:yyyyMMdd}.json"
        };
        if (dlg.ShowDialog() != true) return;

        await _portability.ExportFanProfilesAsync(FanProfiles.ToList(), dlg.FileName).ConfigureAwait(true);
        FanValidationHasErrors = false;
        FanValidationMessage = $"Exported {FanProfiles.Count} fan profile(s) to {Path.GetFileName(dlg.FileName)}.";
    }

    [RelayCommand]
    private async Task ImportAndApplyFanProfilesAsync()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Profile Files (*.json;*.xml)|*.json;*.xml|RadeonGuard JSON / XML-JSON (*.json)|*.json|AMD Adrenalin XML (*.xml)|*.xml"
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var imported = await _portability.ImportFanProfilesAsync(dlg.FileName, Capabilities, GpuHardwareKey).ConfigureAwait(true);
            await LoadAllDataAsync().ConfigureAwait(true);

            var primary = imported.FirstOrDefault(f => !f.IsDefault) ?? imported[0];
            SelectedFanProfile = FanProfiles.FirstOrDefault(f => f.FanProfileId == primary.FanProfileId);

            GuardianIpcClient.EnsureGuardianStarted();
            var res = await _ipc.ApplyFanProfileAsync(primary.FanProfileId).ConfigureAwait(true);
            if (res.Success)
            {
                ActiveFanProfileId = primary.FanProfileId;
                ActiveFanName = primary.Name;
                await EnsureAutoStartIfNotDisabledAsync().ConfigureAwait(true);
            }

            FanValidationHasErrors = !res.Success;
            FanValidationMessage = res.Success
                ? $"Imported {imported.Count} profile(s) and applied '{primary.Name}' to GPU ({res.ApplyStatus}). Locked until changed."
                : $"Imported {imported.Count} profile(s) to SQLite, but GPU apply failed: {res.ErrorMessage}";
        }
        catch (Exception ex)
        {
            FanValidationHasErrors = true;
            FanValidationMessage = "Fan profile import failed: " + ex.Message;
        }
    }

    [RelayCommand]
    private Task SaveFanOnlyAsync() => SaveFanProfileInternalAsync(false);

    [RelayCommand]
    private Task SaveAndApplyFanAsync() => SaveFanProfileInternalAsync(true);

    private async Task SaveFanProfileInternalAsync(bool applyAfterSave)
    {
        if (!ValidateCurrentFanDraft()) return;
        var saved = await _repo.SaveFanProfileAsync(BuildFanDraft(), "UserEdit").ConfigureAwait(true);
        await LoadAllDataAsync().ConfigureAwait(true);
        SelectedFanProfile = FanProfiles.FirstOrDefault(f => f.FanProfileId == saved.FanProfileId);

        if (applyAfterSave)
        {
            GuardianIpcClient.EnsureGuardianStarted();
            var res = await _ipc.ApplyFanProfileAsync(saved.FanProfileId).ConfigureAwait(true);
            if (res.Success)
            {
                ActiveFanProfileId = saved.FanProfileId;
                ActiveFanName = saved.Name;
                await EnsureAutoStartIfNotDisabledAsync().ConfigureAwait(true);
            }
            FanValidationHasErrors = !res.Success;
            FanValidationMessage = res.Success
                ? $"Applied & Locked '{saved.Name}' on GPU ({res.ApplyStatus}). Will stay active across restarts until you change it."
                : $"Saved to DB, but GPU apply failed: {res.ErrorMessage}";
        }
        else
        {
            FanValidationHasErrors = false;
            FanValidationMessage = $"Saved '{saved.Name}' (Rev {saved.RevisionNumber}) to SQLite.";
        }
    }

    [RelayCommand]
    private async Task DeleteFanProfileAsync()
    {
        if (SelectedFanProfile is null) return;
        try
        {
            await _repo.DeleteFanProfileAsync(SelectedFanProfile.FanProfileId).ConfigureAwait(true);
            await LoadAllDataAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            FanValidationHasErrors = true;
            FanValidationMessage = ex.Message;
        }
    }

    partial void OnSelectedTuningProfileChanged(TuningProfile? value)
    {
        if (value is null) return;
        EditTuningName = value.Name;
        EditTuningIsGlobal = value.IsGlobalDefault;
        EditTuningIsQuarantined = value.IsQuarantined;
        EditTuningQuarantineReason = value.QuarantineReason ?? string.Empty;
        EditEnableClock = value.EnableClockTuning;
        EditMinCoreClock = value.MinCoreClockMHz;
        EditMaxCoreClock = value.MaxCoreClockMHz;
        EditEnableVoltage = value.EnableVoltageTuning;
        EditCoreVoltage = value.CoreVoltageMv;
        EditEnableVram = value.EnableVramTuning;
        EditMaxVramClock = value.MaxVramClockMHz;
        EditVramTiming = value.VramTiming;
        EditEnablePower = value.EnablePowerTuning;
        EditPowerLimitDelta = value.PowerLimitDeltaPercent;
        EditLinkedFanProfileId = value.LinkedFanProfileId;
        EditAntiLag = value.ThreeDSettings.AntiLagEnabled;
        EditChill = value.ThreeDSettings.ChillEnabled;
        EditChillMinFps = value.ThreeDSettings.ChillMinFps;
        EditChillMaxFps = value.ThreeDSettings.ChillMaxFps;
        EditImageSharpening = value.ThreeDSettings.ImageSharpeningEnabled;
        EditSharpeningPercent = value.ThreeDSettings.ImageSharpeningPercent;
        EditEnhancedSync = value.ThreeDSettings.EnhancedSyncEnabled;
        EditFrtc = value.ThreeDSettings.FrtcEnabled;
        EditFrtcFps = value.ThreeDSettings.FrtcFps;
        TuningFeedbackMessage = string.Empty;
    }

    private TuningProfile BuildTuningDraft(bool clearQuarantine = false) => new()
    {
        TuningProfileId = SelectedTuningProfile?.TuningProfileId ?? Guid.NewGuid().ToString("B"),
        GpuHardwareKey = string.IsNullOrEmpty(GpuHardwareKey) ? (SelectedTuningProfile?.GpuHardwareKey ?? "UNKNOWN_GPU") : GpuHardwareKey,
        Name = EditTuningName.Trim(),
        IsGlobalDefault = EditTuningIsGlobal,
        IsQuarantined = clearQuarantine ? false : EditTuningIsQuarantined,
        QuarantineReason = clearQuarantine ? null : (string.IsNullOrEmpty(EditTuningQuarantineReason) ? null : EditTuningQuarantineReason),
        EnableClockTuning = EditEnableClock,
        MinCoreClockMHz = EditMinCoreClock,
        MaxCoreClockMHz = EditMaxCoreClock,
        EnableVoltageTuning = EditEnableVoltage,
        CoreVoltageMv = EditCoreVoltage,
        EnableVramTuning = EditEnableVram,
        MaxVramClockMHz = EditMaxVramClock,
        VramTiming = EditVramTiming,
        EnablePowerTuning = EditEnablePower,
        PowerLimitDeltaPercent = EditPowerLimitDelta,
        LinkedFanProfileId = string.IsNullOrEmpty(EditLinkedFanProfileId) ? (FanProfiles.FirstOrDefault()?.FanProfileId ?? string.Empty) : EditLinkedFanProfileId,
        ThreeDSettings = new ThreeDSettings
        {
            AntiLagEnabled = EditAntiLag,
            ChillEnabled = EditChill,
            ChillMinFps = EditChillMinFps,
            ChillMaxFps = EditChillMaxFps,
            ImageSharpeningEnabled = EditImageSharpening,
            ImageSharpeningPercent = EditSharpeningPercent,
            EnhancedSyncEnabled = EditEnhancedSync,
            FrtcEnabled = EditFrtc,
            FrtcFps = EditFrtcFps
        }
    };

    [RelayCommand]
    private async Task NewTuningProfileAsync()
    {
        var defFan = FanProfiles.FirstOrDefault();
        var nt = new TuningProfile
        {
            GpuHardwareKey = string.IsNullOrEmpty(GpuHardwareKey) ? "UNKNOWN_GPU" : GpuHardwareKey,
            Name = $"Custom Tuning {TuningProfiles.Count + 1}",
            LinkedFanProfileId = defFan?.FanProfileId ?? string.Empty
        };
        var saved = await _repo.SaveTuningProfileAsync(nt, "UserCreated").ConfigureAwait(true);
        await LoadAllDataAsync().ConfigureAwait(true);
        SelectedTuningProfile = TuningProfiles.FirstOrDefault(t => t.TuningProfileId == saved.TuningProfileId);
    }

    [RelayCommand]
    private Task SaveTuningOnlyAsync() => SaveTuningProfileInternalAsync(false);

    [RelayCommand]
    private Task SaveAndApplyTuningAsync() => SaveTuningProfileInternalAsync(true);

    private async Task SaveTuningProfileInternalAsync(bool applyAfterSave)
    {
        var draft = BuildTuningDraft(clearQuarantine: true);
        var val = ProfileValidator.ValidateTuningProfile(draft, Capabilities);
        if (!val.IsValid)
        {
            TuningFeedbackMessage = "Validation failed: " + string.Join(" ", val.Errors);
            return;
        }

        var saved = await _repo.SaveTuningProfileAsync(draft, "UserEdit").ConfigureAwait(true);
        await LoadAllDataAsync().ConfigureAwait(true);
        SelectedTuningProfile = TuningProfiles.FirstOrDefault(t => t.TuningProfileId == saved.TuningProfileId);

        if (applyAfterSave)
        {
            var res = await _ipc.ApplyTuningProfileAsync(saved.TuningProfileId, saved.IsGlobalDefault).ConfigureAwait(true);
            if (res.Success)
            {
                await EnsureAutoStartIfNotDisabledAsync().ConfigureAwait(true);
            }
            TuningFeedbackMessage = res.Success
                ? $"Applied & Verified '{saved.Name}' on GPU ({res.ApplyStatus})."
                : $"Saved to DB, but GPU apply failed: {res.ErrorMessage}";
        }
        else
        {
            TuningFeedbackMessage = $"Saved '{saved.Name}' (Rev {saved.RevisionNumber}) to SQLite.";
        }
    }

    [RelayCommand]
    private async Task DeleteTuningProfileAsync()
    {
        if (SelectedTuningProfile is null) return;
        try
        {
            await _repo.DeleteTuningProfileAsync(SelectedTuningProfile.TuningProfileId).ConfigureAwait(true);
            await LoadAllDataAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            TuningFeedbackMessage = ex.Message;
        }
    }

    partial void OnSelectedGameProfileChanged(GameProfile? value)
    {
        if (value is null) return;
        EditGameTitle = value.Title;
        EditGameExePath = value.Executables.FirstOrDefault(e => e.Role == ExecutableRole.PrimaryRenderer)?.ExeFullPath
                          ?? value.Executables.FirstOrDefault()?.ExeFullPath ?? string.Empty;
        EditGameTuningId = value.TuningProfileId;
        EditGameFanOverrideId = value.OverrideFanProfileId ?? string.Empty;
        EditGameEnabled = value.IsEnabled;
        EditGameLockRunning = value.LockWhileProcessRunning;
        EditGameZeroRpmOverride = value.ZeroRpmOverride;
    }

    [RelayCommand]
    private async Task ImportFromAdrenalinAsync()
    {
        var summary = await _ipc.ImportFromAdrenalinAsync().ConfigureAwait(true);
        if (summary is null)
        {
            var localImporter = new AdrenalinImporter(_repo);
            summary = await localImporter.ImportFromGmdbAsync(gpuHardwareKey: GpuHardwareKey).ConfigureAwait(true);
        }
        GameImportFeedback = $"Imported {summary.GamesImported} game(s), recovered {summary.FanCurvesRecovered} fan curve(s), and filtered out {summary.NonGamesFilteredOut} non-game false positive(s).";
        await LoadAllDataAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task RescanLaunchersAsync()
    {
        int added = await _ipc.RescanGamesAsync().ConfigureAwait(true);
        GameImportFeedback = $"Launcher scan complete: {added} new game(s) added.";
        await LoadAllDataAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task AddManualGameAsync()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Game Executables (*.exe)|*.exe",
            Title = "Select Game Executable"
        };
        if (dlg.ShowDialog() != true) return;

        string exePath = dlg.FileName;
        string title = Path.GetFileNameWithoutExtension(exePath);
        var defTuning = TuningProfiles.FirstOrDefault(t => t.IsGlobalDefault) ?? TuningProfiles.FirstOrDefault();
        if (defTuning is null) return;

        var gp = new GameProfile
        {
            Title = title,
            LauncherSource = "Manual",
            TuningProfileId = defTuning.TuningProfileId,
            IsEnabled = true,
            LockWhileProcessRunning = true,
            Executables =
            [
                new GameExecutable
                {
                    ExeFullPath = exePath,
                    ExeFileName = Path.GetFileName(exePath).ToLowerInvariant(),
                    Role = ExecutableRole.PrimaryRenderer
                }
            ]
        };
        var saved = await _repo.SaveGameProfileAsync(gp, "ManualAdd").ConfigureAwait(true);
        await LoadAllDataAsync().ConfigureAwait(true);
        SelectedGameProfile = GameProfiles.FirstOrDefault(g => g.GameProfileId == saved.GameProfileId);
    }

    [RelayCommand]
    private async Task SaveGameProfileAsync()
    {
        if (SelectedGameProfile is null) return;
        var exes = new List<GameExecutable>();
        if (!string.IsNullOrWhiteSpace(EditGameExePath))
        {
            exes.Add(new GameExecutable
            {
                GameProfileId = SelectedGameProfile.GameProfileId,
                ExeFullPath = EditGameExePath.Trim(),
                ExeFileName = Path.GetFileName(EditGameExePath.Trim()).ToLowerInvariant(),
                Role = ExecutableRole.PrimaryRenderer
            });
        }

        var updated = SelectedGameProfile with
        {
            Title = EditGameTitle.Trim(),
            TuningProfileId = EditGameTuningId,
            OverrideFanProfileId = string.IsNullOrEmpty(EditGameFanOverrideId) ? null : EditGameFanOverrideId,
            IsEnabled = EditGameEnabled,
            LockWhileProcessRunning = EditGameLockRunning,
            ZeroRpmOverride = EditGameZeroRpmOverride,
            Executables = exes
        };
        await _repo.SaveGameProfileAsync(updated, "UserEdit").ConfigureAwait(true);
        GuardianIpcClient.EnsureGuardianStarted();
        await _ipc.SendAsync("RefreshActiveGame").ConfigureAwait(true);
        GameImportFeedback = $"Saved game profile '{updated.Title}' (Zero RPM: {EditGameZeroRpmOverride}).";
        await LoadAllDataAsync().ConfigureAwait(true);
        SelectedGameProfile = GameProfiles.FirstOrDefault(g => g.GameProfileId == updated.GameProfileId);
    }

    [RelayCommand]
    private async Task DeleteGameProfileAsync()
    {
        if (SelectedGameProfile is null) return;
        await _repo.DeleteGameProfileAsync(SelectedGameProfile.GameProfileId).ConfigureAwait(true);
        await LoadAllDataAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task AddDenylistAsync()
    {
        if (string.IsNullOrWhiteSpace(NewDenylistExe)) return;
        await _repo.AddDenylistEntryAsync(NewDenylistExe, NewDenylistReason).ConfigureAwait(true);
        NewDenylistExe = string.Empty;
        await LoadAllDataAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task RemoveDenylistAsync(object? param)
    {
        if (param is not DenylistEntry entry || entry.IsBuiltIn) return;
        await _repo.RemoveDenylistEntryAsync(entry.ExeFileName).ConfigureAwait(true);
        await LoadAllDataAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    public async Task RefreshHistoryAndBackupsAsync()
    {
        var revs = await _repo.GetRevisionsAsync(limit: 100).ConfigureAwait(true);
        Revisions.Clear();
        foreach (var r in revs) Revisions.Add(r);

        var bks = await _backups.ListBackupsAsync().ConfigureAwait(true);
        Backups.Clear();
        foreach (var b in bks) Backups.Add(b);

        var audit = await _repo.GetAppliedStateLogAsync(100).ConfigureAwait(true);
        AuditLog.Clear();
        foreach (var a in audit) AuditLog.Add(a);
    }

    [RelayCommand]
    private async Task RollbackRevisionAsync(object? param)
    {
        if (param is not ProfileRevision rev) return;
        bool ok = await _repo.RollbackRevisionAsync(rev.RevisionId).ConfigureAwait(true);
        HistoryFeedback = ok ? $"Rolled back {rev.EntityType} to Revision #{rev.RevisionNumber}." : "Rollback failed.";
        await LoadAllDataAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task CreateBackupNowAsync()
    {
        var info = await _backups.CreateBackupAsync("manual").ConfigureAwait(true);
        HistoryFeedback = $"Created atomic SHA-256 backup: {Path.GetFileName(info.FilePath)}";
        await RefreshHistoryAndBackupsAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task RestoreBackupAsync(object? param)
    {
        if (param is not BackupInfo info) return;
        bool ok = await _backups.RestoreBackupAsync(info.FilePath).ConfigureAwait(true);
        HistoryFeedback = ok ? $"Restored database from {Path.GetFileName(info.FilePath)}." : "Backup SHA-256 verification failed!";
        await LoadAllDataAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task ExportBundleAsync()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "RadeonGuard Bundle (*.json)|*.json",
            FileName = $"radeonguard_profiles_{DateTime.Now:yyyyMMdd}.json"
        };
        if (dlg.ShowDialog() != true) return;
        await _portability.ExportBundleAsync(dlg.FileName).ConfigureAwait(true);
        HistoryFeedback = $"Exported all profiles with SHA-256 seal to {dlg.FileName}.";
    }

    [RelayCommand]
    private async Task ImportBundleAsync()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Profile / Bundle Files (*.json;*.xml)|*.json;*.xml|RadeonGuard Bundle (*.json)|*.json|AMD Adrenalin XML (*.xml)|*.xml"
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var (f, t, g) = await _portability.ImportBundleAsync(dlg.FileName).ConfigureAwait(true);
            HistoryFeedback = $"Imported {f} fan profile(s), {t} tuning profile(s), and {g} game profile(s).";
            await LoadAllDataAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            HistoryFeedback = "Import failed: " + ex.Message;
        }
    }

    [RelayCommand]
    public async Task RunDiagnosticsAsync()
    {
        var report = await _ipc.RunDiagnosticsAsync().ConfigureAwait(true);
        if (report is not null)
        {
            ConflictingProcesses.Clear();
            foreach (var p in report.RunningGpuProcesses) ConflictingProcesses.Add(p);
            foreach (var s in report.ServiceStates) ConflictingProcesses.Add($"Service: {s.ServiceName} -> {s.Status}");
            ConflictingProcesses.Add($"Windows Fast Startup (Hiberboot): {(report.FastStartupEnabled ? "Enabled" : "Disabled")}");

            DiagnosticRecommendations.Clear();
            foreach (var r in report.Recommendations) DiagnosticRecommendations.Add(r);

            TdrEvents.Clear();
            if (report.RecentTdrEvents.Count == 0)
            {
                TdrEvents.Add("No display driver TDR resets (Event ID 4101) detected in the last 7 days.");
            }
            else
            {
                foreach (var t in report.RecentTdrEvents) TdrEvents.Add(t);
            }
        }

        var lines = await FlightRecorder.ReadRecentLogLinesAsync(150).ConfigureAwait(true);
        FlightLogs.Clear();
        foreach (var l in lines.Reverse()) FlightLogs.Add(l);

        await RefreshHistoryAndBackupsAsync().ConfigureAwait(true);
    }

    partial void OnAutoReapplyOnConflictChanged(bool value)
    {
        var policy = value ? ConflictResolutionPolicy.AutoReapply : ConflictResolutionPolicy.AlertOnly;
        _ = _ipc.SendAsync("SetConflictPolicy", policy.ToString());
    }

    partial void OnAutoStartWithWindowsChanged(bool value)
    {
        if (_suppressAutoStartSave) return;
        bool ok = StartupRegistrationService.SetGuardianAutoStart(value);
        _ = _repo.SetSettingAsync(SettingAutoStartDisabled, value ? "false" : "true");
        if (ok)
        {
            FanValidationHasErrors = false;
            FanValidationMessage = value
                ? "Enabled auto-restore at Windows logon — your active profile will automatically reapply after PC restarts."
                : "Disabled auto-restore at Windows logon.";
        }
        else
        {
            FanValidationHasErrors = true;
            FanValidationMessage = "Could not update Windows startup entry (check permissions or locate RadeonGuard.Guardian.exe).";
        }
    }

    private async Task EnsureAutoStartIfNotDisabledAsync()
    {
        string? disabled = await _repo.GetSettingAsync(SettingAutoStartDisabled).ConfigureAwait(true);
        if (string.Equals(disabled, "true", StringComparison.OrdinalIgnoreCase)) return;

        if (StartupRegistrationService.SetGuardianAutoStart(true))
        {
            _suppressAutoStartSave = true;
            AutoStartWithWindows = StartupRegistrationService.IsGuardianAutoStartEnabled();
            _suppressAutoStartSave = false;
        }
    }

    public void Dispose()
    {
        _pollCts.Cancel();
        try { _pollTask?.Wait(TimeSpan.FromSeconds(1)); } catch { }
        _pollCts.Dispose();
        _repo.Dispose();
    }
}

public sealed record ZeroRpmOverrideOption(ZeroRpmOverrideMode Mode, string DisplayName);
public sealed record GameFanOverrideOption(string FanProfileId, string DisplayName);
