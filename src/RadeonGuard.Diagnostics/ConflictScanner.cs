using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.ServiceProcess;
using Microsoft.Win32;

namespace RadeonGuard.Diagnostics;

public sealed record ConflictReport(
    IReadOnlyList<string> RunningGpuProcesses,
    IReadOnlyList<(string ServiceName, string Status)> ServiceStates,
    IReadOnlyList<string> RecentTdrEvents,
    bool FastStartupEnabled,
    IReadOnlyList<string> Recommendations);

public static class ConflictScanner
{
    private static readonly string[] KnownGpuManagers =
    [
        "RadeonSoftware",
        "amdow",
        "AMDRSServ",
        "MSIAfterburner",
        "RTSS",
        "FanControl",
        "ArmouryCrate"
    ];

    private static readonly string[] WatchedServices =
    [
        "AMD External Events Utility",
        "AMD Crash Defender Service"
    ];

    public static ConflictReport ScanSystem()
    {
        var running = new List<string>();
        foreach (var procName in KnownGpuManagers)
        {
            try
            {
                var procs = Process.GetProcessesByName(procName);
                if (procs.Length > 0)
                {
                    running.Add($"{procName}.exe (PID {procs[0].Id})");
                }
                foreach (var p in procs) p.Dispose();
            }
            catch { }
        }

        var svcStates = new List<(string, string)>();
        foreach (var svcName in WatchedServices)
        {
            try
            {
                using var sc = new ServiceController(svcName);
                svcStates.Add((svcName, $"{sc.Status} ({sc.StartType})"));
            }
            catch
            {
                svcStates.Add((svcName, "Not Installed"));
            }
        }

        var tdrs = QueryRecentTdrs();
        bool fastStartup = IsFastStartupEnabled();

        var recs = new List<string>();
        if (running.Any(r => r.StartsWith("MSIAfterburner", StringComparison.OrdinalIgnoreCase)))
        {
            recs.Add("MSI Afterburner is running. Disable its startup/fan control if you want RadeonGuard to have sole control over OverDrive8.");
        }
        if (running.Any(r => r.StartsWith("RadeonSoftware", StringComparison.OrdinalIgnoreCase) || r.StartsWith("amdow", StringComparison.OrdinalIgnoreCase)))
        {
            recs.Add("AMD Adrenalin is running. Keep Adrenalin Performance Tuning at Default; RadeonGuard's Drift Watchdog will automatically re-assert your saved curves if Adrenalin resets them.");
        }
        if (fastStartup)
        {
            recs.Add("Windows Fast Startup is enabled; cold boots may preserve stale driver state. Consider disabling HiberbootEnabled if drift occurs on boot.");
        }
        if (tdrs.Count > 0)
        {
            recs.Add($"Detected {tdrs.Count} display driver TDR reset(s) (Event ID 4101) in the last 7 days.");
        }

        return new ConflictReport(running, svcStates, tdrs, fastStartup, recs);
    }

    private static bool IsFastStartupEnabled()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Power");
            var val = key?.GetValue("HiberbootEnabled");
            return val is int i && i != 0;
        }
        catch
        {
            return false;
        }
    }

    private static IReadOnlyList<string> QueryRecentTdrs()
    {
        var list = new List<string>();
        try
        {
            string query = "*[System[(EventID=4101) and TimeCreated[timediff(@SystemTime) <= 604800000]]]";
            var eq = new EventLogQuery("System", PathType.LogName, query) { ReverseDirection = true };
            using var reader = new EventLogReader(eq);
            for (int i = 0; i < 10; i++)
            {
                using var ev = reader.ReadEvent();
                if (ev is null) break;
                list.Add($"{ev.TimeCreated:yyyy-MM-dd HH:mm:ss}: Display driver stopped responding and recovered ({ev.ProviderName})");
            }
        }
        catch
        {
            // Ignore event log access errors on restricted accounts.
        }
        return list;
    }
}
