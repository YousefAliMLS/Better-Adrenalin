# Architecture Overview

Better-Adrenalin (RadeonGuard) is designed around strict separation of concerns so a UI issue, unclean shutdown, or game crash cannot corrupt saved profiles or lock out fan control.

## Layers

1. **Native Hardware Bridge (`RadeonGuard.AdlxBridge.dll`)**
   - Written in C++20 against the official AMD ADLX SDK headers (`GPUOpen-LibrariesAndSDKs/ADLX`).
   - Uses `CompatibleAdlxHelper` to negotiate `min(ADLX_FULL_VERSION, runtimeDllVersion)`, allowing binaries compiled against newer ADLX headers (such as 2.0 SDK headers) to run reliably against driver-installed `amdadlx64.dll` 1.4.x or newer.
   - Exposes fixed C ABI structs (`__stdcall`) so managed C# code communicates without fragile C++ COM vtable assumptions.

2. **Core Hardware & Validation (`RadeonGuard.Core`)**
   - Defines immutable domain records (`FanProfile`, `TuningProfile`, `GameProfile`, `ThreeDSettings`).
   - Serializes all ADLX calls onto a dedicated worker thread in `AdlxHardwareAdapter`.
   - Enforces two-stage validation (`ProfileValidator`) prior to persistence and again prior to hardware writes.
   - Performs read-after-write verification (~150 ms settle delay) to distinguish between `SAVED` (persisted in SQLite), `APPLIED` (driver call succeeded), and `VERIFIED` (hardware readback matched target).

3. **Persistence & Recovery (`RadeonGuard.Persistence`)**
   - SQLite database at `%LOCALAPPDATA%\RadeonGuard\radeonguard.db` configured with `PRAGMA journal_mode = WAL`, `PRAGMA synchronous = FULL`, and `PRAGMA foreign_keys = ON`.
   - Automatic schema versioning via `DatabaseSchema.MigrateSchema` (v1 -> v2 adds `ZeroRpmOverride` non-destructively via `ALTER TABLE`).
   - Every profile save inserts a full snapshot into `PROFILE_REVISIONS` for one-click rollback.
   - `BackupManager` uses `VACUUM INTO` with a sidecar `.sha256` file and automatically restores the latest valid backup if `PRAGMA integrity_check` fails on startup.

4. **Background Guardian (`RadeonGuard.Guardian.exe`)**
   - Single-instance background daemon listening on `\\.\pipe\RadeonGuard.IPC` (ACL restricted to current user SID and `BUILTIN\Administrators`).
   - Polls metrics every 500 ms, checks game processes every 1 s, and verifies hardware synchronization against expected managed state every 3 s.
   - Retries ADLX initialization automatically if started at Windows logon before the display driver finishes coming online, then triggers `DelayedStartupRestore`.
   - Applies per-game `ZeroRpmOverrideMode` (`InheritFromFanProfile`, `ForceEnabled`, `ForceDisabled`) on game launch and restores `ActiveGlobalFanId` when the game closes.

5. **WPF Desktop Client (`RadeonGuard.UI.exe`)**
   - Built on .NET 8 WPF + `WPF-UI` + `CommunityToolkit.Mvvm`.
   - Connects to Guardian via Named Pipe IPC (launching `RadeonGuard.Guardian.exe` automatically if offline).
   - Registers per-user Windows logon startup (`HKCU\Software\Microsoft\Windows\CurrentVersion\Run\RadeonGuardGuardian`) when enabled via the UI toggle.
