# Better-Adrenalin (RadeonGuard)

A standalone, reliable Windows desktop companion and alternative to AMD Software: Adrenalin Edition for GPU monitoring, manual tuning, permanent custom fan curves, and deterministic per-game profile switching.

Better-Adrenalin (`RadeonGuard.UI.exe` and `RadeonGuard.Guardian.exe`) communicates strictly through AMD's documented user-mode ADLX SDK (`amdadlx64.dll`) while storing all configuration outside Adrenalin's volatile settings files in an ACID SQLite WAL database.

---

## Why This Project Exists

Users running AMD Software: Adrenalin Edition frequently encounter configuration loss and unstable fan behavior:

1. **Custom Fan Curves Disappearing:** Fan profiles silently reset to stock defaults after Windows restarts, sleep/wake cycles, driver resets, or background Adrenalin updates.
2. **Game-Specific Profiles Reverting:** Game profiles reset or fail to switch reliably, or non-game executables (such as `python.exe`, browsers, or office apps) trigger profile switching.
3. **Zero RPM Fan Cycling on Light Games:** Leaving Zero RPM enabled globally causes fans on GPUs like the RX 6800 XT to repeatedly spin up near 60 C (often jumping abruptly), cool down, turn off, and repeat the cycle during light workloads.
4. **Silent Failure After Crashes:** Risky clock or undervolt changes can hang the driver without isolating what caused the crash, risking loss of fan curves on recovery.

Better-Adrenalin separates configuration storage, hardware safety validation, background monitoring, and UI rendering so your saved profiles cannot be silently erased by driver restarts or UI crashes.

---

## Verified Features

### Live Hardware Dashboard
- Displays GPU model, hardware key, VRAM size/type, PCIe link width, driver version, and VBIOS metadata.
- Streams real-time Edge and Hotspot (Junction) temperatures, GPU utilization, Core clock, VRAM clock, dedicated VRAM usage, board power, core voltage, and fan RPM.
- Plots rolling telemetry sparklines and displays ADLX hardware-reported tuning limits (`VERIFIED`, `APPLIED`, `DRIFT_DETECTED`, `RECOVERING`, `READ_ONLY_MONITORING`).

### Permanent Fan Control & Portability
- **Interactive 5-Point Fan Curve Editor:** Drag points or type exact Temperature (C) and Fan Speed (%) values evaluated against GPU Hotspot (Junction) temperature.
- **Zero RPM & Minimum Acoustic Limit:** Toggle Zero RPM and configure minimum acoustic frequency limits where supported by the GPU.
- **Sticky Global Profile Across Reboots:** Applying a fan profile persists `ActiveGlobalFanId` in SQLite and registers `RadeonGuard.Guardian.exe` under the current user's Windows startup entry (`HKCU\Software\Microsoft\Windows\CurrentVersion\Run\RadeonGuardGuardian`, toggleable in Fan Control) so your chosen curve automatically restores after Windows restarts.
- **Create, Duplicate, Export & Import + Apply:** Create or duplicate fan profiles, export single profiles to SHA-256 checksummed JSON or AMD WattMan XML (`.xml`), export all fan profiles to JSON, and import and apply RadeonGuard JSON, AMD WattMan XML, or XML-converted JSON profiles directly to the GPU with hardware validation.

### Deterministic Game Profiles & Per-Game Zero RPM Control
- **Alt-Tab Immune Windowed Process Tracking:** Tracks `PrimaryRenderer` executables with active top-level window detection (`EnumWindows` / `MainWindowHandle`), releasing game profiles if a closed game (such as Fortnite/Unreal Engine with EasyAntiCheat) leaves a hung windowless background process behind.
- **Non-Game Denylist:** Built-in exclusion list (`python.exe`, `chrome.exe`, `msedge.exe`, `discord.exe`, `obs64.exe`, Office apps, terminals) plus custom user denylist entries prevent background tools from hijacking GPU profiles.
- **Per-Game Zero RPM Override & Clearable Fan Override:** Set each game profile independently to `Inherit from Fan Profile`, `Force Enabled (Zero RPM On)`, or `Force Disabled (Fans Always Spin)` to eliminate repetitive fan start/stop cycling on lighter titles, and optionally clear per-game fan overrides back to `(None - Use Active Global Fan Profile)`.
- **Launcher Discovery & Read-Only Adrenalin Import:** Scans Steam, Epic Games, and Ubisoft Connect installs, and optionally reads Adrenalin's `gmdb.blb` read-only to recover saved OverDrive8 fan curves and game entries while filtering false positives.

### Safe GPU Tuning & 3D Settings
- **Two-Stage Validation:** Core Min/Max frequencies, core voltage, VRAM frequency/timing, and power limit delta are checked against live ADLX hardware ranges before saving and again before applying.
- **Pre-Apply Crash Quarantine:** Writes a breadcrumb prior to applying clock, voltage, VRAM, or power changes; if an unclean shutdown happens mid-apply, the risky tuning profile is automatically quarantined on startup while all fan curves remain intact.
- **Supported 3D Settings:** Configure AMD Anti-Lag, Radeon Chill (Min/Max FPS), Image Sharpening, Enhanced Sync, and Frame Rate Target Control (FRTC).

### Reliability, Drift Watchdog & Recovery
- **3-Second Drift & Stuck-ZeroRPM Watchdog:** Periodically reads back live hardware fan and tuning state from ADLX, re-applies your active profile if external software resets it (`AutoReapply` or `AlertOnly` policy), and automatically re-arms Zero RPM if fans remain stuck spinning above 100 RPM during cool idle (`Edge <= 45 C`, `Hotspot <= 48 C`, `Usage <= 10%`).
- **Power Resume & Boot Retry:** Restores active profiles on Windows sleep/resume events and automatically retries ADLX initialization if the Guardian daemon starts before the GPU driver finishes initializing after boot.
- **Immutable Revision History & SHA-256 Backups:** Every save writes an immutable snapshot in `PROFILE_REVISIONS` for one-click rollback, alongside atomic SQLite `VACUUM INTO` backups verified with SHA-256 checksums and automatic recovery if the primary database is corrupted.
- **Diagnostics & Audit Log:** Detects conflicting GPU utilities (such as AMD Adrenalin or MSI Afterburner), Windows Fast Startup (`HiberbootEnabled`), AMD service statuses, and recent Windows Event ID 4101 display driver TDR resets.

---

## System Requirements & Compatibility

- **Operating System:** Windows 10 or Windows 11 (64-bit / x64)
- **GPU & Drivers:** AMD Radeon GPU with AMD Software drivers providing `amdadlx64.dll` (ADLX 1.4 or newer)
  - Tested directly on: **AMD Radeon RX 6800 XT** running **Windows 11**, AMD Driver `25.10.45.16-261009a-205305C-AMD-Software-Adrenalin-Edition` (Software `26.10.1`), and `amdadlx64.dll` (`ADLX 1.4`).
  - Older pre-ADLX Radeon GPUs or GPUs where the driver reports `IsSupported* = false` for a tuning domain will have those controls restricted or disabled automatically.
- **Runtime (Portable build only):** The standalone release archive includes the .NET 8 runtime; the smaller portable archive requires the [.NET 8 Desktop Runtime (x64)](https://dotnet.microsoft.com/en-us/download/dotnet/8.0).
- **Building from Source:** Visual Studio 2022 with Desktop Development with C++ (`v143`), .NET 8 SDK, and Git.

---

## Installation & Quick Start

### Option 1: Download Prebuilt Release (Recommended)

1. Go to the [Releases](https://github.com/YousefAliMLS/Better-Adrenalin/releases) page.
2. Download one of the release archives:
   - `Better-Adrenalin-v1.0.1-win-x64-standalone.zip` (recommended; self-contained, no runtime install required)
   - `Better-Adrenalin-v1.0.1-win-x64-portable.zip` (smaller download if .NET 8 Desktop Runtime x64 is already installed)
3. Extract the archive to a permanent folder (for example, `C:\Tools\Better-Adrenalin\`) rather than running directly from a temporary folder, especially if you enable **Keep Applied After PC Restart**.
4. Launch `RadeonGuard.UI.exe`. It automatically starts `RadeonGuard.Guardian.exe` in the background and connects via Named Pipe IPC.

> **Recommendation:** Keep AMD Adrenalin's Performance Tuning at Default so it does not compete with RadeonGuard. Better-Adrenalin never modifies or deletes your existing AMD Adrenalin installation.

### Option 2: Build from Source

```powershell
git clone https://github.com/YousefAliMLS/Better-Adrenalin.git
cd Better-Adrenalin
powershell -ExecutionPolicy Bypass -File .\build.ps1 -Configuration Release
```

`build.ps1` performs the following steps automatically:
1. Clones AMD's official MIT-licensed `GPUOpen-LibrariesAndSDKs/ADLX` SDK into `third_party\ADLX` if not already present.
2. Compiles the native C++20 wrapper `RadeonGuard.AdlxBridge.dll` via Visual Studio MSBuild (`v143`).
3. Builds `RadeonGuard.sln` in `Release` configuration.
4. Executes the xUnit test suite (`tests/RadeonGuard.Tests`).
5. Stages runnable binaries into `dist\Release\`.

To run a non-destructive hardware capability probe on your GPU:

```powershell
.\dist\Release\RadeonGuard.Guardian.exe --probe
```

---

## Basic Usage Guide

1. **Create and Stick a Fan Profile:**
   - Open the **Fan Control** tab.
   - Click **+ New** (or **Duplicate**), adjust the 5-point curve and Zero RPM setting, and click **Save & Apply to GPU**.
   - Ensure **Keep Applied After PC Restart** is enabled so `RadeonGuard.Guardian.exe` automatically re-applies your curve at Windows logon.
2. **Export or Import Fan Profiles:**
   - Use **Export Selected (.json / .xml)** or **Export All (.json)** in **Fan Control** to save SHA-256 verified JSON or AMD WattMan XML profile files.
   - Use **Import & Apply (.json / .xml)** to validate, save, and immediately activate an exported fan profile, AMD WattMan XML, XML-converted JSON file, or full bundle.
3. **Configure Game Profiles and Per-Game Zero RPM:**
   - Open **Game Profiles**, click **Scan Steam / Epic / Ubisoft** or **Add Game Exe...** (or **Import Adrenalin gmdb.blb**).
   - Assign a Tuning Profile, optional Fan Profile Override, and **Per-Game Zero RPM Mode** (set to **Force Disabled (Fans Always Spin)** on light games to stop fan cycling).
   - Click **Save Game Profile**. Changes apply immediately even if the game is already running.

---

## Architecture Overview

```text
+---------------------------+        Named Pipe IPC        +------------------------------+
|     RadeonGuard.UI.exe    |  <------------------------>  |  RadeonGuard.Guardian.exe    |
|  WPF Fluent UI + MVVM     |    (\\.\pipe\RadeonGuard.IPC)|  Watchdog, Game Tracker,     |
+-------------+-------------+                              |  Boot Restore & ADLX Retry   |
              |                                            +--------------+---------------+
              |                                                           |
              v                                                           v
+---------------------------+                              +------------------------------+
|  RadeonGuard.Persistence  |                              |      RadeonGuard.Core        |
|  SQLite WAL + Backups     |                              |  ProfileValidator + P/Invoke |
+---------------------------+                              +--------------+---------------+
                                                                          |
                                                                          v
                                                           +------------------------------+
                                                           |  RadeonGuard.AdlxBridge.dll  |
                                                           |  C++20 <-> amdadlx64.dll     |
                                                           +------------------------------+
```

See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for module responsibilities, database schema design, and safety mechanisms.

---

## Project Structure

- `src/RadeonGuard.AdlxBridge/` - C++20 native DLL wrapping AMD ADLX with negotiated SDK version compatibility (`1.4` / `2.0+`).
- `src/RadeonGuard.Core/` - Domain models, `IGpuHardwareAdapter`, `ProfileValidator`, IPC contracts, and `AdlxHardwareAdapter`.
- `src/RadeonGuard.Persistence/` - SQLite WAL repository, automatic schema migrations, revision history, SHA-256 backups, JSON portability, and read-only `AdrenalinImporter`.
- `src/RadeonGuard.Diagnostics/` - Serilog flight recorder logs, crash breadcrumb quarantine manager, and system/TDR conflict scanner.
- `src/RadeonGuard.Guardian/` - Background daemon hosting Named Pipe IPC, telemetry polling, 3-second drift guard, and game process tracker.
- `src/RadeonGuard.UI/` - Windows 11 WPF desktop frontend with interactive fan curve canvas and live sparklines.
- `tests/RadeonGuard.Tests/` - xUnit unit and fault-injection test suite (validation, SQLite corruption recovery, schema migration, game switching, Zero RPM overrides, drift recovery, crash quarantine, IPC).

---

## Known Limitations

- Requires AMD's user-mode `amdadlx64.dll` installed by AMD Radeon drivers; GPUs not supported by ADLX cannot be tuned through this tool.
- GPU tuning bounds (frequency, voltage, VRAM, power, fan speed) are enforced strictly by the GPU's VBIOS/SMU via ADLX; Better-Adrenalin intentionally does not attempt to bypass driver/VBIOS safety limits.
- Some features present in full AMD Adrenalin (screen recording/ReLive, Radeon Overlay, streaming, and driver update installer) are outside the scope of this tool.

---

## Contributing & License

- Contributions are welcome. Please read [CONTRIBUTING.md](CONTRIBUTING.md) and [SECURITY.md](SECURITY.md) before submitting a pull request.
- See [CHANGELOG.md](CHANGELOG.md) for version history and [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) for third-party licenses.
- Licensed under the [MIT License](LICENSE).
