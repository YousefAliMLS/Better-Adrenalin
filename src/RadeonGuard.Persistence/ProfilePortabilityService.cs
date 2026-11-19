using System.Text.Json;
using System.Xml.Linq;
using RadeonGuard.Core.Models;
using RadeonGuard.Core.Validation;

namespace RadeonGuard.Persistence;

public sealed record ProfileBundleDto(
    int FormatVersion,
    DateTimeOffset ExportedAtUtc,
    string BundleSha256,
    IReadOnlyList<FanProfile> FanProfiles,
    IReadOnlyList<TuningProfile> TuningProfiles,
    IReadOnlyList<GameProfile> GameProfiles);

public sealed class ProfilePortabilityService
{
    private readonly RadeonGuardRepository _repo;
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public ProfilePortabilityService(RadeonGuardRepository repo)
    {
        _repo = repo;
    }

    public async Task ExportBundleAsync(string outputFilePath, CancellationToken ct = default)
    {
        var fans = await _repo.GetFanProfilesAsync(ct).ConfigureAwait(false);
        var tunings = await _repo.GetTuningProfilesAsync(ct).ConfigureAwait(false);
        var games = await _repo.GetGameProfilesAsync(ct).ConfigureAwait(false);

        string contentForHash = JsonSerializer.Serialize(new { fans, tunings, games });
        string hash = RadeonGuardRepository.ComputeSha256(contentForHash);

        var bundle = new ProfileBundleDto(1, DateTimeOffset.UtcNow, hash, fans, tunings, games);
        string temp = outputFilePath + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(bundle, JsonOpts), ct).ConfigureAwait(false);
        File.Move(temp, outputFilePath, overwrite: true);
    }

    public async Task<(int Fans, int Tunings, int Games)> ImportBundleAsync(string inputFilePath, CancellationToken ct = default)
    {
        string raw = await File.ReadAllTextAsync(inputFilePath, ct).ConfigureAwait(false);
        if (raw.TrimStart().StartsWith('<'))
        {
            var xmlFans = await ImportFanProfilesAsync(inputFilePath, ct: ct).ConfigureAwait(false);
            return (xmlFans.Count, 0, 0);
        }

        using (var doc = JsonDocument.Parse(raw))
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("Invalid RadeonGuard bundle JSON.");
            }
            if (!doc.RootElement.TryGetProperty(nameof(ProfileBundleDto.FanProfiles), out _) &&
                !doc.RootElement.TryGetProperty(nameof(ProfileBundleDto.TuningProfiles), out _) &&
                !doc.RootElement.TryGetProperty(nameof(ProfileBundleDto.GameProfiles), out _))
            {
                var singleOrConverted = await ImportFanProfilesAsync(inputFilePath, ct: ct).ConfigureAwait(false);
                return (singleOrConverted.Count, 0, 0);
            }
        }

        var bundle = JsonSerializer.Deserialize<ProfileBundleDto>(raw)
                     ?? throw new InvalidDataException("Invalid RadeonGuard bundle JSON.");

        var bundleFans = bundle.FanProfiles ?? Array.Empty<FanProfile>();
        var bundleTunings = bundle.TuningProfiles ?? Array.Empty<TuningProfile>();
        var bundleGames = bundle.GameProfiles ?? Array.Empty<GameProfile>();

        string expectedHash = RadeonGuardRepository.ComputeSha256(
            JsonSerializer.Serialize(new { fans = bundleFans, tunings = bundleTunings, games = bundleGames }));

        if (!string.IsNullOrEmpty(bundle.BundleSha256) &&
            !string.Equals(expectedHash, bundle.BundleSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Bundle SHA-256 verification failed. The file may be corrupted or tampered with.");
        }

        int f = 0, t = 0, g = 0;
        foreach (var fan in bundleFans)
        {
            await _repo.SaveFanProfileAsync(fan, "BundleImport", ct).ConfigureAwait(false);
            f++;
        }
        foreach (var tuning in bundleTunings)
        {
            await _repo.SaveTuningProfileAsync(tuning, "BundleImport", ct).ConfigureAwait(false);
            t++;
        }
        foreach (var game in bundleGames)
        {
            await _repo.SaveGameProfileAsync(game, "BundleImport", ct).ConfigureAwait(false);
            g++;
        }
        return (f, t, g);
    }

    public async Task ExportFanProfileToXmlAsync(
        FanProfile fanProfile,
        string outputFilePath,
        string? devId = "73BF",
        string? revId = "C1",
        CancellationToken ct = default)
    {
        var states = new List<XElement>();
        for (int i = 0; i < fanProfile.Points.Count && i < 5; i++)
        {
            states.Add(new XElement("STATE",
                new XAttribute("ID", i * 2),
                new XAttribute("Enabled", "False"),
                new XAttribute("Value", fanProfile.Points[i].TemperatureC)));
            states.Add(new XElement("STATE",
                new XAttribute("ID", i * 2 + 1),
                new XAttribute("Enabled", "False"),
                new XAttribute("Value", fanProfile.Points[i].FanSpeedPercent)));
        }
        states.Add(new XElement("STATE",
            new XAttribute("ID", 10),
            new XAttribute("Enabled", "False"),
            new XAttribute("Value", 0)));

        var doc = new XDocument(
            new XElement("SYSTEM",
                new XElement("GPU",
                    new XAttribute("DevID", string.IsNullOrWhiteSpace(devId) ? "73BF" : devId),
                    new XAttribute("RevID", string.IsNullOrWhiteSpace(revId) ? "C1" : revId),
                    new XElement("FEATURE",
                        new XAttribute("ID", "22"),
                        new XAttribute("Enabled", "True"),
                        new XElement("STATES", states)),
                    new XElement("FEATURE",
                        new XAttribute("ID", "23"),
                        new XAttribute("Enabled", "True"),
                        new XElement("STATES",
                            new XElement("STATE",
                                new XAttribute("ID", "0"),
                                new XAttribute("Enabled", "True"),
                                new XAttribute("Value", fanProfile.ZeroRpmEnabled ? "1" : "0")))))));

        string temp = outputFilePath + ".tmp";
        await File.WriteAllTextAsync(temp, doc.ToString(), ct).ConfigureAwait(false);
        File.Move(temp, outputFilePath, overwrite: true);
    }

    public async Task ExportFanProfilesAsync(IReadOnlyList<FanProfile> fanProfiles, string outputFilePath, CancellationToken ct = default)
    {
        if (fanProfiles.Count == 0)
        {
            throw new InvalidOperationException("No fan profiles selected to export.");
        }

        IReadOnlyList<TuningProfile> emptyTunings = Array.Empty<TuningProfile>();
        IReadOnlyList<GameProfile> emptyGames = Array.Empty<GameProfile>();

        string contentForHash = JsonSerializer.Serialize(new { fans = fanProfiles, tunings = emptyTunings, games = emptyGames });
        string hash = RadeonGuardRepository.ComputeSha256(contentForHash);

        var bundle = new ProfileBundleDto(1, DateTimeOffset.UtcNow, hash, fanProfiles, emptyTunings, emptyGames);
        string temp = outputFilePath + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(bundle, JsonOpts), ct).ConfigureAwait(false);
        File.Move(temp, outputFilePath, overwrite: true);
    }

    public async Task<IReadOnlyList<FanProfile>> ImportFanProfilesAsync(
        string inputFilePath,
        GpuTuningCapabilities? capabilities = null,
        string? targetGpuHardwareKey = null,
        CancellationToken ct = default)
    {
        string raw = await File.ReadAllTextAsync(inputFilePath, ct).ConfigureAwait(false);
        string fallbackName = Path.GetFileNameWithoutExtension(inputFilePath);
        if (string.IsNullOrWhiteSpace(fallbackName)) fallbackName = "Imported Fan Curve";
        List<FanProfile> candidates = new();

        if (raw.TrimStart().StartsWith('<'))
        {
            var xmlFan = TryExtractWattManFanFromXml(raw, fallbackName, targetGpuHardwareKey ?? "UNKNOWN_GPU");
            if (xmlFan is null)
            {
                throw new InvalidDataException("The selected XML file does not contain a valid AMD fan curve (FEATURE ID=22).");
            }
            candidates.Add(xmlFan);
        }
        else
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("Invalid fan profile JSON root element.");
            }

            if (doc.RootElement.TryGetProperty(nameof(ProfileBundleDto.FanProfiles), out _))
            {
                var bundle = JsonSerializer.Deserialize<ProfileBundleDto>(raw)
                             ?? throw new InvalidDataException("Invalid RadeonGuard fan bundle JSON.");

                var bundleFans = bundle.FanProfiles ?? Array.Empty<FanProfile>();
                var bundleTunings = bundle.TuningProfiles ?? Array.Empty<TuningProfile>();
                var bundleGames = bundle.GameProfiles ?? Array.Empty<GameProfile>();

                string expectedHash = RadeonGuardRepository.ComputeSha256(
                    JsonSerializer.Serialize(new { fans = bundleFans, tunings = bundleTunings, games = bundleGames }));

                if (!string.IsNullOrEmpty(bundle.BundleSha256) &&
                    !string.Equals(expectedHash, bundle.BundleSha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("Fan profile SHA-256 verification failed. The file may be corrupted or tampered with.");
                }

                candidates.AddRange(bundleFans);
            }
            else if (doc.RootElement.TryGetProperty(nameof(FanProfile.Points), out _))
            {
                var single = JsonSerializer.Deserialize<FanProfile>(raw)
                             ?? throw new InvalidDataException("Invalid single fan profile JSON.");
                candidates.Add(single);
            }
            else
            {
                var convertedXmlFan = TryExtractWattManFanFromJson(doc.RootElement, fallbackName, targetGpuHardwareKey ?? "UNKNOWN_GPU");
                if (convertedXmlFan is null)
                {
                    throw new InvalidDataException("The selected JSON file does not contain any RadeonGuard or AMD XML-converted fan profiles.");
                }
                candidates.Add(convertedXmlFan);
            }
        }

        if (candidates.Count == 0)
        {
            throw new InvalidDataException("The selected JSON file contains zero fan profiles.");
        }

        var existingFans = await _repo.GetFanProfilesAsync(ct).ConfigureAwait(false);
        var normalizedList = new List<FanProfile>(candidates.Count);
        foreach (var rawFan in candidates)
        {
            bool sourceHasNoId = string.IsNullOrWhiteSpace(rawFan.FanProfileId);
            bool collidesWithDefault = rawFan.IsDefault ||
                                       string.Equals(rawFan.FanProfileId, RadeonGuardRepository.DefaultFanProfileId, StringComparison.OrdinalIgnoreCase);

            string desiredName = collidesWithDefault && string.Equals(rawFan.Name, "Default Fan Curve", StringComparison.OrdinalIgnoreCase)
                ? "Imported Fan Curve"
                : (string.IsNullOrWhiteSpace(rawFan.Name) ? "Imported Fan Curve" : rawFan.Name.Trim());

            FanProfile? existingByName = existingFans.FirstOrDefault(f =>
                !f.IsDefault &&
                string.Equals(f.Name, desiredName, StringComparison.OrdinalIgnoreCase));

            string resolvedId;
            if (!sourceHasNoId && !collidesWithDefault)
            {
                resolvedId = rawFan.FanProfileId;
                // Avoid UNIQUE(Name) collision if a different FanProfileId already owns desiredName
                if (existingFans.Any(f => !string.Equals(f.FanProfileId, resolvedId, StringComparison.OrdinalIgnoreCase) &&
                                          string.Equals(f.Name, desiredName, StringComparison.OrdinalIgnoreCase)))
                {
                    desiredName = MakeUniqueFanName(desiredName, existingFans, normalizedList);
                }
            }
            else if (sourceHasNoId && !collidesWithDefault && existingByName is not null)
            {
                // Re-importing external XML / XML-JSON updates the matching custom profile cleanly
                resolvedId = existingByName.FanProfileId;
            }
            else
            {
                resolvedId = Guid.NewGuid().ToString("B");
                if (existingFans.Any(f => string.Equals(f.Name, desiredName, StringComparison.OrdinalIgnoreCase)) ||
                    normalizedList.Any(f => string.Equals(f.Name, desiredName, StringComparison.OrdinalIgnoreCase)))
                {
                    desiredName = MakeUniqueFanName(desiredName, existingFans, normalizedList);
                }
            }

            var normalized = rawFan with
            {
                FanProfileId = resolvedId,
                Name = desiredName,
                GpuHardwareKey = !string.IsNullOrWhiteSpace(targetGpuHardwareKey)
                    ? targetGpuHardwareKey
                    : (string.IsNullOrWhiteSpace(rawFan.GpuHardwareKey) ? "UNKNOWN_GPU" : rawFan.GpuHardwareKey),
                IsDefault = false
            };

            var val = ProfileValidator.ValidateFanProfile(normalized, capabilities);
            if (!val.IsValid)
            {
                throw new InvalidDataException($"Imported fan profile '{normalized.Name}' failed validation: {string.Join(" ", val.Errors)}");
            }

            normalizedList.Add(normalized);
        }

        var saved = new List<FanProfile>(normalizedList.Count);
        foreach (var fan in normalizedList)
        {
            saved.Add(await _repo.SaveFanProfileAsync(fan, "FanProfileImport", ct).ConfigureAwait(false));
        }

        return saved;
    }

    private static string MakeUniqueFanName(string baseName, IReadOnlyList<FanProfile> existing, IReadOnlyList<FanProfile> pending)
    {
        for (int n = 2; n < 100; n++)
        {
            string candidate = $"{baseName} ({n})";
            if (!existing.Any(f => string.Equals(f.Name, candidate, StringComparison.OrdinalIgnoreCase)) &&
                !pending.Any(f => string.Equals(f.Name, candidate, StringComparison.OrdinalIgnoreCase)))
            {
                return candidate;
            }
        }
        return $"{baseName} ({Guid.NewGuid().ToString("N")[..4]})";
    }

    private static FanProfile? TryExtractWattManFanFromXml(string xmlText, string profileName, string gpuHardwareKey)
    {
        var doc = XDocument.Parse(xmlText);
        var features = doc.Descendants("FEATURE").ToList();
        var fanFeature = features.FirstOrDefault(f => string.Equals((string?)f.Attribute("ID"), "22", StringComparison.OrdinalIgnoreCase));
        if (fanFeature is null) return null;

        var states = new Dictionary<int, int>();
        foreach (var st in fanFeature.Descendants("STATE"))
        {
            if (int.TryParse((string?)st.Attribute("ID"), out int sid) &&
                int.TryParse((string?)st.Attribute("Value"), out int val))
            {
                states[sid] = val;
            }
        }

        bool zeroRpm = true;
        var zeroFeature = features.FirstOrDefault(f => string.Equals((string?)f.Attribute("ID"), "23", StringComparison.OrdinalIgnoreCase));
        if (zeroFeature is not null)
        {
            bool enabledAttr = string.Equals((string?)zeroFeature.Attribute("Enabled"), "True", StringComparison.OrdinalIgnoreCase);
            var st0 = zeroFeature.Descendants("STATE").FirstOrDefault(s => (string?)s.Attribute("ID") == "0");
            if (enabledAttr && st0 is not null && int.TryParse((string?)st0.Attribute("Value"), out int zv))
            {
                zeroRpm = zv != 0;
            }
        }

        return BuildFanFromStateMap(states, zeroRpm, profileName, gpuHardwareKey);
    }

    private static FanProfile? TryExtractWattManFanFromJson(JsonElement root, string profileName, string gpuHardwareKey)
    {
        var features = new List<JsonElement>();
        CollectFeatureNodes(root, features);
        if (features.Count == 0) return null;

        JsonElement? fanFeature = null;
        JsonElement? zeroFeature = null;
        foreach (var feat in features)
        {
            string? id = GetAttr(feat, "ID");
            if (id == "22") fanFeature = feat;
            else if (id == "23") zeroFeature = feat;
        }
        if (fanFeature is null) return null;

        var states = new Dictionary<int, int>();
        foreach (var st in EnumerateStates(fanFeature.Value))
        {
            if (int.TryParse(GetAttr(st, "ID"), out int sid) &&
                int.TryParse(GetAttr(st, "Value"), out int val))
            {
                states[sid] = val;
            }
        }

        bool zeroRpm = true;
        if (zeroFeature is not null &&
            string.Equals(GetAttr(zeroFeature.Value, "Enabled"), "True", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var st in EnumerateStates(zeroFeature.Value))
            {
                if (GetAttr(st, "ID") == "0" && int.TryParse(GetAttr(st, "Value"), out int zv))
                {
                    zeroRpm = zv != 0;
                    break;
                }
            }
        }

        return BuildFanFromStateMap(states, zeroRpm, profileName, gpuHardwareKey);
    }

    private static FanProfile? BuildFanFromStateMap(Dictionary<int, int> states, bool zeroRpm, string profileName, string gpuHardwareKey)
    {
        var points = new List<FanCurvePoint>(5);
        for (int i = 0; i < 5; i++)
        {
            if (states.TryGetValue(i * 2, out int temp) && states.TryGetValue(i * 2 + 1, out int speed))
            {
                points.Add(new FanCurvePoint(temp, speed));
            }
        }
        if (points.Count != 5) return null;

        return new FanProfile
        {
            FanProfileId = string.Empty,
            GpuHardwareKey = string.IsNullOrWhiteSpace(gpuHardwareKey) ? "UNKNOWN_GPU" : gpuHardwareKey,
            Name = profileName,
            ZeroRpmEnabled = zeroRpm,
            Points = points
        };
    }

    private static void CollectFeatureNodes(JsonElement el, List<JsonElement> output)
    {
        if (el.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in el.EnumerateObject())
            {
                if (string.Equals(prop.Name, "FEATURE", StringComparison.OrdinalIgnoreCase))
                {
                    if (prop.Value.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in prop.Value.EnumerateArray())
                            if (item.ValueKind == JsonValueKind.Object) output.Add(item);
                    }
                    else if (prop.Value.ValueKind == JsonValueKind.Object)
                    {
                        output.Add(prop.Value);
                    }
                }
                else
                {
                    CollectFeatureNodes(prop.Value, output);
                }
            }
        }
        else if (el.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in el.EnumerateArray()) CollectFeatureNodes(item, output);
        }
    }

    private static IEnumerable<JsonElement> EnumerateStates(JsonElement featureObj)
    {
        if (!TryGetPropertyCaseInsensitive(featureObj, "STATES", out var statesObj) ||
            statesObj.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }
        if (!TryGetPropertyCaseInsensitive(statesObj, "STATE", out var stateVal))
        {
            yield break;
        }
        if (stateVal.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in stateVal.EnumerateArray())
                if (s.ValueKind == JsonValueKind.Object) yield return s;
        }
        else if (stateVal.ValueKind == JsonValueKind.Object)
        {
            yield return stateVal;
        }
    }

    private static string? GetAttr(JsonElement el, string attrName)
    {
        foreach (string candidate in new[] { "-" + attrName, "@" + attrName, attrName })
        {
            if (TryGetPropertyCaseInsensitive(el, candidate, out var val))
            {
                return val.ValueKind switch
                {
                    JsonValueKind.String => val.GetString(),
                    JsonValueKind.Number => val.GetRawText(),
                    JsonValueKind.True => "True",
                    JsonValueKind.False => "False",
                    _ => null
                };
            }
        }
        return null;
    }

    private static bool TryGetPropertyCaseInsensitive(JsonElement el, string name, out JsonElement value)
    {
        if (el.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in el.EnumerateObject())
            {
                if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = prop.Value;
                    return true;
                }
            }
        }
        value = default;
        return false;
    }
}
