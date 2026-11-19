using RadeonGuard.Core.Models;
using RadeonGuard.Persistence;
using Xunit;

namespace RadeonGuard.Tests;

public sealed class PersistenceAndRecoveryTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;
    private readonly string _backupsDir;

    public PersistenceAndRecoveryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "RadeonGuardTests_" + Guid.NewGuid().ToString("N"));
        _dbPath = Path.Combine(_tempDir, "radeonguard.db");
        _backupsDir = Path.Combine(_tempDir, "backups");
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public async Task SaveEditRollback_PreservesRevisionHistory()
    {
        using var repo = new RadeonGuardRepository(_dbPath);
        await repo.InitializeAsync(_backupsDir);

        var fanV1 = await repo.SaveFanProfileAsync(new FanProfile
        {
            GpuHardwareKey = "RX6800XT",
            Name = "Quiet Curve",
            ZeroRpmEnabled = true,
            Points =
            [
                new FanCurvePoint(30, 15),
                new FanCurvePoint(50, 25),
                new FanCurvePoint(65, 40),
                new FanCurvePoint(78, 60),
                new FanCurvePoint(90, 85)
            ]
        }, "InitialCreate");

        var fanV2 = await repo.SaveFanProfileAsync(fanV1 with
        {
            ZeroRpmEnabled = false,
            Points =
            [
                new FanCurvePoint(30, 30),
                new FanCurvePoint(50, 45),
                new FanCurvePoint(65, 65),
                new FanCurvePoint(78, 85),
                new FanCurvePoint(90, 100)
            ]
        }, "AggressiveEdit");

        Assert.Equal(2, fanV2.RevisionNumber);

        var revs = await repo.GetRevisionsAsync("FanProfile", fanV1.FanProfileId);
        var rev1 = revs.Single(r => r.RevisionNumber == 1);
        Assert.True(await repo.RollbackRevisionAsync(rev1.RevisionId));

        var restored = await repo.GetFanProfileByIdAsync(fanV1.FanProfileId);
        Assert.NotNull(restored);
        Assert.True(restored!.ZeroRpmEnabled);
        Assert.Equal(25, restored.Points[1].FanSpeedPercent);
    }

    [Fact]
    public async Task CorruptDatabase_AutomaticallyRecoversFromSha256Backup()
    {
        var backups = new BackupManager(_dbPath, _backupsDir);

        using (var repo = new RadeonGuardRepository(_dbPath))
        {
            await repo.InitializeAsync(_backupsDir);
            await repo.SaveFanProfileAsync(new FanProfile
            {
                GpuHardwareKey = "RX6800XT",
                Name = "SurvivesCorruption",
                Points =
                [
                    new FanCurvePoint(30, 15),
                    new FanCurvePoint(50, 30),
                    new FanCurvePoint(65, 50),
                    new FanCurvePoint(80, 75),
                    new FanCurvePoint(95, 100)
                ]
            });

            var bk = await backups.CreateBackupAsync("pre_corrupt");
            Assert.True(bk.ChecksumValid);
        }

        // Corrupt the primary database bytes
        await File.WriteAllBytesAsync(_dbPath, System.Text.Encoding.ASCII.GetBytes("CORRUPTED_GARBAGE_BYTES_00000000"));

        using (var recoveredRepo = new RadeonGuardRepository(_dbPath))
        {
            await recoveredRepo.InitializeAsync(_backupsDir);
            Assert.True(await recoveredRepo.CheckIntegrityAsync());
            var fans = await recoveredRepo.GetFanProfilesAsync();
            Assert.Contains(fans, f => f.Name == "SurvivesCorruption");
        }
    }

    [Fact]
    public async Task ExportImportBundle_VerifiesSha256Seal()
    {
        using var repo = new RadeonGuardRepository(_dbPath);
        await repo.InitializeAsync(_backupsDir);
        await repo.EnsureDefaultProfilesAsync("RX6800XT");

        var portability = new ProfilePortabilityService(repo);
        string bundlePath = Path.Combine(_tempDir, "bundle.json");
        await portability.ExportBundleAsync(bundlePath);

        string json = await File.ReadAllTextAsync(bundlePath);
        string tamperedPath = Path.Combine(_tempDir, "tampered.json");
        await File.WriteAllTextAsync(tamperedPath, json.Replace("Default Fan Curve", "Tampered Fan Curve"));

        await Assert.ThrowsAsync<InvalidDataException>(() => portability.ImportBundleAsync(tamperedPath));
    }

    [Fact]
    public async Task LegacyV1Database_MigratesZeroRpmOverrideColumnWithoutDataLoss()
    {
        // Create a v1 database without ZeroRpmOverride column
        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath}"))
        {
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE FAN_PROFILES (
                    FanProfileId TEXT PRIMARY KEY, GpuHardwareKey TEXT NOT NULL, Name TEXT NOT NULL,
                    IsDefault INTEGER NOT NULL DEFAULT 0, ZeroRpmEnabled INTEGER NOT NULL DEFAULT 1,
                    MinAcousticLimitMHz INTEGER NULL, RevisionNumber INTEGER NOT NULL DEFAULT 1,
                    UpdatedAtUtc TEXT NOT NULL
                );
                CREATE TABLE FAN_CURVE_POINTS (
                    FanProfileId TEXT NOT NULL, PointIndex INTEGER NOT NULL,
                    TemperatureC INTEGER NOT NULL, FanSpeedPercent INTEGER NOT NULL,
                    PRIMARY KEY (FanProfileId, PointIndex)
                );
                CREATE TABLE TUNING_PROFILES (
                    TuningProfileId TEXT PRIMARY KEY, GpuHardwareKey TEXT NOT NULL, Name TEXT NOT NULL,
                    IsGlobalDefault INTEGER NOT NULL DEFAULT 0, IsQuarantined INTEGER NOT NULL DEFAULT 0,
                    QuarantineReason TEXT NULL, EnableClockTuning INTEGER NOT NULL DEFAULT 0,
                    MinCoreClockMHz INTEGER NULL, MaxCoreClockMHz INTEGER NULL,
                    EnableVoltageTuning INTEGER NOT NULL DEFAULT 0, CoreVoltageMv INTEGER NULL,
                    EnableVramTuning INTEGER NOT NULL DEFAULT 0, MaxVramClockMHz INTEGER NULL,
                    VramTiming TEXT NOT NULL DEFAULT 'Default', EnablePowerTuning INTEGER NOT NULL DEFAULT 0,
                    PowerLimitDeltaPercent INTEGER NOT NULL DEFAULT 0, LinkedFanProfileId TEXT NOT NULL,
                    RevisionNumber INTEGER NOT NULL DEFAULT 1, UpdatedAtUtc TEXT NOT NULL
                );
                CREATE TABLE THREE_D_SETTINGS (
                    TuningProfileId TEXT PRIMARY KEY, AntiLagEnabled INTEGER NOT NULL DEFAULT 0,
                    ChillEnabled INTEGER NOT NULL DEFAULT 0, ChillMinFps INTEGER NOT NULL DEFAULT 60,
                    ChillMaxFps INTEGER NOT NULL DEFAULT 144, ImageSharpeningEnabled INTEGER NOT NULL DEFAULT 0,
                    ImageSharpeningPercent INTEGER NOT NULL DEFAULT 80, EnhancedSyncEnabled INTEGER NOT NULL DEFAULT 0,
                    FrtcEnabled INTEGER NOT NULL DEFAULT 0, FrtcMaxFps INTEGER NOT NULL DEFAULT 144,
                    VSync TEXT NOT NULL DEFAULT 'OffUnlessAppSpecifies'
                );
                CREATE TABLE GAME_PROFILES (
                    GameProfileId TEXT PRIMARY KEY, Title TEXT NOT NULL, LauncherSource TEXT NOT NULL DEFAULT 'Manual',
                    LauncherAppId TEXT NOT NULL DEFAULT '', TuningProfileId TEXT NOT NULL,
                    OverrideFanProfileId TEXT NULL, IsEnabled INTEGER NOT NULL DEFAULT 1,
                    LockWhileProcessRunning INTEGER NOT NULL DEFAULT 1, LastLaunchedUtc TEXT NULL
                );
                INSERT INTO FAN_PROFILES VALUES ('fan-1', 'RX6800XT', 'Fan1', 1, 1, NULL, 1, '2026-01-01T00:00:00Z');
                INSERT INTO TUNING_PROFILES (TuningProfileId, GpuHardwareKey, Name, IsGlobalDefault, LinkedFanProfileId, UpdatedAtUtc)
                    VALUES ('tun-1', 'RX6800XT', 'Global', 1, 'fan-1', '2026-01-01T00:00:00Z');
                INSERT INTO THREE_D_SETTINGS (TuningProfileId) VALUES ('tun-1');
                INSERT INTO GAME_PROFILES (GameProfileId, Title, TuningProfileId) VALUES ('game-1', 'Legacy Game', 'tun-1');
                PRAGMA user_version = 1;
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        using var repo = new RadeonGuardRepository(_dbPath);
        await repo.InitializeAsync(_backupsDir);

        var games = await repo.GetGameProfilesAsync();
        var legacy = Assert.Single(games);
        Assert.Equal("Legacy Game", legacy.Title);
        Assert.Equal(ZeroRpmOverrideMode.InheritFromFanProfile, legacy.ZeroRpmOverride);

        var updated = legacy with { ZeroRpmOverride = ZeroRpmOverrideMode.ForceDisabled };
        await repo.SaveGameProfileAsync(updated, "DisableZeroRpm");

        var reloaded = Assert.Single(await repo.GetGameProfilesAsync());
        Assert.Equal(ZeroRpmOverrideMode.ForceDisabled, reloaded.ZeroRpmOverride);
    }

    [Fact]
    public async Task ExportImportFanProfiles_ValidatesCurveAndVerifiesSha256()
    {
        using var repo = new RadeonGuardRepository(_dbPath);
        await repo.InitializeAsync(_backupsDir);

        var custom = await repo.SaveFanProfileAsync(new FanProfile
        {
            GpuHardwareKey = "RX6800XT",
            Name = "Quiet ZeroOff",
            ZeroRpmEnabled = false,
            Points =
            [
                new FanCurvePoint(30, 22),
                new FanCurvePoint(48, 35),
                new FanCurvePoint(64, 52),
                new FanCurvePoint(78, 72),
                new FanCurvePoint(90, 92)
            ]
        });

        var portability = new ProfilePortabilityService(repo);
        string exportPath = Path.Combine(_tempDir, "quiet_zerooff.json");
        await portability.ExportFanProfilesAsync([custom], exportPath);

        string tamperedPath = Path.Combine(_tempDir, "tampered_fan.json");
        string json = await File.ReadAllTextAsync(exportPath);
        await File.WriteAllTextAsync(tamperedPath, json.Replace("Quiet ZeroOff", "Tampered"));
        await Assert.ThrowsAsync<InvalidDataException>(() => portability.ImportFanProfilesAsync(tamperedPath));

        using var repo2 = new RadeonGuardRepository(Path.Combine(_tempDir, "second.db"));
        await repo2.InitializeAsync(Path.Combine(_tempDir, "backups2"));
        var portability2 = new ProfilePortabilityService(repo2);
        var imported = await portability2.ImportFanProfilesAsync(exportPath, targetGpuHardwareKey: "RX6800XT_NEW");

        var importedFan = Assert.Single(imported);
        Assert.Equal("Quiet ZeroOff", importedFan.Name);
        Assert.False(importedFan.ZeroRpmEnabled);
        Assert.Equal(52, importedFan.Points[2].FanSpeedPercent);
        Assert.Equal("RX6800XT_NEW", importedFan.GpuHardwareKey);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }
}
