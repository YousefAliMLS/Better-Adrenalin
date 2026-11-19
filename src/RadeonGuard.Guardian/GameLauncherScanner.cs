using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using RadeonGuard.Core.Models;
using RadeonGuard.Persistence;

namespace RadeonGuard.Guardian;

public sealed record DiscoveredGame(
    string Title,
    string LauncherSource,
    string LauncherAppId,
    string InstallDirectory,
    string PrimaryExePath);

public static partial class GameLauncherScanner
{
    private static readonly string[] IgnoredExePrefixes =
    [
        "unins", "crash", "setup", "redist", "vcredist", "vc_redist", "dxsetup", "dotnet", "easyanticheat", "battleye"
    ];

    public static async Task<int> ScanAndRegisterInstalledGamesAsync(
        RadeonGuardRepository repo,
        string gpuHardwareKey,
        CancellationToken ct = default)
    {
        var denylist = (await repo.GetDenylistAsync(ct).ConfigureAwait(false))
            .Select(d => d.ExeFileName.ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var existingGames = await repo.GetGameProfilesAsync(ct).ConfigureAwait(false);
        var (_, defaultTuning) = await repo.EnsureDefaultProfilesAsync(gpuHardwareKey, ct: ct).ConfigureAwait(false);

        var discovered = new List<DiscoveredGame>();
        discovered.AddRange(ScanSteamLibraries());
        discovered.AddRange(ScanEpicManifests());
        discovered.AddRange(ScanUbisoftRegistry());

        int added = 0;
        foreach (var item in discovered)
        {
            ct.ThrowIfCancellationRequested();
            string exeName = Path.GetFileName(item.PrimaryExePath).ToLowerInvariant();
            if (string.IsNullOrEmpty(exeName) || denylist.Contains(exeName)) continue;

            bool exists = existingGames.Any(g =>
                string.Equals(g.Title, item.Title, StringComparison.OrdinalIgnoreCase) ||
                g.Executables.Any(e => string.Equals(e.ExeFileName, exeName, StringComparison.OrdinalIgnoreCase)));
            if (exists) continue;

            var profile = new GameProfile
            {
                Title = item.Title,
                LauncherSource = item.LauncherSource,
                LauncherAppId = item.LauncherAppId,
                TuningProfileId = defaultTuning.TuningProfileId,
                IsEnabled = true,
                LockWhileProcessRunning = true,
                Executables =
                [
                    new GameExecutable
                    {
                        ExeFullPath = item.PrimaryExePath,
                        ExeFileName = exeName,
                        Role = ExecutableRole.PrimaryRenderer
                    }
                ]
            };

            await repo.SaveGameProfileAsync(profile, "LauncherAutoScan", ct).ConfigureAwait(false);
            added++;
        }

        return added;
    }

    public static IReadOnlyList<DiscoveredGame> ScanSteamLibraries()
    {
        var results = new List<DiscoveredGame>();
        try
        {
            string? steamPath = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string
                             ?? @"C:\Program Files (x86)\Steam";

            string vdfPath = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
            var libraryRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (Directory.Exists(Path.Combine(steamPath, "steamapps")))
            {
                libraryRoots.Add(Path.Combine(steamPath, "steamapps"));
            }

            if (File.Exists(vdfPath))
            {
                string vdf = File.ReadAllText(vdfPath);
                foreach (Match m in SteamPathRegex().Matches(vdf))
                {
                    string lib = m.Groups[1].Value.Replace(@"\\", @"\");
                    string appsDir = Path.Combine(lib, "steamapps");
                    if (Directory.Exists(appsDir)) libraryRoots.Add(appsDir);
                }
            }

            foreach (var appsDir in libraryRoots)
            {
                foreach (var acfPath in Directory.GetFiles(appsDir, "appmanifest_*.acf"))
                {
                    try
                    {
                        string acf = File.ReadAllText(acfPath);
                        string appId = ExtractVdfValue(acf, "appid");
                        string name = ExtractVdfValue(acf, "name");
                        string installDir = ExtractVdfValue(acf, "installdir");
                        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(installDir)) continue;
                        if (name.Contains("Steamworks Common Redistributables", StringComparison.OrdinalIgnoreCase)) continue;

                        string fullDir = Path.Combine(appsDir, "common", installDir);
                        if (!Directory.Exists(fullDir)) continue;
                        string? bestExe = FindBestExecutable(fullDir, name);
                        if (bestExe is not null)
                        {
                            results.Add(new DiscoveredGame(name, "Steam", appId, fullDir, bestExe));
                        }
                    }
                    catch { }
                }
            }
        }
        catch { }
        return results;
    }

    public static IReadOnlyList<DiscoveredGame> ScanEpicManifests()
    {
        var results = new List<DiscoveredGame>();
        try
        {
            string manifestsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Epic", "EpicGamesLauncher", "Data", "Manifests");
            if (!Directory.Exists(manifestsDir)) return results;

            foreach (var itemFile in Directory.GetFiles(manifestsDir, "*.item"))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(itemFile));
                    var root = doc.RootElement;
                    string displayName = root.TryGetProperty("DisplayName", out var dn) ? dn.GetString() ?? "" : "";
                    string installLoc = root.TryGetProperty("InstallLocation", out var il) ? il.GetString() ?? "" : "";
                    string launchExe = root.TryGetProperty("LaunchExecutable", out var le) ? le.GetString() ?? "" : "";
                    string appName = root.TryGetProperty("AppName", out var an) ? an.GetString() ?? "" : "";

                    if (string.IsNullOrWhiteSpace(displayName) || string.IsNullOrWhiteSpace(installLoc)) continue;
                    string exePath = !string.IsNullOrWhiteSpace(launchExe) ? Path.Combine(installLoc, launchExe) : "";
                    if (!File.Exists(exePath))
                    {
                        exePath = FindBestExecutable(installLoc, displayName) ?? "";
                    }
                    if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
                    {
                        results.Add(new DiscoveredGame(displayName, "Epic", appName, installLoc, exePath));
                    }
                }
                catch { }
            }
        }
        catch { }
        return results;
    }

    public static IReadOnlyList<DiscoveredGame> ScanUbisoftRegistry()
    {
        var results = new List<DiscoveredGame>();
        try
        {
            using var installs = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Ubisoft\Launcher\Installs");
            if (installs is null) return results;

            foreach (var subName in installs.GetSubKeyNames())
            {
                using var sub = installs.OpenSubKey(subName);
                string? installDir = sub?.GetValue("InstallDir") as string;
                if (string.IsNullOrWhiteSpace(installDir) || !Directory.Exists(installDir)) continue;
                string title = new DirectoryInfo(installDir.TrimEnd('\\', '/')).Name;
                string? bestExe = FindBestExecutable(installDir, title);
                if (bestExe is not null)
                {
                    results.Add(new DiscoveredGame(title, "Ubisoft", subName, installDir, bestExe));
                }
            }
        }
        catch { }
        return results;
    }

    private static string? FindBestExecutable(string rootDir, string gameTitle)
    {
        try
        {
            var candidates = Directory.EnumerateFiles(rootDir, "*.exe", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                MaxRecursionDepth = 3,
                IgnoreInaccessible = true
            })
            .Where(p =>
            {
                string fn = Path.GetFileNameWithoutExtension(p).ToLowerInvariant();
                return !IgnoredExePrefixes.Any(pref => fn.StartsWith(pref, StringComparison.Ordinal));
            })
            .Select(p => new FileInfo(p))
            .OrderByDescending(fi => fi.Name.Contains("Shipping", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(fi => fi.Name.Contains(gameTitle.Replace(" ", ""), StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(fi => fi.Length)
            .FirstOrDefault();

            return candidates?.FullName;
        }
        catch
        {
            return null;
        }
    }

    private static string ExtractVdfValue(string vdf, string key)
    {
        var m = Regex.Match(vdf, $"\"{Regex.Escape(key)}\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : string.Empty;
    }

    [GeneratedRegex("\"path\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex SteamPathRegex();
}
