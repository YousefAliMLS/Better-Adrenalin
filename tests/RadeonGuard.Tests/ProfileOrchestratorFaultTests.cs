using RadeonGuard.Core.Hardware;
using RadeonGuard.Core.Models;
using RadeonGuard.Diagnostics;
using RadeonGuard.Guardian;
using RadeonGuard.Persistence;
using Serilog;
using Xunit;

namespace RadeonGuard.Tests;

public sealed class ProfileOrchestratorFaultTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;
    private readonly string _backupsDir;
    private readonly string _breadcrumbPath;

    public ProfileOrchestratorFaultTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "RadeonGuardOrchTests_" + Guid.NewGuid().ToString("N"));
        _dbPath = Path.Combine(_tempDir, "radeonguard.db");
        _backupsDir = Path.Combine(_tempDir, "backups");
        _breadcrumbPath = Path.Combine(_tempDir, "apply_breadcrumb.json");
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public async Task ExternalFanReset_IsDetectedAndAutomaticallyRestored()
    {
        using var mock = new MockGpuHardwareAdapter();
        using var repo = new RadeonGuardRepository(_dbPath);
        var backups = new BackupManager(_dbPath, _backupsDir);
        var breadcrumbs = new CrashBreadcrumbManager(_breadcrumbPath);
        var log = new LoggerConfiguration().CreateLogger();

        using var orch = new ProfileOrchestrator(mock, repo, backups, breadcrumbs, log);
        await orch.StartAsync();

        var customFan = await repo.SaveFanProfileAsync(new FanProfile
        {
            GpuHardwareKey = "PCI\\VEN_1002&DEV_73BF&SUBSYS_439E1DA2&REV_C1",
            Name = "My Permanent Curve",
            ZeroRpmEnabled = false,
            Points =
            [
                new FanCurvePoint(30, 25),
                new FanCurvePoint(50, 40),
                new FanCurvePoint(63, 55),
                new FanCurvePoint(72, 70),
                new FanCurvePoint(80, 90)
            ]
        });

        var applyRes = await orch.ApplyUserFanProfileAsync(customFan.FanProfileId);
        Assert.True(applyRes.Success);
        Assert.Equal("VERIFIED", applyRes.ApplyStatus);

        // Simulate Adrenalin or Afterburner wiping the GPU fan curve externally!
        mock.SimulateExternalFanReset();
        await Task.Delay(900); // pass internal-apply debounce window

        await orch.VerifyAndGuardHardwareTickAsync();

        var snap = orch.BuildStatusSnapshot();
        Assert.Equal("VERIFIED", snap.HardwareSyncState);
        Assert.NotNull(snap.LiveFanState);
        Assert.False(snap.LiveFanState!.ZeroRpmEnabled);
        Assert.Equal(55, snap.LiveFanState.Points[2].FanSpeedPercent);

        var audit = await repo.GetAppliedStateLogAsync(10);
        Assert.Contains(audit, a => a.ApplyStatus == "DRIFTED");
        Assert.Contains(audit, a => a.TriggerReason.StartsWith("DriftAutoRecover", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CrashDuringRiskyTuningApply_QuarantinesProfile_WithoutDeletingFanProfile()
    {
        using (var repo = new RadeonGuardRepository(_dbPath))
        {
            await repo.InitializeAsync(_backupsDir);
            var (defFan, _) = await repo.EnsureDefaultProfilesAsync("RX6800XT");
            var risky = await repo.SaveTuningProfileAsync(new TuningProfile
            {
                GpuHardwareKey = "RX6800XT",
                Name = "Aggressive Undervolt",
                EnableClockTuning = true,
                MinCoreClockMHz = 2100,
                MaxCoreClockMHz = 2550,
                EnableVoltageTuning = true,
                CoreVoltageMv = 950,
                LinkedFanProfileId = defFan.FanProfileId
            });

            var breadcrumbs = new CrashBreadcrumbManager(_breadcrumbPath);
            breadcrumbs.WritePreApply(new ApplyBreadcrumb(risky.TuningProfileId, risky.Name, defFan.FanProfileId, true, DateTimeOffset.UtcNow));
        }

        using var mock = new MockGpuHardwareAdapter();
        using var repo2 = new RadeonGuardRepository(_dbPath);
        var backups2 = new BackupManager(_dbPath, _backupsDir);
        var breadcrumbs2 = new CrashBreadcrumbManager(_breadcrumbPath);
        var log = new LoggerConfiguration().CreateLogger();

        using var orch = new ProfileOrchestrator(mock, repo2, backups2, breadcrumbs2, log);
        await orch.StartAsync();

        var tunings = await repo2.GetTuningProfilesAsync();
        var quarantined = tunings.Single(t => t.Name == "Aggressive Undervolt");
        Assert.True(quarantined.IsQuarantined);
        Assert.NotNull(quarantined.QuarantineReason);

        // Fan profile remains intact!
        var fans = await repo2.GetFanProfilesAsync();
        Assert.NotEmpty(fans);
    }

    [Fact]
    public async Task GameProfileZeroRpmOverride_DisablesZeroRpmWhileGameRuns_AndRestoresGlobalOnExit()
    {
        using var mock = new MockGpuHardwareAdapter();
        using var repo = new RadeonGuardRepository(_dbPath);
        var backups = new BackupManager(_dbPath, _backupsDir);
        var breadcrumbs = new CrashBreadcrumbManager(_breadcrumbPath);
        var log = new LoggerConfiguration().CreateLogger();

        var running = new List<RunningProcessSnapshot>();
        int? fgPid = null;

        using var orch = new ProfileOrchestrator(mock, repo, backups, breadcrumbs, log, () => running, () => fgPid, TimeSpan.Zero);
        await orch.StartAsync();

        var (globalFan, globalTuning) = await repo.EnsureDefaultProfilesAsync(
            "PCI\\VEN_1002&DEV_73BF&SUBSYS_439E1DA2&REV_C1");

        // Ensure Global Fan has Zero RPM ON
        var globalZeroOn = await repo.SaveFanProfileAsync(globalFan with { ZeroRpmEnabled = true });
        await orch.ApplyUserTuningProfileAsync(globalTuning.TuningProfileId, setAsGlobalDefault: true);

        Assert.True(orch.BuildStatusSnapshot().LiveFanState!.ZeroRpmEnabled);

        // Create a game profile for a light game with Zero RPM ForceDisabled
        var lightGame = await repo.SaveGameProfileAsync(new GameProfile
        {
            Title = "Light Indie Game",
            TuningProfileId = globalTuning.TuningProfileId,
            ZeroRpmOverride = ZeroRpmOverrideMode.ForceDisabled,
            IsEnabled = true,
            LockWhileProcessRunning = true,
            Executables =
            [
                new GameExecutable
                {
                    ExeFullPath = @"C:\Games\LightIndie\indie.exe",
                    ExeFileName = "indie.exe",
                    Role = ExecutableRole.PrimaryRenderer
                }
            ]
        });

        // 1. Game launches -> Zero RPM must turn OFF so fans keep spinning smoothly
        running.Add(new RunningProcessSnapshot(4242, "indie.exe", @"C:\Games\LightIndie\indie.exe"));
        fgPid = 4242;
        await orch.EvaluateGameProfilesAsync();

        var activeSnap = orch.BuildStatusSnapshot();
        Assert.Equal("Light Indie Game", activeSnap.ActiveGameTitle);
        Assert.False(activeSnap.LiveFanState!.ZeroRpmEnabled);

        // 2. While game is still running, user flips Zero RPM override to ForceEnabled & refreshes
        await repo.SaveGameProfileAsync(lightGame with { ZeroRpmOverride = ZeroRpmOverrideMode.ForceEnabled }, "LiveEdit");
        await orch.RefreshActiveGameAsync();
        Assert.True(orch.BuildStatusSnapshot().LiveFanState!.ZeroRpmEnabled);

        // Flip back to ForceDisabled, then simulate Adrenalin/external drift turning Zero RPM back ON
        await repo.SaveGameProfileAsync(lightGame with { ZeroRpmOverride = ZeroRpmOverrideMode.ForceDisabled }, "LiveEdit2");
        await orch.RefreshActiveGameAsync();
        Assert.False(orch.BuildStatusSnapshot().LiveFanState!.ZeroRpmEnabled);

        // 3. Game exits -> Global Fan profile's ZeroRpmEnabled = true is restored
        running.Clear();
        fgPid = null;
        await orch.EvaluateGameProfilesAsync();

        var exitSnap = orch.BuildStatusSnapshot();
        Assert.Null(exitSnap.ActiveGameTitle);
        Assert.True(exitSnap.LiveFanState!.ZeroRpmEnabled);
    }

    [Fact]
    public async Task ChosenFanProfile_SticksAcrossOrchestratorRestart_AndGlobalTuningApply()
    {
        var customFan = new FanProfile
        {
            GpuHardwareKey = "PCI\\VEN_1002&DEV_73BF&SUBSYS_439E1DA2&REV_C1",
            Name = "Sticky Custom Fan",
            ZeroRpmEnabled = false,
            Points =
            [
                new FanCurvePoint(30, 28),
                new FanCurvePoint(48, 42),
                new FanCurvePoint(64, 58),
                new FanCurvePoint(78, 76),
                new FanCurvePoint(90, 95)
            ]
        };

        using (var mock1 = new MockGpuHardwareAdapter())
        using (var repo1 = new RadeonGuardRepository(_dbPath))
        {
            var backups1 = new BackupManager(_dbPath, _backupsDir);
            var breadcrumbs1 = new CrashBreadcrumbManager(_breadcrumbPath);
            var log = new LoggerConfiguration().CreateLogger();
            using var orch1 = new ProfileOrchestrator(mock1, repo1, backups1, breadcrumbs1, log);
            await orch1.StartAsync();

            var (_, globalTuning) = await repo1.EnsureDefaultProfilesAsync("PCI\\VEN_1002&DEV_73BF&SUBSYS_439E1DA2&REV_C1");
            customFan = await repo1.SaveFanProfileAsync(customFan, "UserCreated");

            var fanRes = await orch1.ApplyUserFanProfileAsync(customFan.FanProfileId);
            Assert.True(fanRes.Success);

            // Applying Global Default tuning must NOT revert user's chosen global fan profile back to Default Fan Curve
            var tuningRes = await orch1.ApplyUserTuningProfileAsync(globalTuning.TuningProfileId, setAsGlobalDefault: true);
            Assert.True(tuningRes.Success);
            Assert.Equal(customFan.FanProfileId, orch1.BuildStatusSnapshot().ActiveFanProfileId);
        }

        // Simulate PC reboot: fresh MockGpuHardwareAdapter starts back at hardware defaults
        using (var mock2 = new MockGpuHardwareAdapter())
        using (var repo2 = new RadeonGuardRepository(_dbPath))
        {
            var backups2 = new BackupManager(_dbPath, _backupsDir);
            var breadcrumbs2 = new CrashBreadcrumbManager(_breadcrumbPath);
            var log = new LoggerConfiguration().CreateLogger();
            using var orch2 = new ProfileOrchestrator(mock2, repo2, backups2, breadcrumbs2, log);
            await orch2.StartAsync();

            var bootSnap = orch2.BuildStatusSnapshot();
            Assert.Equal(customFan.FanProfileId, bootSnap.ActiveFanProfileId);
            Assert.Equal("Sticky Custom Fan", bootSnap.ActiveFanProfileName);
            Assert.Equal("VERIFIED", bootSnap.HardwareSyncState);
            Assert.False(bootSnap.LiveFanState!.ZeroRpmEnabled);
            Assert.Equal(58, bootSnap.LiveFanState.Points[2].FanSpeedPercent);
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }
}
