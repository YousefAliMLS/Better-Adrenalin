using System.Text.Json;
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
        var bundle = JsonSerializer.Deserialize<ProfileBundleDto>(raw)
                     ?? throw new InvalidDataException("Invalid RadeonGuard bundle JSON.");

        string expectedHash = RadeonGuardRepository.ComputeSha256(
            JsonSerializer.Serialize(new { fans = bundle.FanProfiles, tunings = bundle.TuningProfiles, games = bundle.GameProfiles }));

        if (!string.IsNullOrEmpty(bundle.BundleSha256) &&
            !string.Equals(expectedHash, bundle.BundleSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Bundle SHA-256 verification failed. The file may be corrupted or tampered with.");
        }

        int f = 0, t = 0, g = 0;
        foreach (var fan in bundle.FanProfiles)
        {
            await _repo.SaveFanProfileAsync(fan, "BundleImport", ct).ConfigureAwait(false);
            f++;
        }
        foreach (var tuning in bundle.TuningProfiles)
        {
            await _repo.SaveTuningProfileAsync(tuning, "BundleImport", ct).ConfigureAwait(false);
            t++;
        }
        foreach (var game in bundle.GameProfiles)
        {
            await _repo.SaveGameProfileAsync(game, "BundleImport", ct).ConfigureAwait(false);
            g++;
        }
        return (f, t, g);
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
        List<FanProfile> candidates = new();

        using (var doc = JsonDocument.Parse(raw))
        {
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
                throw new InvalidDataException("The selected JSON file does not contain any fan profiles.");
            }
        }

        if (candidates.Count == 0)
        {
            throw new InvalidDataException("The selected JSON file contains zero fan profiles.");
        }

        var normalizedList = new List<FanProfile>(candidates.Count);
        foreach (var rawFan in candidates)
        {
            bool collidesWithDefault = rawFan.IsDefault ||
                                       string.Equals(rawFan.FanProfileId, RadeonGuardRepository.DefaultFanProfileId, StringComparison.OrdinalIgnoreCase);

            var normalized = rawFan with
            {
                FanProfileId = string.IsNullOrWhiteSpace(rawFan.FanProfileId) || collidesWithDefault
                    ? Guid.NewGuid().ToString("B")
                    : rawFan.FanProfileId,
                Name = collidesWithDefault && string.Equals(rawFan.Name, "Default Fan Curve", StringComparison.OrdinalIgnoreCase)
                    ? "Imported Fan Curve"
                    : (string.IsNullOrWhiteSpace(rawFan.Name) ? "Imported Fan Curve" : rawFan.Name.Trim()),
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
}
