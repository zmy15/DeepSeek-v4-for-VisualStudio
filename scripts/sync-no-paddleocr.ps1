# sync-no-paddleocr.ps1
# Sync feature/v1.1.10 to feature/v1.1.10-no-local-paddleocr (remove PaddleOCR only)
#
# Usage:
#   .\scripts\sync-no-paddleocr.ps1              # sync + local commit
#   .\scripts\sync-no-paddleocr.ps1 -Push        # sync + commit + push

param([switch]$Push)

$ErrorActionPreference = "Stop"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptDir
$patchFile = Join-Path $scriptDir "paddleocr-remove.patch"
$sourceBranch = "feature/v1.1.10"
$targetBranch = "feature/v1.1.10-no-local-paddleocr"

if (-not (Test-Path $patchFile)) {
    Write-Error "Patch file not found: $patchFile"
    Write-Host "Regenerate with: cmd /c 'git diff $sourceBranch..$targetBranch > $patchFile'"
    exit 1
}

Push-Location $repoRoot
try {
    $status = git status --porcelain
    if ($status) {
        Write-Error "Working tree is dirty. Please commit or stash changes first."
        git status --short
        exit 1
    }

    Write-Host "[1/4] Fetching..." -ForegroundColor Cyan
    git fetch origin

    Write-Host "[2/4] Checkout $targetBranch + reset to $sourceBranch..." -ForegroundColor Cyan
    git checkout $targetBranch
    git reset --hard "origin/$sourceBranch"

    Write-Host "[3/4] Applying PaddleOCR removal patch..." -ForegroundColor Cyan
    git apply --index $patchFile
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Patch failed. Regenerate: cmd /c 'git diff $sourceBranch..$targetBranch > $patchFile'"
        git checkout $sourceBranch
        exit 1
    }

    $msg = "chore: sync with $sourceBranch + remove PaddleOCR only"
    git commit -m $msg
    Write-Host "Committed: $msg" -ForegroundColor Green

    if ($Push) {
        Write-Host "[4/4] Pushing $targetBranch..." -ForegroundColor Cyan
        git push --force-with-lease origin $targetBranch
        Write-Host "Done: origin/$targetBranch updated" -ForegroundColor Green
    } else {
        Write-Host "[4/4] Skipped push (use -Push to push)" -ForegroundColor Yellow
    }

    git checkout $sourceBranch
    Write-Host "Back on $sourceBranch" -ForegroundColor Green
}
catch {
    Write-Error "Sync failed: $_"
    try { git checkout $sourceBranch } catch { }
    exit 1
}
finally {
    Pop-Location
}
