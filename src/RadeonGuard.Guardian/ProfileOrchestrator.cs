using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using RadeonGuard.Core.Hardware;
using RadeonGuard.Core.IPC;
using RadeonGuard.Core.Models;
using RadeonGuard.Diagnostics;
using RadeonGuard.Persistence;
using Serilog;

namespace RadeonGuard.Guardian;

public sealed class ProfileOrchestrator : IDisposable
{
    private readonly IGpuHardwareAdapter _hardware;
    private readonly RadeonGuardRepository _repo;
    private readonly BackupManager _backups;
    private readonly CrashBreadcrumbManager _breadcrumbs;
    private readonly ILogger _log;
    private readonly GameProcessTracker _gameTracker;
    private readonly SemaphoreSlim _applyGate = new(1, 1);
    private readonly Func<IReadOnlyList<RunningProcessSnapshot>> _processProvider;
    private readonly Func<int?> _foregroundPidProvider;

    private GpuDeviceInfo? _gpuInfo;
    private GpuTuningCapabilities? _capabilities;
    private GpuTelemetrySample? _latestMetrics;
    private FanTuningHardwareSnapshot? _liveFanState;
    private ManualTuningSnapshot? _liveManualState;
    private ThreeDSettings? _liveThreeDState;

    private TuningProfile? _activeManagedTuning;
    private FanProfile? _activeManagedFan;
    private ActiveGameMatch? _activeGame;
    private bool _hasManagedSession;
    private string _hardwareSyncState = "MONITORING";
    private ConflictResolutionPolicy _conflictPolicy = ConflictResolutionPolicy.AutoReapply;
    private DateTimeOffset _lastInternalApplyUtc = DateTimeOffset.MinValue;
    private volatile bool _externalDriftFlagged;

    public const string SettingActiveGlobalTuningId = "ActiveGlobalTuningId";
    public const string SettingActiveGlobalFanId = "ActiveGlobalFanId";
    public const string SettingHasUserManagedConfig = "HasUserManagedConfig";
    public const string SettingConflictPolicy = "ConflictPolicy";

    public ProfileOrchestrator(
        IGpuHardwareAdapter hardware,
        RadeonGuardRepository repo,
        BackupManager backups,
        CrashBreadcrumbManager breadcrumbs,
        ILogger log,
        Func<IReadOnlyList<RunningProcessSnapshot>>? processProvider = null,
        Func<int?>? foregroundPidProvider = null,
        TimeSpan? gameExitDebounce = null)
    {
        _hardware = hardware;
        _repo = repo;
        _backups = backups;
        _breadcrumbs = breadcrumbs;
        _log = log;
        _gameTracker = new GameProcessTracker(gameExitDebounce);
        _processProvider = processProvider ?? CaptureRunningProcesses;
        _foregroundPidProvider = foregroundPidProvider ?? GetForegroundProcessId;
        _hardware.TuningChanged += OnHardwareTuningChanged;
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        await _repo.InitializeAsync(StoragePaths.GetBackupsDirectory(), ct).ConfigureAwait(false);

        var res = await _hardware.InitializeAsync(ct).ConfigureAwait(false);
        _log.Information("ADLX Initialization result: {Result}, Version: {Version}", res, _hardware.AdlxVersionString);

        if (_hardware.IsInitialized)
        {
            _gpuInfo = await _hardware.GetGpuInfoAsync(0, ct).ConfigureAwait(false);
            _capabilities = await _hardware.GetTuningCapabilitiesAsync(0, ct).ConfigureAwait(false);
            if (_gpuInfo is not null)
            {
                await _repo.UpsertGpuDeviceAsync(_gpuInfo, _capabilities, ct).ConfigureAwait(false);
            }
            await RefreshHardwareSnapshotsAsync(ct).ConfigureAwait(false);
        }

        string hwKey = _gpuInfo?.HardwareKey ?? "UNKNOWN_GPU";
        var (defaultFan, defaultTuning) = await _repo.EnsureDefaultProfilesAsync(hwKey, _liveFanState, _liveManualState, ct).ConfigureAwait(false);

        var policyStr = await _repo.GetSettingAsync(SettingConflictPolicy, ct).ConfigureAwait(false);
        if (Enum.TryParse<ConflictResolutionPolicy>(policyStr, out var parsedPolicy))
        {
            _conflictPolicy = parsedPolicy;
        }

        // Crash recovery check: if an unclean shutdown happened during tuning apply, quarantine that profile
        // while keeping fan profiles intact!
        var crashCrumb = await _breadcrumbs.CheckAndHandleCrashRecoveryAsync(_repo, ct).ConfigureAwait(false);
        if (crashCrumb is not null)
        {
            _log.Warning("Recovered from unclean shutdown during profile '{Profile}' apply at {StartedAt}. Profile quarantined; Fan curve preserved.",
                crashCrumb.TuningProfileName, crashCrumb.StartedAtUtc);
        }

        // Only auto-apply saved global configuration at startup if the user has explicitly saved/applied a managed config.
        string? managedFlag = await _repo.GetSettingAsync(SettingHasUserManagedConfig, ct).ConfigureAwait(false);
        _hasManagedSession = string.Equals(managedFlag, "true", StringComparison.OrdinalIgnoreCase);

        if (_hasManagedSession && _hardware.IsInitialized)
        {
            await ApplyGlobalProfileFromStorageAsync("StartupRestore", ct).ConfigureAwait(false);
        }
        else
        {
            _activeManagedTuning = defaultTuning;
            _activeManagedFan = defaultFan;
            _hardwareSyncState = _hardware.IsInitialized ? "READ_ONLY_MONITORING" : "ADLX_UNAVAILABLE";
        }
    }

    public async Task PollTelemetryTickAsync(bool includeGameWatcher, CancellationToken ct = default)
    {
        if (!_hardware.IsInitialized)
        {
            await EnsureHardwareInitializedAndRestoredAsync(ct).ConfigureAwait(false);
            if (!_hardware.IsInitialized) return;
        }

        _latestMetrics = await _hardware.GetCurrentMetricsAsync(0, ct).ConfigureAwait(false);

        if (includeGameWatcher)
        {
            await EvaluateGameProfilesAsync(ct).ConfigureAwait(false);
        }
    }

    private async Task EnsureHardwareInitializedAndRestoredAsync(CancellationToken ct)
    {
        var res = await _hardware.InitializeAsync(ct).ConfigureAwait(false);
        if (!_hardware.IsInitialized) return;

        _log.Information("Delayed ADLX initialization succeeded (Version: {Version}, Result: {Result}).",
            _hardware.AdlxVersionString, res);

        _gpuInfo = await _hardware.GetGpuInfoAsync(0, ct).ConfigureAwait(false);
        _capabilities = await _hardware.GetTuningCapabilitiesAsync(0, ct).ConfigureAwait(false);
        if (_gpuInfo is not null)
        {
            await _repo.UpsertGpuDeviceAsync(_gpuInfo, _capabilities, ct).ConfigureAwait(false);
        }
        await RefreshHardwareSnapshotsAsync(ct).ConfigureAwait(false);

        string hwKey = _gpuInfo?.HardwareKey ?? "UNKNOWN_GPU";
        var (defaultFan, defaultTuning) = await _repo.EnsureDefaultProfilesAsync(hwKey, _liveFanState, _liveManualState, ct).ConfigureAwait(false);

        if (_hasManagedSession)
        {
            await ApplyGlobalProfileFromStorageAsync("DelayedStartupRestore", ct).ConfigureAwait(false);
        }
        else
        {
            _activeManagedTuning = defaultTuning;
            _activeManagedFan = defaultFan;
            _hardwareSyncState = "READ_ONLY_MONITORING";
        }
    }

    public async Task VerifyAndGuardHardwareTickAsync(CancellationToken ct = default)
    {
        if (!_hardware.IsInitialized) return;

        await RefreshHardwareSnapshotsAsync(ct).ConfigureAwait(false);

        if (!_hasManagedSession || _activeManagedFan is null || _activeManagedTuning is null)
        {
            return;
        }

        // Ignore transient notifications right after our own apply
        if ((DateTimeOffset.UtcNow - _lastInternalApplyUtc) < TimeSpan.FromMilliseconds(800))
        {
            return;
        }

        bool fanOk = AdlxHardwareAdapter.VerifyFanMatch(_activeManagedFan, _liveFanState);
        bool tuningOk = AdlxHardwareAdapter.VerifyManualMatch(_activeManagedTuning, _liveManualState);

        if (!fanOk || !tuningOk || _externalDriftFlagged)
        {
            _externalDriftFlagged = false;
            string reason = !fanOk && !tuningOk ? "Fan+TuningDrift" : (!fanOk ? "FanCurveDrift" : "TuningDrift");
            _log.Warning("Hardware drift detected ({Reason}). Expected Tuning='{Tuning}', Fan='{Fan}'.",
                reason, _activeManagedTuning.Name, _activeManagedFan.Name);

            await _repo.LogAppliedStateAsync(new AppliedStateRecord(
                0,
                _gpuInfo?.HardwareKey ?? "UNKNOWN",
                _activeManagedTuning.TuningProfileId,
                _activeManagedTuning.Name,
                _activeManagedFan.FanProfileId,
                _activeManagedFan.Name,
                "SAVED",
                "DRIFTED",
                JsonSerializer.Serialize(new { LiveFan = _liveFanState, LiveManual = _liveManualState }),
                null,
                reason,
                DateTimeOffset.UtcNow), ct).ConfigureAwait(false);

            if (_conflictPolicy == ConflictResolutionPolicy.AutoReapply)
            {
                _hardwareSyncState = "RECOVERING";
                await Task.Delay(350, ct).ConfigureAwait(false);
                await ApplyPairInternalAsync(_activeManagedTuning, _activeManagedFan, $"DriftAutoRecover:{reason}", ct).ConfigureAwait(false);
            }
            else
            {
                _hardwareSyncState = "DRIFT_DETECTED";
            }
        }
        else
        {
            _hardwareSyncState = "VERIFIED";
        }
    }

    public async Task OnSystemResumeAsync(CancellationToken ct = default)
    {
        _log.Information("System resume detected; verifying GPU configuration.");
        if (_hasManagedSession && _activeManagedTuning is not null && _activeManagedFan is not null && _hardware.IsInitialized)
        {
            await ApplyPairInternalAsync(_activeManagedTuning, _activeManagedFan, "PowerResumeRestore", ct).ConfigureAwait(false);
        }
    }

    public async Task<HardwareApplyResult> ApplyUserFanProfileAsync(string fanProfileId, CancellationToken ct = default)
    {
        var fan = await _repo.GetFanProfileByIdAsync(fanProfileId, ct).ConfigureAwait(false);
        if (fan is null)
        {
            return new HardwareApplyResult(false, "FAILED", AdlxResultCode.NotFound, "Fan profile not found.", "{}");
        }

        await _repo.SetSettingAsync(SettingActiveGlobalFanId, fan.FanProfileId, ct).ConfigureAwait(false);
        await _repo.SetSettingAsync(SettingHasUserManagedConfig, "true", ct).ConfigureAwait(false);
        _hasManagedSession = true;
        _activeManagedFan = fan;

        await _applyGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _lastInternalApplyUtc = DateTimeOffset.UtcNow;
            var res = await _hardware.ApplyFanProfileAsync(0, fan, ct).ConfigureAwait(false);
            _hardwareSyncState = res.ApplyStatus;
            await RefreshHardwareSnapshotsAsync(ct).ConfigureAwait(false);

            await _repo.LogAppliedStateAsync(new AppliedStateRecord(
                0,
                _gpuInfo?.HardwareKey ?? "UNKNOWN",
                _activeManagedTuning?.TuningProfileId ?? "FAN_ONLY",
                _activeManagedTuning?.Name ?? "Current",
                fan.FanProfileId,
                fan.Name,
                "SAVED",
                res.ApplyStatus,
                res.ReadbackSummaryJson,
                res.Success ? null : res.ResultCode.ToString(),
                "UserFanApply",
                DateTimeOffset.UtcNow), ct).ConfigureAwait(false);

            return res;
        }
        finally
        {
            _applyGate.Release();
        }
    }

    public async Task<HardwareApplyResult> ApplyUserTuningProfileAsync(string tuningProfileId, bool setAsGlobalDefault = false, CancellationToken ct = default)
    {
        var tuning = await _repo.GetTuningProfileByIdAsync(tuningProfileId, ct).ConfigureAwait(false);
        if (tuning is null)
        {
            return new HardwareApplyResult(false, "FAILED", AdlxResultCode.NotFound, "Tuning profile not found.", "{}");
        }

        var existingGlobalFanId = await _repo.GetSettingAsync(SettingActiveGlobalFanId, ct).ConfigureAwait(false);
        bool preserveStickyGlobalFan =
            tuning.IsGlobalDefault &&
            !string.IsNullOrEmpty(existingGlobalFanId) &&
            !string.Equals(existingGlobalFanId, RadeonGuardRepository.DefaultFanProfileId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(tuning.LinkedFanProfileId, RadeonGuardRepository.DefaultFanProfileId, StringComparison.OrdinalIgnoreCase);

        var fan = preserveStickyGlobalFan
            ? await _repo.GetFanProfileByIdAsync(existingGlobalFanId!, ct).ConfigureAwait(false)
            : await _repo.GetFanProfileByIdAsync(tuning.LinkedFanProfileId, ct).ConfigureAwait(false);

        if (setAsGlobalDefault || tuning.IsGlobalDefault)
        {
            await _repo.SetSettingAsync(SettingActiveGlobalTuningId, tuning.TuningProfileId, ct).ConfigureAwait(false);
            if (fan is not null)
            {
                await _repo.SetSettingAsync(SettingActiveGlobalFanId, fan.FanProfileId, ct).ConfigureAwait(false);
            }
        }
        await _repo.SetSettingAsync(SettingHasUserManagedConfig, "true", ct).ConfigureAwait(false);
        _hasManagedSession = true;

        return await ApplyPairInternalAsync(tuning, fan, "UserTuningApply", ct).ConfigureAwait(false);
    }

    public async Task SetConflictPolicyAsync(ConflictResolutionPolicy policy, CancellationToken ct = default)
    {
        _conflictPolicy = policy;
        await _repo.SetSettingAsync(SettingConflictPolicy, policy.ToString(), ct).ConfigureAwait(false);
        _log.Information("Conflict policy updated to {Policy}", policy);
    }

    public Task EvaluateGameProfilesAsync(CancellationToken ct = default)
        => EvaluateGameProfilesInternalAsync(forceReapplyActiveGame: false, ct);

    /// <summary>
    /// Re-evaluates running games and forces re-application if a tracked game is still active
    /// (for example after editing its Zero RPM override in the UI while the game runs).
    /// </summary>
    public Task RefreshActiveGameAsync(CancellationToken ct = default)
        => EvaluateGameProfilesInternalAsync(forceReapplyActiveGame: true, ct);

    private async Task EvaluateGameProfilesInternalAsync(bool forceReapplyActiveGame, CancellationToken ct)
    {
        var games = await _repo.GetGameProfilesAsync(ct).ConfigureAwait(false);
        if (games.Count == 0 && _activeGame is null) return;

        var denylist = (await _repo.GetDenylistAsync(ct).ConfigureAwait(false))
            .Select(d => d.ExeFileName.ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var running = _processProvider();
        int? fgPid = _foregroundPidProvider();
        var prevGame = _activeGame;
        var nextGame = _gameTracker.Evaluate(running, fgPid, games, denylist, DateTimeOffset.UtcNow);

        bool sameGame = prevGame?.Game.GameProfileId == nextGame?.Game.GameProfileId;
        if (sameGame && (!forceReapplyActiveGame || nextGame is null))
        {
            _activeGame = nextGame;
            return;
        }

        _activeGame = nextGame;
        if (nextGame is not null)
        {
            _log.Information("Applying game profile '{Title}' (PID {Pid}, Exe {Exe}, ZeroRPM: {ZeroRpm})...",
                nextGame.Game.Title, nextGame.MatchedPid, nextGame.MatchedExeName, nextGame.Game.ZeroRpmOverride);

            var tuning = await _repo.GetTuningProfileByIdAsync(nextGame.Game.TuningProfileId, ct).ConfigureAwait(false);
            if (tuning is not null)
            {
                var baseFan = await ResolveGameBaseFanProfileAsync(nextGame.Game, tuning, ct).ConfigureAwait(false);
                var effectiveFan = nextGame.Game.ApplyZeroRpmOverride(baseFan);
                _hasManagedSession = true;
                await _repo.SetSettingAsync(SettingHasUserManagedConfig, "true", ct).ConfigureAwait(false);

                string trigger = sameGame ? $"GameProfileUpdated:{nextGame.Game.Title}" : $"GameLaunched:{nextGame.Game.Title}";
                await ApplyPairInternalAsync(tuning, effectiveFan, trigger, ct).ConfigureAwait(false);

                if (!sameGame)
                {
                    var updatedGame = nextGame.Game with { LastLaunchedUtc = DateTimeOffset.UtcNow };
                    await _repo.SaveGameProfileAsync(updatedGame, "GameLaunchedTimestamp", ct).ConfigureAwait(false);
                }
            }
        }
        else if (prevGame is not null)
        {
            _log.Information("Game '{Title}' exited or disabled. Restoring Global profile...", prevGame.Game.Title);
            await ApplyGlobalProfileFromStorageAsync($"GameExited:{prevGame.Game.Title}", ct).ConfigureAwait(false);
        }
    }

    private async Task<FanProfile?> ResolveGameBaseFanProfileAsync(GameProfile game, TuningProfile tuning, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(game.OverrideFanProfileId))
        {
            var overrideFan = await _repo.GetFanProfileByIdAsync(game.OverrideFanProfileId, ct).ConfigureAwait(false);
            if (overrideFan is not null) return overrideFan;
        }

        string? globalFanId = await _repo.GetSettingAsync(SettingActiveGlobalFanId, ct).ConfigureAwait(false);
        if (tuning.IsGlobalDefault && !string.IsNullOrEmpty(globalFanId))
        {
            var stickyGlobalFan = await _repo.GetFanProfileByIdAsync(globalFanId, ct).ConfigureAwait(false);
            if (stickyGlobalFan is not null) return stickyGlobalFan;
        }

        var linkedFan = await _repo.GetFanProfileByIdAsync(tuning.LinkedFanProfileId, ct).ConfigureAwait(false);
        if (linkedFan is not null) return linkedFan;

        if (!string.IsNullOrEmpty(globalFanId))
        {
            var globalFan = await _repo.GetFanProfileByIdAsync(globalFanId, ct).ConfigureAwait(false);
            if (globalFan is not null) return globalFan;
        }

        string hwKey = _gpuInfo?.HardwareKey ?? "UNKNOWN_GPU";
        var (defaultFan, _) = await _repo.EnsureDefaultProfilesAsync(hwKey, _liveFanState, _liveManualState, ct).ConfigureAwait(false);
        return defaultFan;
    }

    private async Task ApplyGlobalProfileFromStorageAsync(string triggerReason, CancellationToken ct)
    {
        string hwKey = _gpuInfo?.HardwareKey ?? "UNKNOWN_GPU";
        var (defaultFan, defaultTuning) = await _repo.EnsureDefaultProfilesAsync(hwKey, _liveFanState, _liveManualState, ct).ConfigureAwait(false);

        string? globalTuningId = await _repo.GetSettingAsync(SettingActiveGlobalTuningId, ct).ConfigureAwait(false);
        string? globalFanId = await _repo.GetSettingAsync(SettingActiveGlobalFanId, ct).ConfigureAwait(false);

        var tuning = (!string.IsNullOrEmpty(globalTuningId)
            ? await _repo.GetTuningProfileByIdAsync(globalTuningId, ct).ConfigureAwait(false)
            : null) ?? defaultTuning;

        var fan = (!string.IsNullOrEmpty(globalFanId)
            ? await _repo.GetFanProfileByIdAsync(globalFanId, ct).ConfigureAwait(false)
            : null) ?? await _repo.GetFanProfileByIdAsync(tuning.LinkedFanProfileId, ct).ConfigureAwait(false) ?? defaultFan;

        await ApplyPairInternalAsync(tuning, fan, triggerReason, ct).ConfigureAwait(false);
    }

    private async Task<HardwareApplyResult> ApplyPairInternalAsync(TuningProfile tuning, FanProfile? fan, string triggerReason, CancellationToken ct)
    {
        await _applyGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            bool riskyChange = tuning.EnableClockTuning || tuning.EnableVoltageTuning || tuning.EnableVramTuning || tuning.EnablePowerTuning;
            if (riskyChange)
            {
                _breadcrumbs.WritePreApply(new ApplyBreadcrumb(
                    tuning.TuningProfileId,
                    tuning.Name,
                    fan?.FanProfileId ?? string.Empty,
                    true,
                    DateTimeOffset.UtcNow));
            }

            _lastInternalApplyUtc = DateTimeOffset.UtcNow;
            var res = await _hardware.ApplyTuningProfileAsync(0, tuning, fan, ct).ConfigureAwait(false);
            _breadcrumbs.Clear();

            if (res.Success)
            {
                _activeManagedTuning = tuning;
                if (fan is not null) _activeManagedFan = fan;
            }
            _hardwareSyncState = res.ApplyStatus;
            await RefreshHardwareSnapshotsAsync(ct).ConfigureAwait(false);

            await _repo.LogAppliedStateAsync(new AppliedStateRecord(
                0,
                _gpuInfo?.HardwareKey ?? "UNKNOWN",
                tuning.TuningProfileId,
                tuning.Name,
                fan?.FanProfileId ?? string.Empty,
                fan?.Name ?? "None",
                "SAVED",
                res.ApplyStatus,
                res.ReadbackSummaryJson,
                res.Success ? null : res.ResultCode.ToString(),
                triggerReason,
                DateTimeOffset.UtcNow), ct).ConfigureAwait(false);

            _log.Information("Applied profile '{Tuning}' + Fan '{Fan}' ({Trigger}) -> Status: {Status}",
                tuning.Name, fan?.Name ?? "None", triggerReason, res.ApplyStatus);

            return res;
        }
        finally
        {
            _applyGate.Release();
        }
    }

    private async Task RefreshHardwareSnapshotsAsync(CancellationToken ct)
    {
        _liveFanState = await _hardware.GetFanTuningStateAsync(0, false, ct).ConfigureAwait(false);
        _liveManualState = await _hardware.GetManualTuningStateAsync(0, false, ct).ConfigureAwait(false);
        _liveThreeDState = await _hardware.Get3DSettingsStateAsync(0, ct).ConfigureAwait(false);
    }

    private void OnHardwareTuningChanged(object? sender, TuningChangedNotification e)
    {
        if (e.Origin == SyncOrigin.External ||
            (e.Origin == SyncOrigin.Unknown && (DateTimeOffset.UtcNow - _lastInternalApplyUtc) > TimeSpan.FromSeconds(1)))
        {
            _externalDriftFlagged = true;
            _log.Warning("External ADLX tuning change event received (Origin={Origin}, Fan={Fan}, Gfx={Gfx}, Vram={Vram}, Power={Pwr}).",
                e.Origin, e.IsManualFanChanged, e.IsManualGfxChanged, e.IsManualVramChanged, e.IsManualPowerChanged);
        }
    }

    public GuardianStatusSnapshot BuildStatusSnapshot() => new(
        true,
        _hardware.IsInitialized,
        _hardware.AdlxVersionString,
        _gpuInfo,
        _latestMetrics,
        _capabilities,
        _liveFanState,
        _liveManualState,
        _liveThreeDState,
        _activeManagedTuning?.TuningProfileId ?? string.Empty,
        _activeManagedTuning?.Name ?? "Unmanaged",
        _activeManagedFan?.FanProfileId ?? string.Empty,
        _activeManagedFan?.Name ?? "Unmanaged",
        _activeGame?.Game.Title,
        _activeGame?.MatchedPid,
        _hardwareSyncState,
        _conflictPolicy,
        DateTimeOffset.UtcNow);

    private static IReadOnlyList<RunningProcessSnapshot> CaptureRunningProcesses()
    {
        var list = new List<RunningProcessSnapshot>();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                string? fullPath = null;
                try { fullPath = p.MainModule?.FileName; } catch { }
                list.Add(new RunningProcessSnapshot(p.Id, p.ProcessName + ".exe", fullPath));
            }
            catch { }
            finally
            {
                p.Dispose();
            }
        }
        return list;
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    private static int? GetForegroundProcessId()
    {
        nint hwnd = GetForegroundWindow();
        if (hwnd == 0) return null;
        GetWindowThreadProcessId(hwnd, out uint pid);
        return pid > 0 ? (int)pid : null;
    }

    public void Dispose()
    {
        _hardware.TuningChanged -= OnHardwareTuningChanged;
        _applyGate.Dispose();
    }
}
