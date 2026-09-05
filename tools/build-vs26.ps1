# Reproducible full-VSIX build + test using the VS2026 toolchain on this machine.
#
# Why this exists: VS2026 has the C#14 Roslyn but lacks the .NET SDK resolver,
# while dotnet SDK 9's Roslyn rejects LangVersion 14. We therefore point MSBuild
# at a junction-merged Sdks view (real SDK targets + synthesized workload
# locator stubs) and let VS2026's compiler do the compilation.
#
# Usage:
#   .\scripts\build-vs26.ps1                      # main + tests + run tests
#   .\scripts\build-vs26.ps1 -SkipTests           # main only
#   .\scripts\build-vs26.ps1 -Configuration Release
#
# NOTE: keep this file pure ASCII (PS 5.1 parses BOM-less scripts as ANSI).
param(
    [string]$Configuration = 'Debug',
    [switch]$SkipTests,
    [switch]$NoRestore
)

$ErrorActionPreference = 'Stop'

$Vs26Root   = 'D:\Visual Studio 2026'
$Msbuild    = Join-Path $Vs26Root 'MSBuild\Current\Bin\MSBuild.exe'
$Vstest     = 'D:\Visual Studio\IDE\Common7\IDE\CommonExtensions\Microsoft\TestWindow\vstest.console.exe'
$SdkSdksDir = 'C:\Program Files\dotnet\sdk\9.0.314\Sdks'
$RepoRoot   = Split-Path -Parent $PSScriptRoot

if (-not (Test-Path $Msbuild))   { throw "MSBuild not found: $Msbuild" }
if (-not (Test-Path $SdkSdksDir)) { throw "dotnet SDK Sdks dir not found: $SdkSdksDir" }

# ── 1. Build the merged Sdks view (junctions + workload locator stubs) ──
$merge = Join-Path $env:TEMP 'ds-sdkmerge'
if (Test-Path $merge) { Remove-Item $merge -Recurse -Force }
New-Item -ItemType Directory -Path $merge | Out-Null

foreach ($d in Get-ChildItem $SdkSdksDir -Directory) {
    cmd /c mklink /J "`"$merge\$($d.Name)`"" "`"$($d.FullName)`"" | Out-Null
}
Get-ChildItem $SdkSdksDir -File -ErrorAction SilentlyContinue |
    Copy-Item -Destination $merge -ErrorAction SilentlyContinue

$emptyProject = '<Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003" />'
foreach ($locator in @('Microsoft.NET.SDK.WorkloadAutoImportPropsLocator',
                       'Microsoft.NET.SDK.WorkloadManifestTargetsLocator')) {
    $stub = Join-Path $merge "$locator\Sdk"
    New-Item -ItemType Directory -Path $stub -Force | Out-Null
    foreach ($f in 'AutoImport.props','AutoImport.targets','Sdk.props','Sdk.targets') {
        Set-Content -LiteralPath (Join-Path $stub $f) -Value $emptyProject -Encoding UTF8
    }
    # ImportWorkloads.targets pulls WorkloadManifest.targets through this locator
    Set-Content -LiteralPath (Join-Path $stub 'WorkloadManifest.targets') `
        -Value $emptyProject -Encoding UTF8
}

$env:MSBuildSDKsPath = $merge

# ── 2. Build the extension ──
$restoreArgs = @()
if (-not $NoRestore) { $restoreArgs += '-t:Restore,Build' } else { $restoreArgs += '-t:Build' }

& $Msbuild (Join-Path $RepoRoot 'DeepSeek_v4_for_VisualStudio.csproj') `
    @restoreArgs "-p:Configuration=$Configuration" -v:m -nologo -m:1
if ($LASTEXITCODE -ne 0) { throw "Main project build failed ($LASTEXITCODE)" }
Write-Host '[OK] Main project built.' -ForegroundColor Green

if ($SkipTests) { Write-Host 'Done (-SkipTests).' ; return }

# ── 3. Build tests (coverlet needs a non-empty NETCoreSdkVersion under VS MSBuild) ──
& $Msbuild (Join-Path $RepoRoot 'DeepSeek_v4_for_VisualStudio.Tests\DeepSeek_v4_for_VisualStudio.Tests.csproj') `
    @restoreArgs "-p:Configuration=$Configuration" '-p:NETCoreSdkVersion=9.0.314' -v:m -nologo -m:1
if ($LASTEXITCODE -ne 0) { throw "Tests project build failed ($LASTEXITCODE)" }
Write-Host '[OK] Tests project built.' -ForegroundColor Green

# ── 4. Run the suite ──
$testDll = Join-Path $RepoRoot "DeepSeek_v4_for_VisualStudio.Tests\bin\$Configuration\net472\DeepSeek_v4_for_VisualStudio.Tests.dll"
& $Vstest $testDll
if ($LASTEXITCODE -ne 0) { throw "Tests failed ($LASTEXITCODE)" }
