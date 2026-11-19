using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using RadeonGuard.Core.Models;

namespace RadeonGuard.Persistence;

public sealed class RadeonGuardRepository : IDisposable
{
    public const string DefaultFanProfileId = "{00000000-0000-0000-0000-000000000001}";

    private readonly string _dbPath;
    private readonly string _connectionString;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public string DatabasePath => _dbPath;

    public RadeonGuardRepository(string? dbPath = null)
    {
        _dbPath = dbPath ?? StoragePaths.GetDatabasePath();
        var dir = Path.GetDirectoryName(_dbPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        }.ToString();
    }

    private SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
        cmd.ExecuteNonQuery();
        return conn;
    }

    public async Task InitializeAsync(string? backupsDir = null, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            bool dbExisted = File.Exists(_dbPath) && new FileInfo(_dbPath).Length > 0;
            if (dbExisted)
            {
                bool healthy = CheckIntegrityInternal();
                if (!healthy && backupsDir is not null)
                {
                    var backupMgr = new BackupManager(_dbPath, backupsDir);
                    await backupMgr.RestoreLatestValidBackupAsync(ct).ConfigureAwait(false);
                }
            }

            using var conn = OpenConnection();
            DatabaseSchema.InitializeDatabase(conn);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private bool CheckIntegrityInternal()
    {
        try
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA integrity_check(1);";
            var result = cmd.ExecuteScalar()?.ToString();
            return string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> CheckIntegrityAsync(CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return CheckIntegrityInternal();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task UpsertGpuDeviceAsync(GpuDeviceInfo gpu, GpuTuningCapabilities? caps, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO GPU_DEVICES (PnpDeviceId, HardwareKey, UniqueId, MarketingName, LastSeenDriverVer, HardwareLimitsJson, UpdatedAtUtc)
                VALUES ($pnp, $hw, $uid, $name, $drv, $caps, $ts)
                ON CONFLICT(PnpDeviceId) DO UPDATE SET
                    HardwareKey = excluded.HardwareKey,
                    UniqueId = excluded.UniqueId,
                    MarketingName = excluded.MarketingName,
                    LastSeenDriverVer = excluded.LastSeenDriverVer,
                    HardwareLimitsJson = excluded.HardwareLimitsJson,
                    UpdatedAtUtc = excluded.UpdatedAtUtc;
                """;
            cmd.Parameters.AddWithValue("$pnp", gpu.PnpString);
            cmd.Parameters.AddWithValue("$hw", gpu.HardwareKey);
            cmd.Parameters.AddWithValue("$uid", gpu.UniqueId);
            cmd.Parameters.AddWithValue("$name", gpu.Name);
            cmd.Parameters.AddWithValue("$drv", gpu.DriverVersion);
            cmd.Parameters.AddWithValue("$caps", caps is null ? "{}" : JsonSerializer.Serialize(caps));
            cmd.Parameters.AddWithValue("$ts", DateTimeOffset.UtcNow.ToString("O"));
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<(FanProfile DefaultFan, TuningProfile DefaultTuning)> EnsureDefaultProfilesAsync(
        string gpuHardwareKey,
        FanTuningHardwareSnapshot? hwFanDefaults = null,
        ManualTuningSnapshot? hwManualDefaults = null,
        CancellationToken ct = default)
    {
        var fans = await GetFanProfilesAsync(ct).ConfigureAwait(false);
        var defaultFan = fans.FirstOrDefault(f => f.IsDefault);
        if (defaultFan is null)
        {
            var pts = hwFanDefaults?.Points is { Count: > 0 }
                ? hwFanDefaults.Points
                : new[]
                {
                    new FanCurvePoint(30, 15),
                    new FanCurvePoint(50, 30),
                    new FanCurvePoint(65, 50),
                    new FanCurvePoint(80, 75),
                    new FanCurvePoint(95, 100)
                };
            defaultFan = new FanProfile
            {
                FanProfileId = DefaultFanProfileId,
                GpuHardwareKey = gpuHardwareKey,
                Name = "Default Fan Curve",
                IsDefault = true,
                ZeroRpmEnabled = hwFanDefaults?.ZeroRpmEnabled ?? true,
                MinAcousticLimitMHz = hwFanDefaults?.MinAcousticSupported == true ? hwFanDefaults.MinAcousticLimitMHz : null,
                Points = pts
            };
            defaultFan = await SaveFanProfileAsync(defaultFan, "InitialSeed", ct).ConfigureAwait(false);
        }

        var tunings = await GetTuningProfilesAsync(ct).ConfigureAwait(false);
        var defaultTuning = tunings.FirstOrDefault(t => t.IsGlobalDefault);
        if (defaultTuning is null)
        {
            defaultTuning = new TuningProfile
            {
                TuningProfileId = "{00000000-0000-0000-0000-000000000002}",
                GpuHardwareKey = gpuHardwareKey,
                Name = "Global Default",
                IsGlobalDefault = true,
                EnableClockTuning = false,
                MinCoreClockMHz = hwManualDefaults?.GfxValid == true ? hwManualDefaults.GfxMinFreqMHz : 500,
                MaxCoreClockMHz = hwManualDefaults?.GfxValid == true ? hwManualDefaults.GfxMaxFreqMHz : 2400,
                EnableVoltageTuning = false,
                CoreVoltageMv = hwManualDefaults?.GfxValid == true ? hwManualDefaults.GfxVoltageMv : 1150,
                EnableVramTuning = false,
                MaxVramClockMHz = hwManualDefaults?.VramValid == true ? hwManualDefaults.VramMaxFreqMHz : 2000,
                VramTiming = hwManualDefaults?.VramMemoryTiming ?? VramTimingMode.Default,
                EnablePowerTuning = false,
                PowerLimitDeltaPercent = hwManualDefaults?.PowerValid == true ? hwManualDefaults.PowerLimitPercent : 0,
                LinkedFanProfileId = defaultFan.FanProfileId,
                ThreeDSettings = new ThreeDSettings()
            };
            defaultTuning = await SaveTuningProfileAsync(defaultTuning, "InitialSeed", ct).ConfigureAwait(false);
        }

        return (defaultFan, defaultTuning);
    }

    public async Task<IReadOnlyList<FanProfile>> GetFanProfilesAsync(CancellationToken ct = default)
    {
        using var conn = OpenConnection();
        var list = new List<FanProfile>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT FanProfileId, GpuHardwareKey, Name, IsDefault, ZeroRpmEnabled, MinAcousticLimitMHz, CurvePayloadSha256, RevisionNumber, UpdatedAtUtc
            FROM FAN_PROFILES
            ORDER BY IsDefault DESC, Name COLLATE NOCASE;
            """;
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            string id = reader.GetString(0);
            list.Add(new FanProfile
            {
                FanProfileId = id,
                GpuHardwareKey = reader.GetString(1),
                Name = reader.GetString(2),
                IsDefault = reader.GetInt32(3) != 0,
                ZeroRpmEnabled = reader.GetInt32(4) != 0,
                MinAcousticLimitMHz = reader.IsDBNull(5) ? null : reader.GetInt32(5),
                CurvePayloadSha256 = reader.GetString(6),
                RevisionNumber = reader.GetInt32(7),
                UpdatedAtUtc = DateTimeOffset.Parse(reader.GetString(8)),
                Points = await GetFanPointsInternalAsync(conn, id, ct).ConfigureAwait(false)
            });
        }
        return list;
    }

    public async Task<FanProfile?> GetFanProfileByIdAsync(string fanProfileId, CancellationToken ct = default)
    {
        var all = await GetFanProfilesAsync(ct).ConfigureAwait(false);
        return all.FirstOrDefault(f => string.Equals(f.FanProfileId, fanProfileId, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<IReadOnlyList<FanCurvePoint>> GetFanPointsInternalAsync(SqliteConnection conn, string fanProfileId, CancellationToken ct)
    {
        var pts = new List<FanCurvePoint>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT TemperatureC, FanSpeedPwmPercent
            FROM FAN_CURVE_POINTS
            WHERE FanProfileId = $id
            ORDER BY PointIndex ASC;
            """;
        cmd.Parameters.AddWithValue("$id", fanProfileId);
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            pts.Add(new FanCurvePoint(reader.GetInt32(0), reader.GetInt32(1)));
        }
        return pts;
    }

    public async Task<FanProfile> SaveFanProfileAsync(FanProfile profile, string changeReason = "UserEdit", CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var conn = OpenConnection();
            using var tx = conn.BeginTransaction();

            int nextRev = await GetNextRevisionNumberAsync(conn, tx, "FanProfile", profile.FanProfileId, ct).ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            string payloadJson = JsonSerializer.Serialize(new { profile.ZeroRpmEnabled, profile.MinAcousticLimitMHz, profile.Points });
            string hash = ComputeSha256(payloadJson);

            var saved = profile with
            {
                CurvePayloadSha256 = hash,
                RevisionNumber = nextRev,
                UpdatedAtUtc = now
            };

            if (saved.IsDefault)
            {
                using var clearDef = conn.CreateCommand();
                clearDef.Transaction = tx;
                clearDef.CommandText = "UPDATE FAN_PROFILES SET IsDefault = 0 WHERE FanProfileId <> $id;";
                clearDef.Parameters.AddWithValue("$id", saved.FanProfileId);
                await clearDef.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO FAN_PROFILES (FanProfileId, GpuHardwareKey, Name, IsDefault, ZeroRpmEnabled, MinAcousticLimitMHz, CurvePayloadSha256, RevisionNumber, UpdatedAtUtc)
                    VALUES ($id, $hw, $name, $def, $zrpm, $ac, $sha, $rev, $ts)
                    ON CONFLICT(FanProfileId) DO UPDATE SET
                        GpuHardwareKey = excluded.GpuHardwareKey,
                        Name = excluded.Name,
                        IsDefault = excluded.IsDefault,
                        ZeroRpmEnabled = excluded.ZeroRpmEnabled,
                        MinAcousticLimitMHz = excluded.MinAcousticLimitMHz,
                        CurvePayloadSha256 = excluded.CurvePayloadSha256,
                        RevisionNumber = excluded.RevisionNumber,
                        UpdatedAtUtc = excluded.UpdatedAtUtc;
                    """;
                cmd.Parameters.AddWithValue("$id", saved.FanProfileId);
                cmd.Parameters.AddWithValue("$hw", saved.GpuHardwareKey);
                cmd.Parameters.AddWithValue("$name", saved.Name);
                cmd.Parameters.AddWithValue("$def", saved.IsDefault ? 1 : 0);
                cmd.Parameters.AddWithValue("$zrpm", saved.ZeroRpmEnabled ? 1 : 0);
                cmd.Parameters.AddWithValue("$ac", saved.MinAcousticLimitMHz.HasValue ? saved.MinAcousticLimitMHz.Value : DBNull.Value);
                cmd.Parameters.AddWithValue("$sha", saved.CurvePayloadSha256);
                cmd.Parameters.AddWithValue("$rev", saved.RevisionNumber);
                cmd.Parameters.AddWithValue("$ts", saved.UpdatedAtUtc.ToString("O"));
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            using (var delPts = conn.CreateCommand())
            {
                delPts.Transaction = tx;
                delPts.CommandText = "DELETE FROM FAN_CURVE_POINTS WHERE FanProfileId = $id;";
                delPts.Parameters.AddWithValue("$id", saved.FanProfileId);
                await delPts.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            for (int i = 0; i < saved.Points.Count; i++)
            {
                using var insPt = conn.CreateCommand();
                insPt.Transaction = tx;
                insPt.CommandText = """
                    INSERT INTO FAN_CURVE_POINTS (FanProfileId, PointIndex, TemperatureC, FanSpeedPwmPercent)
                    VALUES ($id, $idx, $temp, $pwm);
                    """;
                insPt.Parameters.AddWithValue("$id", saved.FanProfileId);
                insPt.Parameters.AddWithValue("$idx", i);
                insPt.Parameters.AddWithValue("$temp", saved.Points[i].TemperatureC);
                insPt.Parameters.AddWithValue("$pwm", saved.Points[i].FanSpeedPercent);
                await insPt.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await InsertRevisionInternalAsync(conn, tx, "FanProfile", saved.FanProfileId, nextRev, JsonSerializer.Serialize(saved), changeReason, now, ct).ConfigureAwait(false);
            tx.Commit();
            return saved;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task DeleteFanProfileAsync(string fanProfileId, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var conn = OpenConnection();
            using var tx = conn.BeginTransaction();

            using var chk = conn.CreateCommand();
            chk.Transaction = tx;
            chk.CommandText = "SELECT IsDefault FROM FAN_PROFILES WHERE FanProfileId = $id;";
            chk.Parameters.AddWithValue("$id", fanProfileId);
            var isDef = Convert.ToInt32(await chk.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0);
            if (isDef != 0)
            {
                throw new InvalidOperationException("Cannot delete the default fan profile.");
            }

            using var fallbackCmd = conn.CreateCommand();
            fallbackCmd.Transaction = tx;
            fallbackCmd.CommandText = "SELECT FanProfileId FROM FAN_PROFILES WHERE IsDefault = 1 LIMIT 1;";
            var fallbackId = (await fallbackCmd.ExecuteScalarAsync(ct).ConfigureAwait(false))?.ToString();
            if (!string.IsNullOrEmpty(fallbackId))
            {
                using var updT = conn.CreateCommand();
                updT.Transaction = tx;
                updT.CommandText = "UPDATE TUNING_PROFILES SET LinkedFanProfileId = $fb WHERE LinkedFanProfileId = $id;";
                updT.Parameters.AddWithValue("$fb", fallbackId);
                updT.Parameters.AddWithValue("$id", fanProfileId);
                await updT.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

                using var updG = conn.CreateCommand();
                updG.Transaction = tx;
                updG.CommandText = "UPDATE GAME_PROFILES SET OverrideFanProfileId = NULL WHERE OverrideFanProfileId = $id;";
                updG.Parameters.AddWithValue("$id", fanProfileId);
                await updG.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            using var del = conn.CreateCommand();
            del.Transaction = tx;
            del.CommandText = "DELETE FROM FAN_PROFILES WHERE FanProfileId = $id;";
            del.Parameters.AddWithValue("$id", fanProfileId);
            await del.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            tx.Commit();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<IReadOnlyList<TuningProfile>> GetTuningProfilesAsync(CancellationToken ct = default)
    {
        using var conn = OpenConnection();
        var list = new List<TuningProfile>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT TuningProfileId, GpuHardwareKey, Name, IsGlobalDefault, IsQuarantined, QuarantineReason,
                   EnableClockTuning, MinCoreClockMHz, MaxCoreClockMHz, EnableVoltageTuning, CoreVoltageMv,
                   EnableVramTuning, MaxVramClockMHz, VramTimingMode, EnablePowerTuning, PowerLimitDeltaPercent,
                   LinkedFanProfileId, ThreeDSettingsJson, PayloadSha256, RevisionNumber, UpdatedAtUtc
            FROM TUNING_PROFILES
            ORDER BY IsGlobalDefault DESC, Name COLLATE NOCASE;
            """;
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            string threeDJson = reader.GetString(17);
            var threeD = JsonSerializer.Deserialize<ThreeDSettings>(threeDJson) ?? new ThreeDSettings();
            list.Add(new TuningProfile
            {
                TuningProfileId = reader.GetString(0),
                GpuHardwareKey = reader.GetString(1),
                Name = reader.GetString(2),
                IsGlobalDefault = reader.GetInt32(3) != 0,
                IsQuarantined = reader.GetInt32(4) != 0,
                QuarantineReason = reader.IsDBNull(5) ? null : reader.GetString(5),
                EnableClockTuning = reader.GetInt32(6) != 0,
                MinCoreClockMHz = reader.GetInt32(7),
                MaxCoreClockMHz = reader.GetInt32(8),
                EnableVoltageTuning = reader.GetInt32(9) != 0,
                CoreVoltageMv = reader.GetInt32(10),
                EnableVramTuning = reader.GetInt32(11) != 0,
                MaxVramClockMHz = reader.GetInt32(12),
                VramTiming = (VramTimingMode)reader.GetInt32(13),
                EnablePowerTuning = reader.GetInt32(14) != 0,
                PowerLimitDeltaPercent = reader.GetInt32(15),
                LinkedFanProfileId = reader.GetString(16),
                ThreeDSettings = threeD,
                PayloadSha256 = reader.GetString(18),
                RevisionNumber = reader.GetInt32(19),
                UpdatedAtUtc = DateTimeOffset.Parse(reader.GetString(20))
            });
        }
        return list;
    }

    public async Task<TuningProfile?> GetTuningProfileByIdAsync(string tuningProfileId, CancellationToken ct = default)
    {
        var all = await GetTuningProfilesAsync(ct).ConfigureAwait(false);
        return all.FirstOrDefault(t => string.Equals(t.TuningProfileId, tuningProfileId, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<TuningProfile> SaveTuningProfileAsync(TuningProfile profile, string changeReason = "UserEdit", CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var conn = OpenConnection();
            using var tx = conn.BeginTransaction();

            int nextRev = await GetNextRevisionNumberAsync(conn, tx, "TuningProfile", profile.TuningProfileId, ct).ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            string threeDJson = JsonSerializer.Serialize(profile.ThreeDSettings);
            string hash = ComputeSha256(JsonSerializer.Serialize(new
            {
                profile.EnableClockTuning,
                profile.MinCoreClockMHz,
                profile.MaxCoreClockMHz,
                profile.EnableVoltageTuning,
                profile.CoreVoltageMv,
                profile.EnableVramTuning,
                profile.MaxVramClockMHz,
                profile.VramTiming,
                profile.EnablePowerTuning,
                profile.PowerLimitDeltaPercent,
                profile.LinkedFanProfileId,
                threeDJson
            }));

            var saved = profile with
            {
                PayloadSha256 = hash,
                RevisionNumber = nextRev,
                UpdatedAtUtc = now
            };

            if (saved.IsGlobalDefault)
            {
                using var clearDef = conn.CreateCommand();
                clearDef.Transaction = tx;
                clearDef.CommandText = "UPDATE TUNING_PROFILES SET IsGlobalDefault = 0 WHERE TuningProfileId <> $id;";
                clearDef.Parameters.AddWithValue("$id", saved.TuningProfileId);
                await clearDef.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO TUNING_PROFILES (
                    TuningProfileId, GpuHardwareKey, Name, IsGlobalDefault, IsQuarantined, QuarantineReason,
                    EnableClockTuning, MinCoreClockMHz, MaxCoreClockMHz, EnableVoltageTuning, CoreVoltageMv,
                    EnableVramTuning, MaxVramClockMHz, VramTimingMode, EnablePowerTuning, PowerLimitDeltaPercent,
                    LinkedFanProfileId, ThreeDSettingsJson, PayloadSha256, RevisionNumber, UpdatedAtUtc)
                VALUES (
                    $id, $hw, $name, $def, $quar, $qreason,
                    $enClk, $minClk, $maxClk, $enVolt, $volt,
                    $enVram, $maxVram, $vramTim, $enPwr, $pwrLim,
                    $fanId, $threeD, $sha, $rev, $ts)
                ON CONFLICT(TuningProfileId) DO UPDATE SET
                    GpuHardwareKey = excluded.GpuHardwareKey,
                    Name = excluded.Name,
                    IsGlobalDefault = excluded.IsGlobalDefault,
                    IsQuarantined = excluded.IsQuarantined,
                    QuarantineReason = excluded.QuarantineReason,
                    EnableClockTuning = excluded.EnableClockTuning,
                    MinCoreClockMHz = excluded.MinCoreClockMHz,
                    MaxCoreClockMHz = excluded.MaxCoreClockMHz,
                    EnableVoltageTuning = excluded.EnableVoltageTuning,
                    CoreVoltageMv = excluded.CoreVoltageMv,
                    EnableVramTuning = excluded.EnableVramTuning,
                    MaxVramClockMHz = excluded.MaxVramClockMHz,
                    VramTimingMode = excluded.VramTimingMode,
                    EnablePowerTuning = excluded.EnablePowerTuning,
                    PowerLimitDeltaPercent = excluded.PowerLimitDeltaPercent,
                    LinkedFanProfileId = excluded.LinkedFanProfileId,
                    ThreeDSettingsJson = excluded.ThreeDSettingsJson,
                    PayloadSha256 = excluded.PayloadSha256,
                    RevisionNumber = excluded.RevisionNumber,
                    UpdatedAtUtc = excluded.UpdatedAtUtc;
                """;
            cmd.Parameters.AddWithValue("$id", saved.TuningProfileId);
            cmd.Parameters.AddWithValue("$hw", saved.GpuHardwareKey);
            cmd.Parameters.AddWithValue("$name", saved.Name);
            cmd.Parameters.AddWithValue("$def", saved.IsGlobalDefault ? 1 : 0);
            cmd.Parameters.AddWithValue("$quar", saved.IsQuarantined ? 1 : 0);
            cmd.Parameters.AddWithValue("$qreason", (object?)saved.QuarantineReason ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$enClk", saved.EnableClockTuning ? 1 : 0);
            cmd.Parameters.AddWithValue("$minClk", saved.MinCoreClockMHz);
            cmd.Parameters.AddWithValue("$maxClk", saved.MaxCoreClockMHz);
            cmd.Parameters.AddWithValue("$enVolt", saved.EnableVoltageTuning ? 1 : 0);
            cmd.Parameters.AddWithValue("$volt", saved.CoreVoltageMv);
            cmd.Parameters.AddWithValue("$enVram", saved.EnableVramTuning ? 1 : 0);
            cmd.Parameters.AddWithValue("$maxVram", saved.MaxVramClockMHz);
            cmd.Parameters.AddWithValue("$vramTim", (int)saved.VramTiming);
            cmd.Parameters.AddWithValue("$enPwr", saved.EnablePowerTuning ? 1 : 0);
            cmd.Parameters.AddWithValue("$pwrLim", saved.PowerLimitDeltaPercent);
            cmd.Parameters.AddWithValue("$fanId", saved.LinkedFanProfileId);
            cmd.Parameters.AddWithValue("$threeD", threeDJson);
            cmd.Parameters.AddWithValue("$sha", saved.PayloadSha256);
            cmd.Parameters.AddWithValue("$rev", saved.RevisionNumber);
            cmd.Parameters.AddWithValue("$ts", saved.UpdatedAtUtc.ToString("O"));
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            await InsertRevisionInternalAsync(conn, tx, "TuningProfile", saved.TuningProfileId, nextRev, JsonSerializer.Serialize(saved), changeReason, now, ct).ConfigureAwait(false);
            tx.Commit();
            return saved;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task DeleteTuningProfileAsync(string tuningProfileId, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var conn = OpenConnection();
            using var tx = conn.BeginTransaction();

            using var chk = conn.CreateCommand();
            chk.Transaction = tx;
            chk.CommandText = "SELECT IsGlobalDefault FROM TUNING_PROFILES WHERE TuningProfileId = $id;";
            chk.Parameters.AddWithValue("$id", tuningProfileId);
            var isDef = Convert.ToInt32(await chk.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0);
            if (isDef != 0)
            {
                throw new InvalidOperationException("Cannot delete the Global Default tuning profile.");
            }

            using var fbCmd = conn.CreateCommand();
            fbCmd.Transaction = tx;
            fbCmd.CommandText = "SELECT TuningProfileId FROM TUNING_PROFILES WHERE IsGlobalDefault = 1 LIMIT 1;";
            var fbId = (await fbCmd.ExecuteScalarAsync(ct).ConfigureAwait(false))?.ToString();
            if (!string.IsNullOrEmpty(fbId))
            {
                using var updG = conn.CreateCommand();
                updG.Transaction = tx;
                updG.CommandText = "UPDATE GAME_PROFILES SET TuningProfileId = $fb WHERE TuningProfileId = $id;";
                updG.Parameters.AddWithValue("$fb", fbId);
                updG.Parameters.AddWithValue("$id", tuningProfileId);
                await updG.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            using var del = conn.CreateCommand();
            del.Transaction = tx;
            del.CommandText = "DELETE FROM TUNING_PROFILES WHERE TuningProfileId = $id;";
            del.Parameters.AddWithValue("$id", tuningProfileId);
            await del.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            tx.Commit();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<IReadOnlyList<GameProfile>> GetGameProfilesAsync(CancellationToken ct = default)
    {
        using var conn = OpenConnection();
        var list = new List<GameProfile>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT GameProfileId, Title, LauncherSource, LauncherAppId, TuningProfileId, OverrideFanProfileId, IsEnabled, LockWhileProcessRunning, ZeroRpmOverride, LastLaunchedUtc
            FROM GAME_PROFILES
            ORDER BY Title COLLATE NOCASE;
            """;
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            string id = reader.GetString(0);
            list.Add(new GameProfile
            {
                GameProfileId = id,
                Title = reader.GetString(1),
                LauncherSource = reader.GetString(2),
                LauncherAppId = reader.GetString(3),
                TuningProfileId = reader.GetString(4),
                OverrideFanProfileId = reader.IsDBNull(5) ? null : reader.GetString(5),
                IsEnabled = reader.GetInt32(6) != 0,
                LockWhileProcessRunning = reader.GetInt32(7) != 0,
                ZeroRpmOverride = (ZeroRpmOverrideMode)reader.GetInt32(8),
                LastLaunchedUtc = reader.IsDBNull(9) ? null : DateTimeOffset.Parse(reader.GetString(9)),
                Executables = await GetGameExecutablesInternalAsync(conn, id, ct).ConfigureAwait(false)
            });
        }
        return list;
    }

    private static async Task<IReadOnlyList<GameExecutable>> GetGameExecutablesInternalAsync(SqliteConnection conn, string gameProfileId, CancellationToken ct)
    {
        var exes = new List<GameExecutable>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Id, GameProfileId, ExeFullPath, ExeFileName, Role
            FROM GAME_EXECUTABLES
            WHERE GameProfileId = $id
            ORDER BY Id ASC;
            """;
        cmd.Parameters.AddWithValue("$id", gameProfileId);
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            exes.Add(new GameExecutable
            {
                Id = reader.GetInt64(0),
                GameProfileId = reader.GetString(1),
                ExeFullPath = reader.GetString(2),
                ExeFileName = reader.GetString(3),
                Role = Enum.Parse<ExecutableRole>(reader.GetString(4))
            });
        }
        return exes;
    }

    public async Task<GameProfile> SaveGameProfileAsync(GameProfile game, string changeReason = "UserEdit", CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var conn = OpenConnection();
            using var tx = conn.BeginTransaction();

            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO GAME_PROFILES (GameProfileId, Title, LauncherSource, LauncherAppId, TuningProfileId, OverrideFanProfileId, IsEnabled, LockWhileProcessRunning, ZeroRpmOverride, LastLaunchedUtc)
                    VALUES ($id, $title, $src, $appId, $tId, $fId, $en, $lock, $zrpm, $last)
                    ON CONFLICT(GameProfileId) DO UPDATE SET
                        Title = excluded.Title,
                        LauncherSource = excluded.LauncherSource,
                        LauncherAppId = excluded.LauncherAppId,
                        TuningProfileId = excluded.TuningProfileId,
                        OverrideFanProfileId = excluded.OverrideFanProfileId,
                        IsEnabled = excluded.IsEnabled,
                        LockWhileProcessRunning = excluded.LockWhileProcessRunning,
                        ZeroRpmOverride = excluded.ZeroRpmOverride,
                        LastLaunchedUtc = excluded.LastLaunchedUtc;
                    """;
                cmd.Parameters.AddWithValue("$id", game.GameProfileId);
                cmd.Parameters.AddWithValue("$title", game.Title);
                cmd.Parameters.AddWithValue("$src", game.LauncherSource);
                cmd.Parameters.AddWithValue("$appId", game.LauncherAppId);
                cmd.Parameters.AddWithValue("$tId", game.TuningProfileId);
                cmd.Parameters.AddWithValue("$fId", (object?)game.OverrideFanProfileId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$en", game.IsEnabled ? 1 : 0);
                cmd.Parameters.AddWithValue("$lock", game.LockWhileProcessRunning ? 1 : 0);
                cmd.Parameters.AddWithValue("$zrpm", (int)game.ZeroRpmOverride);
                cmd.Parameters.AddWithValue("$last", game.LastLaunchedUtc.HasValue ? game.LastLaunchedUtc.Value.ToString("O") : DBNull.Value);
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            using (var del = conn.CreateCommand())
            {
                del.Transaction = tx;
                del.CommandText = "DELETE FROM GAME_EXECUTABLES WHERE GameProfileId = $id;";
                del.Parameters.AddWithValue("$id", game.GameProfileId);
                await del.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            foreach (var exe in game.Executables)
            {
                string fileName = string.IsNullOrWhiteSpace(exe.ExeFileName)
                    ? Path.GetFileName(exe.ExeFullPath).ToLowerInvariant()
                    : exe.ExeFileName.ToLowerInvariant();
                using var ins = conn.CreateCommand();
                ins.Transaction = tx;
                ins.CommandText = """
                    INSERT INTO GAME_EXECUTABLES (GameProfileId, ExeFullPath, ExeFileName, Role)
                    VALUES ($gid, $path, $name, $role);
                    """;
                ins.Parameters.AddWithValue("$gid", game.GameProfileId);
                ins.Parameters.AddWithValue("$path", exe.ExeFullPath);
                ins.Parameters.AddWithValue("$name", fileName);
                ins.Parameters.AddWithValue("$role", exe.Role.ToString());
                await ins.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            int nextRev = await GetNextRevisionNumberAsync(conn, tx, "GameProfile", game.GameProfileId, ct).ConfigureAwait(false);
            await InsertRevisionInternalAsync(conn, tx, "GameProfile", game.GameProfileId, nextRev, JsonSerializer.Serialize(game), changeReason, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);

            tx.Commit();
            return game;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task DeleteGameProfileAsync(string gameProfileId, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM GAME_PROFILES WHERE GameProfileId = $id;";
            cmd.Parameters.AddWithValue("$id", gameProfileId);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<IReadOnlyList<DenylistEntry>> GetDenylistAsync(CancellationToken ct = default)
    {
        using var conn = OpenConnection();
        var list = new List<DenylistEntry>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT ExeFileName, Reason, IsBuiltIn FROM DENYLIST_EXECUTABLES ORDER BY ExeFileName;";
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new DenylistEntry(reader.GetString(0), reader.GetString(1), reader.GetInt32(2) != 0));
        }
        return list;
    }

    public async Task AddDenylistEntryAsync(string exeFileName, string reason, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO DENYLIST_EXECUTABLES (ExeFileName, Reason, IsBuiltIn)
                VALUES ($exe, $reason, 0)
                ON CONFLICT(ExeFileName) DO UPDATE SET Reason = excluded.Reason;
                """;
            cmd.Parameters.AddWithValue("$exe", exeFileName.Trim().ToLowerInvariant());
            cmd.Parameters.AddWithValue("$reason", reason);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task RemoveDenylistEntryAsync(string exeFileName, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM DENYLIST_EXECUTABLES WHERE ExeFileName = $exe AND IsBuiltIn = 0;";
            cmd.Parameters.AddWithValue("$exe", exeFileName.Trim().ToLowerInvariant());
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<IReadOnlyList<ProfileRevision>> GetRevisionsAsync(string? entityType = null, string? entityId = null, int limit = 100, CancellationToken ct = default)
    {
        using var conn = OpenConnection();
        var list = new List<ProfileRevision>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT RevisionId, EntityType, EntityId, RevisionNumber, SnapshotJson, ChangeReason, CreatedAtUtc
            FROM PROFILE_REVISIONS
            WHERE ($et IS NULL OR EntityType = $et) AND ($eid IS NULL OR EntityId = $eid)
            ORDER BY RevisionId DESC
            LIMIT $lim;
            """;
        cmd.Parameters.AddWithValue("$et", (object?)entityType ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$eid", (object?)entityId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$lim", limit);
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new ProfileRevision(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3),
                reader.GetString(4),
                reader.GetString(5),
                DateTimeOffset.Parse(reader.GetString(6))));
        }
        return list;
    }

    public async Task<bool> RollbackRevisionAsync(long revisionId, CancellationToken ct = default)
    {
        ProfileRevision? rev;
        using (var conn = OpenConnection())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT RevisionId, EntityType, EntityId, RevisionNumber, SnapshotJson, ChangeReason, CreatedAtUtc
                FROM PROFILE_REVISIONS WHERE RevisionId = $id;
                """;
            cmd.Parameters.AddWithValue("$id", revisionId);
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return false;
            rev = new ProfileRevision(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3),
                reader.GetString(4),
                reader.GetString(5),
                DateTimeOffset.Parse(reader.GetString(6)));
        }

        switch (rev.EntityType)
        {
            case "FanProfile":
                var fan = JsonSerializer.Deserialize<FanProfile>(rev.SnapshotJson);
                if (fan is null) return false;
                await SaveFanProfileAsync(fan, $"RollbackToRev{rev.RevisionNumber}", ct).ConfigureAwait(false);
                return true;
            case "TuningProfile":
                var tuning = JsonSerializer.Deserialize<TuningProfile>(rev.SnapshotJson);
                if (tuning is null) return false;
                await SaveTuningProfileAsync(tuning, $"RollbackToRev{rev.RevisionNumber}", ct).ConfigureAwait(false);
                return true;
            case "GameProfile":
                var game = JsonSerializer.Deserialize<GameProfile>(rev.SnapshotJson);
                if (game is null) return false;
                await SaveGameProfileAsync(game, $"RollbackToRev{rev.RevisionNumber}", ct).ConfigureAwait(false);
                return true;
            default:
                return false;
        }
    }

    public async Task LogAppliedStateAsync(AppliedStateRecord record, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO APPLIED_STATE_LOG (
                    GpuHardwareKey, TargetProfileId, TargetProfileName, TargetFanProfileId, TargetFanProfileName,
                    PersistStatus, ApplyStatus, DriverReadbackJson, ErrorCode, TriggerReason, TimestampUtc)
                VALUES ($hw, $pid, $pname, $fid, $fname, $pstat, $astat, $rb, $err, $trig, $ts);
                """;
            cmd.Parameters.AddWithValue("$hw", record.GpuHardwareKey);
            cmd.Parameters.AddWithValue("$pid", record.TargetProfileId);
            cmd.Parameters.AddWithValue("$pname", record.TargetProfileName);
            cmd.Parameters.AddWithValue("$fid", record.TargetFanProfileId);
            cmd.Parameters.AddWithValue("$fname", record.TargetFanProfileName);
            cmd.Parameters.AddWithValue("$pstat", record.PersistStatus);
            cmd.Parameters.AddWithValue("$astat", record.ApplyStatus);
            cmd.Parameters.AddWithValue("$rb", record.DriverReadbackJson);
            cmd.Parameters.AddWithValue("$err", (object?)record.ErrorCode ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$trig", record.TriggerReason);
            cmd.Parameters.AddWithValue("$ts", record.TimestampUtc.ToString("O"));
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<IReadOnlyList<AppliedStateRecord>> GetAppliedStateLogAsync(int limit = 100, CancellationToken ct = default)
    {
        using var conn = OpenConnection();
        var list = new List<AppliedStateRecord>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT LogId, GpuHardwareKey, TargetProfileId, TargetProfileName, TargetFanProfileId, TargetFanProfileName,
                   PersistStatus, ApplyStatus, DriverReadbackJson, ErrorCode, TriggerReason, TimestampUtc
            FROM APPLIED_STATE_LOG
            ORDER BY LogId DESC
            LIMIT $lim;
            """;
        cmd.Parameters.AddWithValue("$lim", limit);
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new AppliedStateRecord(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.GetString(10),
                DateTimeOffset.Parse(reader.GetString(11))));
        }
        return list;
    }

    public async Task<string?> GetSettingAsync(string key, CancellationToken ct = default)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT SettingValue FROM APP_SETTINGS WHERE SettingKey = $k;";
        cmd.Parameters.AddWithValue("$k", key);
        return (await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))?.ToString();
    }

    public async Task SetSettingAsync(string key, string value, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO APP_SETTINGS (SettingKey, SettingValue, UpdatedAtUtc)
                VALUES ($k, $v, $ts)
                ON CONFLICT(SettingKey) DO UPDATE SET SettingValue = excluded.SettingValue, UpdatedAtUtc = excluded.UpdatedAtUtc;
                """;
            cmd.Parameters.AddWithValue("$k", key);
            cmd.Parameters.AddWithValue("$v", value);
            cmd.Parameters.AddWithValue("$ts", DateTimeOffset.UtcNow.ToString("O"));
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static async Task<int> GetNextRevisionNumberAsync(SqliteConnection conn, SqliteTransaction tx, string entityType, string entityId, CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT COALESCE(MAX(RevisionNumber), 0) + 1 FROM PROFILE_REVISIONS WHERE EntityType = $et AND EntityId = $eid;";
        cmd.Parameters.AddWithValue("$et", entityType);
        cmd.Parameters.AddWithValue("$eid", entityId);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
    }

    private static async Task InsertRevisionInternalAsync(
        SqliteConnection conn, SqliteTransaction tx, string entityType, string entityId,
        int revNum, string snapshotJson, string reason, DateTimeOffset ts, CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO PROFILE_REVISIONS (EntityType, EntityId, RevisionNumber, SnapshotJson, ChangeReason, CreatedAtUtc)
            VALUES ($et, $eid, $rev, $json, $reason, $ts);
            """;
        cmd.Parameters.AddWithValue("$et", entityType);
        cmd.Parameters.AddWithValue("$eid", entityId);
        cmd.Parameters.AddWithValue("$rev", revNum);
        cmd.Parameters.AddWithValue("$json", snapshotJson);
        cmd.Parameters.AddWithValue("$reason", reason);
        cmd.Parameters.AddWithValue("$ts", ts.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public static string ComputeSha256(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _writeLock.Dispose();
    }
}
