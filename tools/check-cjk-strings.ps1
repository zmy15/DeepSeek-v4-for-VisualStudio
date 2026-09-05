# ============================================================================
# check-cjk-strings.ps1 - hardcoded CJK string-literal scanner (i18n guard)
#
# Usage:
#   powershell -File tools\check-cjk-strings.ps1            # report mode
#   powershell -File tools\check-cjk-strings.ps1 -Enforce   # exit 1 on new violations
#
# Rules:
#   Scans product .cs files for CJK characters inside string-literal lines
#   (pure comment lines are skipped). Compared against
#   tools\cjk-strings-baseline.txt: files listed there are known stock
#   pending migration (reported only); any OTHER file containing CJK
#   string literals counts as a NEW violation.
#
# Background: docs/I18n-Implementation-Analysis.md section 6, phase 2.
# NOTE: keep this file ASCII-only (PS 5.1 parses BOM-less scripts as ANSI).
# ============================================================================
param([switch]$Enforce)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$baselineFile = Join-Path $PSScriptRoot 'cjk-strings-baseline.txt'

$baseline = @()
if (Test-Path $baselineFile) {
    $baseline = Get-Content $baselineFile | Where-Object { $_.Trim().Length -gt 0 }
}

$files = git ls-files | Where-Object { $_ -match '^Services/.*\.cs$' -or $_ -match '^View/.*\.cs$' }
$newViolations = @()
$stockFiles = @{}

foreach ($f in $files) {
    $full = Join-Path $root ($f -replace '/', '\')
    if (-not (Test-Path $full)) { continue }
    $lines = [IO.File]::ReadAllLines($full, [Text.Encoding]::UTF8)
    $hits = @()
    for ($i = 0; $i -lt $lines.Length; $i++) {
        $codePart = ($lines[$i] -replace '//.*$', '')
        foreach ($ch in $codePart.ToCharArray()) {
            $c = [int]$ch
            if ($c -ge 0x4E00 -and $c -le 0x9FFF) { $hits += ($i + 1); break }
        }
    }
    if ($hits.Count -gt 0) {
        if ($baseline -contains $f) { $stockFiles[$f] = $hits.Count }
        else { $newViolations += $f }
    }
}

Write-Host ("Stock files (pending migration): " + $stockFiles.Count)
foreach ($k in $stockFiles.Keys) { Write-Host ("  [stock] " + $k + " (" + $stockFiles[$k] + " lines)") }

if ($newViolations.Count -gt 0) {
    Write-Host ("NEW violations: " + $newViolations.Count)
    foreach ($v in $newViolations) { Write-Host ("  [NEW] " + $v) }
    if ($Enforce) { exit 1 }
} else {
    Write-Host "No new violations."
}
