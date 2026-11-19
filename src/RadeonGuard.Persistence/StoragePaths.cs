namespace RadeonGuard.Persistence;

public static class StoragePaths
{
    public static string DefaultDataRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RadeonGuard");

    public static string GetDatabasePath(string? dataRoot = null) =>
        Path.Combine(dataRoot ?? DefaultDataRoot, "radeonguard.db");

    public static string GetBackupsDirectory(string? dataRoot = null) =>
        Path.Combine(dataRoot ?? DefaultDataRoot, "backups");

    public static string GetLogsDirectory(string? dataRoot = null) =>
        Path.Combine(dataRoot ?? DefaultDataRoot, "logs");

    public static string GetBreadcrumbPath(string? dataRoot = null) =>
        Path.Combine(dataRoot ?? DefaultDataRoot, "apply_breadcrumb.json");
}
