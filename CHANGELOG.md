# Changelog

All notable changes to Better-Adrenalin (RadeonGuard) are documented in this file.

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
