param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$SkipTests,
    [switch]$Package,
    [string]$Version = "1.0.0"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

# 1. Ensure AMD ADLX SDK headers are present
$adlxHeader = Join-Path $root "third_party\ADLX\SDK\ADLXHelper\Windows\Cpp\ADLXHelper.h"
if (-not (Test-Path $adlxHeader)) {
    Write-Host "==> Fetching AMD ADLX SDK into third_party\ADLX..." -ForegroundColor Cyan
    New-Item -ItemType Directory -Force -Path (Join-Path $root "third_party") | Out-Null
    & git clone --depth 1 https://github.com/GPUOpen-LibrariesAndSDKs/ADLX.git (Join-Path $root "third_party\ADLX")
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to clone GPUOpen-LibrariesAndSDKs/ADLX into third_party\ADLX."
    }
}

# 2. Locate Visual Studio 2022 MSBuild for native C++ bridge
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$msbuild = $null
if (Test-Path $vswhere) {
    $vsInstall = & $vswhere -latest -requires Microsoft.Component.MSBuild -property installationPath | Select-Object -First 1
    if ($vsInstall) {
        $candidate = Join-Path $vsInstall "MSBuild\Current\Bin\MSBuild.exe"
        if (Test-Path $candidate) { $msbuild = $candidate }
    }
}
if (-not $msbuild) {
    throw "Visual Studio 2022 MSBuild.exe with C++ v143 tools is required to build RadeonGuard.AdlxBridge."
}

Write-Host "==> Building RadeonGuard.AdlxBridge ($Configuration|x64)..." -ForegroundColor Cyan
& $msbuild (Join-Path $root "src\RadeonGuard.AdlxBridge\RadeonGuard.AdlxBridge.vcxproj") /p:Configuration=$Configuration /p:Platform=x64 /m /v:minimal
if ($LASTEXITCODE -ne 0) {
    throw "MSBuild failed for RadeonGuard.AdlxBridge with exit code $LASTEXITCODE."
}

Write-Host "==> Building RadeonGuard.sln ($Configuration)..." -ForegroundColor Cyan
& dotnet build (Join-Path $root "RadeonGuard.sln") -c $Configuration
if ($LASTEXITCODE -ne 0) {
    throw "dotnet build failed with exit code $LASTEXITCODE."
}

if (-not $SkipTests) {
    Write-Host "==> Running xUnit test suite..." -ForegroundColor Cyan
    & dotnet test (Join-Path $root "tests\RadeonGuard.Tests\RadeonGuard.Tests.csproj") -c $Configuration --no-build
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet test failed with exit code $LASTEXITCODE."
    }
}

$distDir = Join-Path $root "dist\$Configuration"
if (Test-Path $distDir) {
    Remove-Item -Path $distDir -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $distDir | Out-Null
Copy-Item -Path (Join-Path $root "src\RadeonGuard.UI\bin\$Configuration\net8.0-windows\*") -Destination $distDir -Recurse -Force
Copy-Item -Path (Join-Path $root "src\RadeonGuard.Guardian\bin\$Configuration\net8.0-windows\*") -Destination $distDir -Recurse -Force
Write-Host "==> Staged runnable app in $distDir" -ForegroundColor Green

if ($Package) {
    $releaseDir = Join-Path $root "artifacts\release"
    $standaloneDir = Join-Path $releaseDir "standalone-staging"
    if (Test-Path $releaseDir) {
        Remove-Item -Path $releaseDir -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $standaloneDir | Out-Null

    Write-Host "==> Publishing self-contained win-x64 release package..." -ForegroundColor Cyan
    & dotnet publish (Join-Path $root "src\RadeonGuard.UI\RadeonGuard.UI.csproj") -c $Configuration -r win-x64 --self-contained true -o $standaloneDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for RadeonGuard.UI." }

    & dotnet publish (Join-Path $root "src\RadeonGuard.Guardian\RadeonGuard.Guardian.csproj") -c $Configuration -r win-x64 --self-contained true -o $standaloneDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for RadeonGuard.Guardian." }

    Copy-Item -Path (Join-Path $root "LICENSE") -Destination $standaloneDir -Force -ErrorAction SilentlyContinue
    Copy-Item -Path (Join-Path $root "README.md") -Destination $standaloneDir -Force -ErrorAction SilentlyContinue
    Copy-Item -Path (Join-Path $root "THIRD_PARTY_NOTICES.md") -Destination $standaloneDir -Force -ErrorAction SilentlyContinue

    Copy-Item -Path (Join-Path $root "LICENSE") -Destination $distDir -Force -ErrorAction SilentlyContinue
    Copy-Item -Path (Join-Path $root "README.md") -Destination $distDir -Force -ErrorAction SilentlyContinue
    Copy-Item -Path (Join-Path $root "THIRD_PARTY_NOTICES.md") -Destination $distDir -Force -ErrorAction SilentlyContinue

    $standaloneZip = Join-Path $releaseDir "Better-Adrenalin-v$Version-win-x64-standalone.zip"
    $portableZip = Join-Path $releaseDir "Better-Adrenalin-v$Version-win-x64-portable.zip"

    Compress-Archive -Path (Join-Path $standaloneDir "*") -DestinationPath $standaloneZip -CompressionLevel Optimal
    Compress-Archive -Path (Join-Path $distDir "*") -DestinationPath $portableZip -CompressionLevel Optimal

    $hashes = @()
    foreach ($file in @($standaloneZip, $portableZip)) {
        $h = Get-FileHash -Path $file -Algorithm SHA256
        $hashes += "$($h.Hash.ToLowerInvariant())  $(Split-Path -Leaf $file)"
    }
    $sumFile = Join-Path $releaseDir "SHA256SUMS.txt"
    Set-Content -Path $sumFile -Value $hashes -Encoding ASCII
    Write-Host "==> Created release artifacts in $releaseDir" -ForegroundColor Green
}
