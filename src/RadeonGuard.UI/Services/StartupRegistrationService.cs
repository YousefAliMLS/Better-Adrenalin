using Microsoft.Win32;

namespace RadeonGuard.UI.Services;

/// <summary>
/// Manages RadeonGuard's own per-user Windows logon startup entry (HKCU\...\Run)
/// so RadeonGuard.Guardian restores the active sticky GPU/fan profile on PC reboot.
/// Never touches AMD Adrenalin keys or Windows system services.
/// </summary>
public static class StartupRegistrationService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "RadeonGuardGuardian";

    public static bool IsGuardianAutoStartEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            var val = key?.GetValue(ValueName) as string;
            return !string.IsNullOrWhiteSpace(val);
        }
        catch
        {
            return false;
        }
    }

    public static bool SetGuardianAutoStart(bool enabled, string? guardianExePath = null)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null) return false;

            if (enabled)
            {
                string? exe = guardianExePath ?? GuardianIpcClient.ResolveGuardianExecutablePath();
                if (string.IsNullOrWhiteSpace(exe)) return false;
                key.SetValue(ValueName, $"\"{exe}\"", RegistryValueKind.String);
                return true;
            }

            key.DeleteValue(ValueName, throwOnMissingValue: false);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
