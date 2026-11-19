using Microsoft.Data.Sqlite;

namespace RadeonGuard.Persistence;

public static class DatabaseSchema
{
    public const int CurrentSchemaVersion = 2;

    public static readonly (string ExeName, string Reason)[] BuiltInDenylist =
    [
        ("python.exe", "Python runtime / AI script false positive in Adrenalin"),
        ("pythonw.exe", "Python GUI runtime"),
        ("zoom.exe", "Video conferencing app"),
        ("excel.exe", "Microsoft Office Excel"),
        ("winword.exe", "Microsoft Office Word"),
        ("powerpnt.exe", "Microsoft Office PowerPoint"),
        ("outlook.exe", "Microsoft Office Outlook"),
        ("onenote.exe", "Microsoft Office OneNote"),
        ("lync.exe", "Skype for Business"),
        ("mspub.exe", "Microsoft Publisher"),
        ("vlc.exe", "VLC Media Player"),
        ("wmplayer.exe", "Windows Media Player"),
        ("chrome.exe", "Web browser"),
        ("msedge.exe", "Web browser"),
        ("brave.exe", "Web browser"),
        ("firefox.exe", "Web browser"),
        ("discord.exe", "Chat client"),
        ("obs64.exe", "Streaming / recording studio"),
        ("code.exe", "Visual Studio Code"),
        ("devenv.exe", "Visual Studio IDE"),
        ("cmd.exe", "Command prompt"),
        ("powershell.exe", "PowerShell"),
        ("pwsh.exe", "PowerShell 7"),
        ("windowsterminal.exe", "Windows Terminal"),
        ("radeonsoftware.exe", "AMD Adrenalin UI"),
        ("msiafterburner.exe", "MSI Afterburner")
    ];

    public static void InitializeDatabase(SqliteConnection conn)
    {
        using var pragmaCmd = conn.CreateCommand();
        pragmaCmd.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = FULL;
            PRAGMA foreign_keys = ON;
            PRAGMA busy_timeout = 5000;
            """;
        pragmaCmd.ExecuteNonQuery();

        using var schemaCmd = conn.CreateCommand();
        schemaCmd.CommandText = """
            CREATE TABLE IF NOT EXISTS GPU_DEVICES (
                PnpDeviceId TEXT PRIMARY KEY,
                HardwareKey TEXT NOT NULL,
                UniqueId INTEGER NOT NULL,
                MarketingName TEXT NOT NULL,
                LastSeenDriverVer TEXT NOT NULL,
                HardwareLimitsJson TEXT NOT NULL DEFAULT '{}',
                UpdatedAtUtc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS FAN_PROFILES (
                FanProfileId TEXT PRIMARY KEY,
                GpuHardwareKey TEXT NOT NULL,
                Name TEXT NOT NULL UNIQUE,
                IsDefault INTEGER NOT NULL DEFAULT 0,
                ZeroRpmEnabled INTEGER NOT NULL DEFAULT 1,
                MinAcousticLimitMHz INTEGER NULL,
                CurvePayloadSha256 TEXT NOT NULL,
                RevisionNumber INTEGER NOT NULL DEFAULT 1,
                UpdatedAtUtc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS FAN_CURVE_POINTS (
                FanProfileId TEXT NOT NULL,
                PointIndex INTEGER NOT NULL CHECK (PointIndex BETWEEN 0 AND 7),
                TemperatureC INTEGER NOT NULL CHECK (TemperatureC BETWEEN 15 AND 115),
                FanSpeedPwmPercent INTEGER NOT NULL CHECK (FanSpeedPwmPercent BETWEEN 0 AND 100),
                PRIMARY KEY (FanProfileId, PointIndex),
                FOREIGN KEY (FanProfileId) REFERENCES FAN_PROFILES(FanProfileId) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS TUNING_PROFILES (
                TuningProfileId TEXT PRIMARY KEY,
                GpuHardwareKey TEXT NOT NULL,
                Name TEXT NOT NULL UNIQUE,
                IsGlobalDefault INTEGER NOT NULL DEFAULT 0,
                IsQuarantined INTEGER NOT NULL DEFAULT 0,
                QuarantineReason TEXT NULL,
                EnableClockTuning INTEGER NOT NULL DEFAULT 0,
                MinCoreClockMHz INTEGER NOT NULL DEFAULT 500,
                MaxCoreClockMHz INTEGER NOT NULL DEFAULT 2400,
                EnableVoltageTuning INTEGER NOT NULL DEFAULT 0,
                CoreVoltageMv INTEGER NOT NULL DEFAULT 1150,
                EnableVramTuning INTEGER NOT NULL DEFAULT 0,
                MaxVramClockMHz INTEGER NOT NULL DEFAULT 2000,
                VramTimingMode INTEGER NOT NULL DEFAULT 0,
                EnablePowerTuning INTEGER NOT NULL DEFAULT 0,
                PowerLimitDeltaPercent INTEGER NOT NULL DEFAULT 0,
                LinkedFanProfileId TEXT NOT NULL,
                ThreeDSettingsJson TEXT NOT NULL DEFAULT '{}',
                PayloadSha256 TEXT NOT NULL,
                RevisionNumber INTEGER NOT NULL DEFAULT 1,
                UpdatedAtUtc TEXT NOT NULL,
                FOREIGN KEY (LinkedFanProfileId) REFERENCES FAN_PROFILES(FanProfileId)
            );

            CREATE TABLE IF NOT EXISTS GAME_PROFILES (
                GameProfileId TEXT PRIMARY KEY,
                Title TEXT NOT NULL,
                LauncherSource TEXT NOT NULL DEFAULT 'Manual',
                LauncherAppId TEXT NOT NULL DEFAULT '',
                TuningProfileId TEXT NOT NULL,
                OverrideFanProfileId TEXT NULL,
                IsEnabled INTEGER NOT NULL DEFAULT 1,
                LockWhileProcessRunning INTEGER NOT NULL DEFAULT 1,
                ZeroRpmOverride INTEGER NOT NULL DEFAULT 0,
                LastLaunchedUtc TEXT NULL,
                FOREIGN KEY (TuningProfileId) REFERENCES TUNING_PROFILES(TuningProfileId),
                FOREIGN KEY (OverrideFanProfileId) REFERENCES FAN_PROFILES(FanProfileId)
            );

            CREATE TABLE IF NOT EXISTS GAME_EXECUTABLES (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                GameProfileId TEXT NOT NULL,
                ExeFullPath TEXT NOT NULL,
                ExeFileName TEXT NOT NULL,
                Role TEXT NOT NULL CHECK (Role IN ('PrimaryRenderer', 'LauncherBootstrapper')),
                FOREIGN KEY (GameProfileId) REFERENCES GAME_PROFILES(GameProfileId) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS IX_GAME_EXECUTABLES_FILENAME ON GAME_EXECUTABLES(ExeFileName);

            CREATE TABLE IF NOT EXISTS DENYLIST_EXECUTABLES (
                ExeFileName TEXT PRIMARY KEY,
                Reason TEXT NOT NULL,
                IsBuiltIn INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS PROFILE_REVISIONS (
                RevisionId INTEGER PRIMARY KEY AUTOINCREMENT,
                EntityType TEXT NOT NULL,
                EntityId TEXT NOT NULL,
                RevisionNumber INTEGER NOT NULL,
                SnapshotJson TEXT NOT NULL,
                ChangeReason TEXT NOT NULL,
                CreatedAtUtc TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS IX_PROFILE_REVISIONS_ENTITY ON PROFILE_REVISIONS(EntityType, EntityId, RevisionNumber DESC);

            CREATE TABLE IF NOT EXISTS APPLIED_STATE_LOG (
                LogId INTEGER PRIMARY KEY AUTOINCREMENT,
                GpuHardwareKey TEXT NOT NULL,
                TargetProfileId TEXT NOT NULL,
                TargetProfileName TEXT NOT NULL,
                TargetFanProfileId TEXT NOT NULL,
                TargetFanProfileName TEXT NOT NULL,
                PersistStatus TEXT NOT NULL,
                ApplyStatus TEXT NOT NULL,
                DriverReadbackJson TEXT NOT NULL,
                ErrorCode TEXT NULL,
                TriggerReason TEXT NOT NULL,
                TimestampUtc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS APP_SETTINGS (
                SettingKey TEXT PRIMARY KEY,
                SettingValue TEXT NOT NULL,
                UpdatedAtUtc TEXT NOT NULL
            );
            """;
        schemaCmd.ExecuteNonQuery();

        MigrateSchema(conn);
        SeedDefaults(conn);
    }

    private static void MigrateSchema(SqliteConnection conn)
    {
        if (!ColumnExists(conn, "GAME_PROFILES", "ZeroRpmOverride"))
        {
            using var alterCmd = conn.CreateCommand();
            alterCmd.CommandText = "ALTER TABLE GAME_PROFILES ADD COLUMN ZeroRpmOverride INTEGER NOT NULL DEFAULT 0;";
            alterCmd.ExecuteNonQuery();
        }

        using var verCmd = conn.CreateCommand();
        verCmd.CommandText = $"PRAGMA user_version = {CurrentSchemaVersion};";
        verCmd.ExecuteNonQuery();
    }

    private static bool ColumnExists(SqliteConnection conn, string tableName, string columnName)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({tableName});";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static void SeedDefaults(SqliteConnection conn)
    {
        using var tx = conn.BeginTransaction();
        foreach (var (exe, reason) in BuiltInDenylist)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT OR IGNORE INTO DENYLIST_EXECUTABLES (ExeFileName, Reason, IsBuiltIn)
                VALUES ($exe, $reason, 1);
                """;
            cmd.Parameters.AddWithValue("$exe", exe.ToLowerInvariant());
            cmd.Parameters.AddWithValue("$reason", reason);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }
}
