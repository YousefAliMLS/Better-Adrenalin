using RadeonGuard.Persistence;
using Serilog;
using Serilog.Formatting.Compact;

namespace RadeonGuard.Diagnostics;

public static class FlightRecorder
{
    public static ILogger CreateLogger(string? logsDir = null)
    {
        string dir = logsDir ?? StoragePaths.GetLogsDirectory();
        Directory.CreateDirectory(dir);

        return new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.FromLogContext()
            .WriteTo.File(
                Path.Combine(dir, "radeonguard-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                shared: true,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.File(
                new CompactJsonFormatter(),
                Path.Combine(dir, "radeonguard-.jsonl"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                shared: true)
            .CreateLogger();
    }

    public static async Task<IReadOnlyList<string>> ReadRecentLogLinesAsync(int maxLines = 200, string? logsDir = null, CancellationToken ct = default)
    {
        string dir = logsDir ?? StoragePaths.GetLogsDirectory();
        if (!Directory.Exists(dir)) return Array.Empty<string>();

        var latest = new DirectoryInfo(dir)
            .GetFiles("radeonguard-*.log")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .FirstOrDefault();

        if (latest is null) return Array.Empty<string>();

        var lines = new List<string>();
        await using var fs = new FileStream(latest.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs);
        while (!sr.EndOfStream)
        {
            ct.ThrowIfCancellationRequested();
            string? line = await sr.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is not null) lines.Add(line);
        }

        if (lines.Count <= maxLines) return lines;
        return lines.Skip(lines.Count - maxLines).ToList();
    }
}
