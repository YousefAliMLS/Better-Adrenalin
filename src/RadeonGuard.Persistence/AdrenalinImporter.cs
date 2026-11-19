using System.Text.Json;
using System.Xml.Linq;
using RadeonGuard.Core.Models;
using RadeonGuard.Core.Validation;

namespace RadeonGuard.Persistence;

public sealed record AdrenalinImportSummary(
    int GamesImported,
    int NonGamesFilteredOut,
    int FanCurvesRecovered,
    IReadOnlyList<string> Messages);

public sealed class AdrenalinImporter
{
    private readonly RadeonGuardRepository _repo;

    public AdrenalinImporter(RadeonGuardRepository repo)
    {
        _repo = repo;
    }

    public static string DefaultGmdbPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AMD", "CN", "gmdb.blb");

    /// <summary>
    /// Reads AMD Adrenalin's gmdb.blb strictly read-only, filtering out false-positive non-games
    /// and recovering any saved custom OverDrive8 fan curves (e.g. Fortnite's 5-point fan curve).
    /// </summary>
    public async Task<AdrenalinImportSummary> ImportFromGmdbAsync(
        string? gmdbPath = null,
        string gpuHardwareKey = "",
        CancellationToken ct = default)
    {
        string path = gmdbPath ?? DefaultGmdbPath;
        var messages = new List<string>();
        if (!File.Exists(path))
        {
            return new AdrenalinImportSummary(0, 0, 0, [$"Adrenalin database not found at {path}"]);
        }

        string jsonText;
        await using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var sr = new StreamReader(fs))
        {
            jsonText = await sr.ReadToEndAsync(ct).ConfigureAwait(false);
        }

        using var doc = JsonDocument.Parse(jsonText);
        if (!doc.RootElement.TryGetProperty("games", out var gamesEl) || gamesEl.ValueKind != JsonValueKind.Array)
        {
            return new AdrenalinImportSummary(0, 0, 0, ["No 'games' array found in gmdb.blb."]);
        }

        var denylist = (await _repo.GetDenylistAsync(ct).ConfigureAwait(false))
            .Select(d => d.ExeFileName.ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var existingGames = await _repo.GetGameProfilesAsync(ct).ConfigureAwait(false);
        var (_, defaultTuning) = await _repo.EnsureDefaultProfilesAsync(gpuHardwareKey, ct: ct).ConfigureAwait(false);

        int importedGames = 0;
        int filteredOut = 0;
        int recoveredCurves = 0;

        foreach (var gameEl in gamesEl.EnumerateArray())
        {
            string title = GetStr(gameEl, "title");
            string exePath = GetStr(gameEl, "exe_path");
            string renderingProc = GetStr(gameEl, "rendering_process");

            if (string.IsNullOrWhiteSpace(title) || (string.IsNullOrWhiteSpace(exePath) && string.IsNullOrWhiteSpace(renderingProc)))
            {
                continue;
            }

            string primaryExeName = !string.IsNullOrWhiteSpace(renderingProc)
                ? Path.GetFileName(renderingProc).ToLowerInvariant()
                : Path.GetFileName(exePath).ToLowerInvariant();

            string exeFileName = Path.GetFileName(exePath).ToLowerInvariant();

            if (denylist.Contains(primaryExeName) || (!string.IsNullOrEmpty(exeFileName) && denylist.Contains(exeFileName)))
            {
                filteredOut++;
                messages.Add($"Filtered false-positive non-game: {title} ({primaryExeName})");
                continue;
            }

            // Check if this game has an OverDrive8 fan curve saved (even if Adrenalin disabled profileEnabled!)
            string? recoveredFanId = null;
            if (gameEl.TryGetProperty("overdrive", out var odArr) && odArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var od in odArr.EnumerateArray())
                {
                    if (!od.TryGetProperty("settings8", out var s8) || s8.ValueKind != JsonValueKind.Array) continue;

                    var fanProfile = TryExtractFanCurveFromSettings8(title, gpuHardwareKey, s8);
                    if (fanProfile is not null && ProfileValidator.ValidateFanProfile(fanProfile).IsValid)
                    {
                        var existingFans = await _repo.GetFanProfilesAsync(ct).ConfigureAwait(false);
                        var matchFan = existingFans.FirstOrDefault(f => string.Equals(f.Name, fanProfile.Name, StringComparison.OrdinalIgnoreCase));
                        if (matchFan is null)
                        {
                            var savedFan = await _repo.SaveFanProfileAsync(fanProfile, "ImportedFromAdrenalinGmdb", ct).ConfigureAwait(false);
                            recoveredFanId = savedFan.FanProfileId;
                            recoveredCurves++;
                            messages.Add($"Recovered custom fan curve '{savedFan.Name}' ({string.Join(", ", savedFan.Points.Select(p => $"{p.TemperatureC}°C→{p.FanSpeedPercent}%"))})");
                        }
                        else
                        {
                            recoveredFanId = matchFan.FanProfileId;
                        }
                    }
                }
            }

            // Avoid duplicate game import if already tracked by title or executable
            bool alreadyExists = existingGames.Any(g =>
                string.Equals(g.Title, title, StringComparison.OrdinalIgnoreCase) ||
                g.Executables.Any(e => string.Equals(e.ExeFileName, primaryExeName, StringComparison.OrdinalIgnoreCase)));

            if (alreadyExists)
            {
                continue;
            }

            var exes = new List<GameExecutable>();
            if (!string.IsNullOrWhiteSpace(exePath))
            {
                bool hasSeparateRenderer = !string.IsNullOrWhiteSpace(renderingProc) &&
                                           !string.Equals(Path.GetFileName(exePath), Path.GetFileName(renderingProc), StringComparison.OrdinalIgnoreCase);
                exes.Add(new GameExecutable
                {
                    ExeFullPath = exePath,
                    ExeFileName = Path.GetFileName(exePath).ToLowerInvariant(),
                    Role = hasSeparateRenderer ? ExecutableRole.LauncherBootstrapper : ExecutableRole.PrimaryRenderer
                });
            }
            if (!string.IsNullOrWhiteSpace(renderingProc) &&
                exes.All(e => !string.Equals(e.ExeFileName, Path.GetFileName(renderingProc), StringComparison.OrdinalIgnoreCase)))
            {
                exes.Add(new GameExecutable
                {
                    ExeFullPath = renderingProc,
                    ExeFileName = Path.GetFileName(renderingProc).ToLowerInvariant(),
                    Role = ExecutableRole.PrimaryRenderer
                });
            }

            string launcher = "Manual";
            if (gameEl.TryGetProperty("steam_id", out var st) && st.TryGetInt64(out long sid) && sid > 0) launcher = "Steam";
            else if (gameEl.TryGetProperty("uplay_id", out var up) && up.TryGetInt64(out long uid) && uid > 0) launcher = "Ubisoft";

            var newGame = new GameProfile
            {
                Title = title,
                LauncherSource = launcher,
                TuningProfileId = defaultTuning.TuningProfileId,
                OverrideFanProfileId = recoveredFanId,
                IsEnabled = true,
                LockWhileProcessRunning = true,
                Executables = exes
            };
            await _repo.SaveGameProfileAsync(newGame, "ImportedFromAdrenalinGmdb", ct).ConfigureAwait(false);
            importedGames++;
            messages.Add($"Imported game: {title} ({primaryExeName})");
        }

        return new AdrenalinImportSummary(importedGames, filteredOut, recoveredCurves, messages);
    }

    private static FanProfile? TryExtractFanCurveFromSettings8(string gameTitle, string gpuHardwareKey, JsonElement settings8)
    {
        JsonElement? fanFeature = null;
        bool zeroRpm = true;

        foreach (var item in settings8.EnumerateArray())
        {
            if (!item.TryGetProperty("featureId", out var fidEl) || !fidEl.TryGetInt32(out int fid)) continue;
            if (fid == 22) fanFeature = item;
            if (fid == 23 && item.TryGetProperty("featureValue", out var zfv))
            {
                if (zfv.TryGetProperty("Enabled_", out var zen) &&
                    string.Equals(zen.GetString(), "TRUE", StringComparison.OrdinalIgnoreCase) &&
                    zfv.TryGetProperty("ActualValue_", out var zav) && zav.TryGetInt32(out int zv))
                {
                    zeroRpm = zv != 0;
                }
            }
        }

        if (fanFeature is null) return null;
        if (!fanFeature.Value.TryGetProperty("featureValue", out var fv)) return null;
        if (!string.Equals(GetStr(fv, "Enabled_"), "TRUE", StringComparison.OrdinalIgnoreCase)) return null;
        if (!fv.TryGetProperty("settings", out var ptsArr) || ptsArr.ValueKind != JsonValueKind.Array) return null;

        var rawValues = new Dictionary<int, int>();
        foreach (var pt in ptsArr.EnumerateArray())
        {
            if (pt.TryGetProperty("featureId", out var pidEl) && pidEl.TryGetInt32(out int pid) &&
                pt.TryGetProperty("featureValue", out var pfv) &&
                pfv.TryGetProperty("expectedValue_", out var expEl) && expEl.TryGetInt32(out int val))
            {
                rawValues[pid] = val;
            }
        }

        var points = new List<FanCurvePoint>(5);
        for (int i = 0; i < 5; i++)
        {
            if (rawValues.TryGetValue(i * 2, out int temp) && rawValues.TryGetValue(i * 2 + 1, out int pwm) && temp > 0)
            {
                points.Add(new FanCurvePoint(temp, pwm));
            }
        }

        if (points.Count != 5) return null;

        return new FanProfile
        {
            GpuHardwareKey = gpuHardwareKey,
            Name = $"{gameTitle} (Recovered from Adrenalin)",
            ZeroRpmEnabled = zeroRpm,
            Points = points
        };
    }

    public async Task<FanProfile?> ImportWattManXmlFanCurveAsync(string xmlPath, string profileName, string gpuHardwareKey, CancellationToken ct = default)
    {
        if (!File.Exists(xmlPath)) return null;
        string content = await File.ReadAllTextAsync(xmlPath, ct).ConfigureAwait(false);
        var doc = XDocument.Parse(content);
        var fanFeature = doc.Descendants("FEATURE")
            .FirstOrDefault(f => (string?)f.Attribute("ID") == "22");
        if (fanFeature is null) return null;

        var states = fanFeature.Descendants("STATE")
            .ToDictionary(
                s => int.Parse((string?)s.Attribute("ID") ?? "-1"),
                s => int.Parse((string?)s.Attribute("Value") ?? "0"));

        var points = new List<FanCurvePoint>(5);
        for (int i = 0; i < 5; i++)
        {
            if (states.TryGetValue(i * 2, out int t) && states.TryGetValue(i * 2 + 1, out int p))
            {
                points.Add(new FanCurvePoint(t, p));
            }
        }
        if (points.Count != 5) return null;

        var fan = new FanProfile
        {
            GpuHardwareKey = gpuHardwareKey,
            Name = profileName,
            ZeroRpmEnabled = true,
            Points = points
        };
        if (!ProfileValidator.ValidateFanProfile(fan).IsValid) return null;
        return await _repo.SaveFanProfileAsync(fan, "ImportedFromWattManXml", ct).ConfigureAwait(false);
    }

    private static string GetStr(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var val) && val.ValueKind == JsonValueKind.String
            ? val.GetString() ?? string.Empty
            : string.Empty;
}
