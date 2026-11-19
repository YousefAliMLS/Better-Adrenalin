# Third-Party Notices

Better-Adrenalin (RadeonGuard) uses the following open-source libraries and SDKs:

1. **AMD Device Library eXtra (ADLX SDK)**
   - Source: https://github.com/GPUOpen-LibrariesAndSDKs/ADLX
   - License: MIT License (Advanced Micro Devices, Inc.)
   - Usage: Native C++20 headers and ADLXHelper wrapper compiled into `RadeonGuard.AdlxBridge.dll` to communicate with the system-installed `amdadlx64.dll`.

2. **WPF-UI** (`Wpf.Ui` 3.0.5)
   - Source: https://github.com/lepoco/wpfui
   - License: MIT License
   - Usage: Windows 11 Fluent desktop interface components.

3. **CommunityToolkit.Mvvm** (8.3.2)
   - Source: https://github.com/CommunityToolkit/dotnet
   - License: MIT License
   - Usage: MVVM observable properties and relay commands in `RadeonGuard.UI`.

4. **Microsoft.Data.Sqlite** (8.0.11)
   - Source: https://github.com/dotnet/efcore
   - License: MIT License
   - Usage: Transactional SQLite WAL storage and `VACUUM INTO` backups.

5. **Serilog & Serilog.Sinks.File / Serilog.Formatting.Compact**
   - Source: https://github.com/serilog/serilog
   - License: Apache License 2.0
   - Usage: Structured flight-recorder diagnostic logs.

6. **xUnit & Microsoft.NET.Test.Sdk**
   - Source: https://github.com/xunit/xunit
   - License: Apache License 2.0 / MIT License
   - Usage: Automated unit and fault-recovery test suite (`tests/RadeonGuard.Tests`).
