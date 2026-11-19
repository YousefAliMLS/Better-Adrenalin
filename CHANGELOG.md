# Changelog

All notable changes to Better-Adrenalin (RadeonGuard) are documented in this file.

## [1.0.1] - 2026-11-19

### Added
- AMD WattMan XML (`.xml`) and XML-converted-to-JSON export/import support across `ProfilePortabilityService`, **Fan Control** (`Export Selected (.json / .xml)`, `Import & Apply (.json / .xml)`), and **History & Backups** (`Import (.json / .xml)`).
- `(None - Use Active Global Fan Profile)` option in **Game Profiles -> Optional Fan Profile Override** so per-game fan overrides can be cleared cleanly.
- Single-instance mutex and tray activation (`Local\RadeonGuard.UI.Singleton` / `Local\RadeonGuard.UI.ActivateEvent`) so re-launching `RadeonGuard.UI.exe` focuses the existing window instead of spawning duplicate tray instances.

### Fixed
- **Hung/Zombie Game Process Detection:** Updated `GameProcessTracker` and `ProfileOrchestrator` to track visible top-level windows (`EnumWindows` / `IsWindowVisible` / `MainWindowHandle`) and process startup age, releasing game locks and restoring global Zero RPM when games such as Fortnite leave a windowless teardown process running after exit.
- **Idle Stuck-ZeroRPM Watchdog:** Added automatic Zero RPM re-arming in `ProfileOrchestrator.VerifyAndGuardHardwareTickAsync` if fans continue spinning above 100 RPM during cool idle (`Edge <= 45 C`, `Hotspot <= 48 C`, `Usage <= 10%`) while Zero RPM is enabled.
- **Duplicate Name on Re-Import & Banner Visibility:** Re-importing external XML/XML-JSON profiles updates matching custom profiles or disambiguates names cleanly instead of failing with a SQLite `UNIQUE` constraint error, and `FanValidationMessage` now displays inside the top header card in **Fan Control**.
- **Guardian Launch on Game Save:** `SaveGameProfileAsync` now ensures `RadeonGuard.Guardian.exe` is running before dispatching `RefreshActiveGame`.

### Tests
- Expanded xUnit suite from 16 to 19 passing tests (`StuckFansAtCoolIdle_WhenZeroRpmEnabled_AutomaticallyRearmsZeroRpmAndStopsFans`, `ZombieWindowlessProcessAfterGameClose_OrHungStartupProcess_DoesNotHoldGameProfile`, and `ExportImportWattManXml_AndXmlConvertedJson_Succeeds`).

## [1.0.0] - 2026-11-19

### Added
- Native C++20 ADLX bridge (`RadeonGuard.AdlxBridge.dll`) with negotiated ADLX 1.4/2.0+ initialization, live telemetry, hardware capability discovery, manual GFX/VRAM/Power/Fan tuning, 3D settings, and external tuning change callbacks.
- Decoupled background daemon (`RadeonGuard.Guardian.exe`) with ACL-secured Named Pipe IPC (`\\.\pipe\RadeonGuard.IPC`), 3-second drift verification watchdog, power-resume restoration, and delayed ADLX initialization retry on cold boot.
- Permanent SQLite configuration storage (`WAL`, `synchronous = FULL`, `foreign_keys = ON`) with automatic schema migration, immutable `PROFILE_REVISIONS` rollback history, and SHA-256 verified `VACUUM INTO` backups with automatic corruption recovery.
- Modern Windows 11 WPF desktop client (`RadeonGuard.UI.exe`) with real-time Dashboard sparklines, interactive 5-point Hotspot fan curve editor, GPU Tuning, Game Profiles, Revision History & Backups, and Conflict Diagnostics.
- Per-game **Zero RPM Mode** override (`InheritFromFanProfile`, `ForceEnabled`, `ForceDisabled`) to prevent fan stop/start cycling in lighter games while restoring the global Zero RPM state on game exit.
- Fan profile creation, duplication, single/multi-profile JSON export and SHA-256 verified **Import & Apply**, plus sticky per-user Windows logon startup registration (`HKCU\Software\Microsoft\Windows\CurrentVersion\Run`) so active profiles persist across PC restarts.
- Read-only importer for AMD Adrenalin `gmdb.blb` game and OverDrive8 fan curve data with built-in and custom non-game denylist filtering (such as `python.exe`, browsers, and office apps).
- Automated xUnit test suite covering validation, persistence/corruption recovery, schema v1-to-v2 migration, fan JSON export/import verification, Adrenalin import filtering, Alt-Tab game locking, drift auto-recovery, crash quarantine, and IPC round-trips.
