using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace RadeonGuard.Persistence;

public sealed record BackupInfo(string FilePath, string Sha256, DateTimeOffset CreatedAtUtc, long SizeBytes, bool ChecksumValid);

public sealed class BackupManager
{
    private readonly string _dbPath;
    private readonly string _backupsDir;

    public BackupManager(string? dbPath = null, string? backupsDir = null)
    {
        _dbPath = dbPath ?? StoragePaths.GetDatabasePath();
        _backupsDir = backupsDir ?? StoragePaths.GetBackupsDirectory();
    }

    public async Task<BackupInfo> CreateBackupAsync(string reason = "manual", CancellationToken ct = default)
    {
        Directory.CreateDirectory(_backupsDir);
        string safeReason = string.Concat(reason.Where(c => char.IsLetterOrDigit(c) || c == '_' || c == '-'));
        if (string.IsNullOrEmpty(safeReason)) safeReason = "backup";

        string fileName = $"radeonguard_{DateTimeOffset.UtcNow:yyyyMMdd_HHmmss_fff}_{safeReason}.db";
        string destPath = Path.Combine(_backupsDir, fileName);
        string tempPath = destPath + ".tmp";

        if (File.Exists(tempPath)) File.Delete(tempPath);

        var connStr = new SqliteConnectionStringBuilder { DataSource = _dbPath, Mode = SqliteOpenMode.ReadWrite }.ToString();
        using (var conn = new SqliteConnection(connStr))
        {
            await conn.OpenAsync(ct).ConfigureAwait(false);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "VACUUM INTO $dest;";
            cmd.Parameters.AddWithValue("$dest", tempPath);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        File.Move(tempPath, destPath, overwrite: true);

        string sha = await ComputeFileSha256Async(destPath, ct).ConfigureAwait(false);
        await File.WriteAllTextAsync(destPath + ".sha256", sha, ct).ConfigureAwait(false);

        PruneOldBackups(20);
        var fi = new FileInfo(destPath);
        return new BackupInfo(destPath, sha, fi.LastWriteTimeUtc, fi.Length, true);
    }

    public async Task<IReadOnlyList<BackupInfo>> ListBackupsAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(_backupsDir)) return Array.Empty<BackupInfo>();
        var files = new DirectoryInfo(_backupsDir)
            .GetFiles("radeonguard_*.db")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .ToList();

        var results = new List<BackupInfo>(files.Count);
        foreach (var f in files)
        {
            string shaFile = f.FullName + ".sha256";
            string actual = await ComputeFileSha256Async(f.FullName, ct).ConfigureAwait(false);
            bool valid = File.Exists(shaFile) &&
                         string.Equals((await File.ReadAllTextAsync(shaFile, ct).ConfigureAwait(false)).Trim(), actual, StringComparison.OrdinalIgnoreCase);
            results.Add(new BackupInfo(f.FullName, actual, f.LastWriteTimeUtc, f.Length, valid));
        }
        return results;
    }

    public async Task<bool> RestoreBackupAsync(string backupFilePath, CancellationToken ct = default)
    {
        if (!File.Exists(backupFilePath)) return false;
        string shaFile = backupFilePath + ".sha256";
        if (File.Exists(shaFile))
        {
            string expected = (await File.ReadAllTextAsync(shaFile, ct).ConfigureAwait(false)).Trim();
            string actual = await ComputeFileSha256Async(backupFilePath, ct).ConfigureAwait(false);
            if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
        {
            string corruptCopy = _dbPath + $".corrupt_{DateTimeOffset.UtcNow:yyyyMMdd_HHmmss}";
            File.Move(_dbPath, corruptCopy, overwrite: true);
        }
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            if (File.Exists(_dbPath + suffix)) File.Delete(_dbPath + suffix);
        }
        File.Copy(backupFilePath, _dbPath, overwrite: true);
        return true;
    }

    public async Task<bool> RestoreLatestValidBackupAsync(CancellationToken ct = default)
    {
        var backups = await ListBackupsAsync(ct).ConfigureAwait(false);
        var valid = backups.FirstOrDefault(b => b.ChecksumValid);
        if (valid is null) return false;
        return await RestoreBackupAsync(valid.FilePath, ct).ConfigureAwait(false);
    }

    private void PruneOldBackups(int keepCount)
    {
        var files = new DirectoryInfo(_backupsDir)
            .GetFiles("radeonguard_*.db")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Skip(keepCount);

        foreach (var f in files)
        {
            try
            {
                f.Delete();
                var sha = f.FullName + ".sha256";
                if (File.Exists(sha)) File.Delete(sha);
            }
            catch { }
        }
    }

    private static async Task<string> ComputeFileSha256Async(string filePath, CancellationToken ct)
    {
        await using var stream = File.OpenRead(filePath);
        var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }
}
