using System.Text.Json;
using RadeonGuard.Persistence;

namespace RadeonGuard.Diagnostics;

public sealed record ApplyBreadcrumb(
    string TuningProfileId,
    string TuningProfileName,
    string FanProfileId,
    bool HasClockOrVoltageChanges,
    DateTimeOffset StartedAtUtc);

/// <summary>
/// Writes an atomic pre-apply breadcrumb before touching GPU clocks/voltages.
/// If the PC or driver crashes mid-apply, the next startup detects the uncleared breadcrumb
/// and quarantines ONLY the risky clock/voltage profile while preserving all saved profiles and fan curves.
/// </summary>
public sealed class CrashBreadcrumbManager
{
    private readonly string _breadcrumbPath;

    public CrashBreadcrumbManager(string? breadcrumbPath = null)
    {
        _breadcrumbPath = breadcrumbPath ?? StoragePaths.GetBreadcrumbPath();
    }

    public void WritePreApply(ApplyBreadcrumb breadcrumb)
    {
        var dir = Path.GetDirectoryName(_breadcrumbPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        string tmp = _breadcrumbPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(breadcrumb));
        File.Move(tmp, _breadcrumbPath, overwrite: true);
    }

    public void Clear()
    {
        if (File.Exists(_breadcrumbPath))
        {
            File.Delete(_breadcrumbPath);
        }
    }

    public async Task<ApplyBreadcrumb?> CheckAndHandleCrashRecoveryAsync(RadeonGuardRepository repo, CancellationToken ct = default)
    {
        if (!File.Exists(_breadcrumbPath)) return null;

        ApplyBreadcrumb? crumb = null;
        try
        {
            string json = await File.ReadAllTextAsync(_breadcrumbPath, ct).ConfigureAwait(false);
            crumb = JsonSerializer.Deserialize<ApplyBreadcrumb>(json);
        }
        catch
        {
            // Corrupt breadcrumb file; clear safely.
        }
        finally
        {
            Clear();
        }

        if (crumb is not null && crumb.HasClockOrVoltageChanges)
        {
            var profile = await repo.GetTuningProfileByIdAsync(crumb.TuningProfileId, ct).ConfigureAwait(false);
            if (profile is not null && !profile.IsGlobalDefault && !profile.IsQuarantined)
            {
                var quarantined = profile with
                {
                    IsQuarantined = true,
                    QuarantineReason = $"Unclean shutdown / driver crash detected while applying '{profile.Name}' at {crumb.StartedAtUtc:u}."
                };
                await repo.SaveTuningProfileAsync(quarantined, "AutoCrashQuarantine", ct).ConfigureAwait(false);
            }
        }

        return crumb;
    }
}
