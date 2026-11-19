# Contributing to Better-Adrenalin

Thank you for your interest in contributing to Better-Adrenalin (RadeonGuard). Because this software interacts directly with GPU hardware tuning APIs, reliability and hardware safety take top priority.

## Core Engineering Rules

1. Never bypass `ProfileValidator` bounds checks when writing tuning or fan settings to ADLX.
2. Never ship changes that modify or delete AMD Adrenalin files, AMD driver files, Windows system services, or external application registries.
3. Separate hardware logic behind `IGpuHardwareAdapter` so all orchestration, persistence, and drift tests run deterministically with `MockGpuHardwareAdapter` on CI runners without a physical AMD GPU.
4. Keep SQLite schema changes idempotent via `DatabaseSchema.MigrateSchema` so existing user databases upgrade without data loss.
5. Maintain documentation integrity and avoid speculative hardware claims not verified against real ADLX responses.

## Local Development Setup

### Prerequisites
- Windows 10/11 (x64)
- Visual Studio 2022 with Desktop Development with C++ (`v143` toolset)
- .NET 8 SDK
- Git

### Building and Testing

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1 -Configuration Release
```

`build.ps1` automatically clones AMD's official `GPUOpen-LibrariesAndSDKs/ADLX` SDK into `third_party\ADLX` if absent, compiles `RadeonGuard.AdlxBridge.dll`, builds `RadeonGuard.sln`, runs all tests in `tests\RadeonGuard.Tests`, and stages runnable binaries into `dist\Release`.

If you have an AMD Radeon GPU installed, you can run a safe read-only hardware check without writing settings:

```powershell
.\dist\Release\RadeonGuard.Guardian.exe --probe
```

## Pull Request Checklist

- All existing and new xUnit tests pass (`dotnet test tests\RadeonGuard.Tests\RadeonGuard.Tests.csproj -c Release`).
- Release build compiles with zero errors (`.\build.ps1 -Configuration Release`).
- No hardcoded user paths, secrets, local `.db` files, or build binaries are staged in Git.
- Hardware tuning changes document the ADLX interface behavior and fallback behavior when unsupported.
