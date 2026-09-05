# VS-Agent Benchmark annotation & report script (Phase 1.5, steps 27/28).
#
# Usage:
#   # 1) After finishing one benchmark task, tag the newest untagged session:
#   .\invoke-benchmark.ps1 -TaskCategory compile_fix -TaskId cf-001
#
#   # 2) Generate a Markdown report over all tagged sessions:
#   .\invoke-benchmark.ps1 -ReportOnly
#
# Compatible with PowerShell 5.1+. Session JSONs are produced by the
# extension telemetry at: %LocalAppData%\DeepSeekVS\telemetry\
#
# NOTE: keep this file pure ASCII. Windows PowerShell 5.1 parses BOM-less
# .ps1 files as ANSI, and non-ASCII characters corrupt the parser.
param(
    [string]$TelemetryDir = (Join-Path $env:LOCALAPPDATA 'DeepSeekVS\telemetry'),
    [string]$TaskCategory,
    [string]$TaskId,
    [switch]$ReportOnly,
    [string]$OutFile
)

$ErrorActionPreference = 'Stop'

function Get-Sessions {
    if (-not (Test-Path $TelemetryDir)) { return @() }
    Get-ChildItem -LiteralPath $TelemetryDir -Filter 'agent-session_*.json' | ForEach-Object {
        try {
            $json = [System.IO.File]::ReadAllText($_.FullName, [System.Text.Encoding]::UTF8)
            $s = ConvertFrom-Json $json
            [pscustomobject]@{ Path = $_.FullName; Data = $s }
        } catch {
            Write-Verbose "Skipping corrupt file: $($_.Name)"
        }
    }
}

function Add-TaskAnnotation {
    if ([string]::IsNullOrWhiteSpace($TaskCategory)) {
        throw "-TaskCategory is required (compile_fix / inline_edit / cross_file)"
    }

    $target = Get-Sessions |
        Where-Object { -not $_.Data.task_category } |
        Sort-Object { [DateTime]$_.Data.started_at } -Descending |
        Select-Object -First 1

    if ($null -eq $target) {
        Write-Host 'No untagged sessions found (all sessions already have task_category).' -ForegroundColor Yellow
        return
    }

    $d = $target.Data
    $d | Add-Member -NotePropertyName task_category -NotePropertyValue $TaskCategory -Force
    if (-not [string]::IsNullOrWhiteSpace($TaskId)) {
        $d | Add-Member -NotePropertyName task_id -NotePropertyValue $TaskId -Force
    }
    $out = $d | ConvertTo-Json -Depth 12
    [System.IO.File]::WriteAllText($target.Path, $out, (New-Object System.Text.UTF8Encoding($false)))
    $name = Split-Path $target.Path -Leaf
    Write-Host "Tagged: $name -> category=$TaskCategory id=$TaskId" -ForegroundColor Green
}

function New-Report {
    $sessions = Get-Sessions
    $total = @($sessions).Count
    if ($total -eq 0) {
        Write-Host 'Telemetry directory is empty; nothing to summarize.' -ForegroundColor Yellow
        return
    }

    $catOrder = @('Model', 'Context', 'Host', 'System', 'None')
    $failByCat = @{}
    foreach ($c in $catOrder) { $failByCat[$c] = 0 }

    $success = 0; $failure = 0; $cancelled = 0
    $turnSum = 0.0; $toolSum = 0.0; $durSum = 0.0
    $ttftList = @(); $inTok = 0L; $outTok = 0L
    $byAgent = @{ }; $byCat = @{ }

    foreach ($s in $sessions) {
        $d = $s.Data
        switch ([string]$d.result) {
            'Success'   { $success++ }
            'Cancelled' { $cancelled++ }
            'Failure'   {
                $failure++
                $fc = [string]$d.failure_category
                if (-not $failByCat.ContainsKey($fc)) { $failByCat[$fc] = 0 }
                $failByCat[$fc]++
            }
        }
        $turnSum += [double]$d.turn_count
        $toolSum += [double]$d.tool_call_count
        $durSum  += [double]$d.duration_ms
        $inTok   += [long]$d.input_tokens
        $outTok  += [long]$d.output_tokens
        if ($d.first_turn_ttft_ms) { $ttftList += [double]$d.first_turn_ttft_ms }

        $ag = '(none)'
        if (@($d.agents).Count -gt 0) { $ag = [string]@($d.agents)[0] }
        if (-not $byAgent.ContainsKey($ag)) { $byAgent[$ag] = 0 }
        $byAgent[$ag]++

        if ($d.task_category) {
            if (-not $byCat.ContainsKey([string]$d.task_category)) {
                $byCat[[string]$d.task_category] = [pscustomobject]@{ Total = 0; Success = 0 }
            }
            $byCat[[string]$d.task_category].Total++
            if ([string]$d.result -eq 'Success') { $byCat[[string]$d.task_category].Success++ }
        }
    }

    $avgTtft = 0
    if ($ttftList.Count -gt 0) { $avgTtft = ($ttftList | Measure-Object -Average).Average }
    $ratePct = '{0:P0}' -f ($success / $total)

    $md = New-Object System.Text.StringBuilder
    [void]$md.AppendLine('# VS-Agent Benchmark Report')
    [void]$md.AppendLine()
    [void]$md.AppendLine("Sessions: $total | Success: $success ($ratePct) | Failure: $failure | Cancelled: $cancelled")
    [void]$md.AppendLine()
    [void]$md.AppendLine('## Failures by category')
    [void]$md.AppendLine('| Model | Context | Host | System | Unlabeled |')
    [void]$md.AppendLine('|------:|--------:|-----:|-------:|----------:|')
    [void]$md.AppendLine(('| {0} | {1} | {2} | {3} | {4} |' -f `
        $failByCat['Model'], $failByCat['Context'], $failByCat['Host'],
        $failByCat['System'], $failByCat['None']))
    [void]$md.AppendLine()
    [void]$md.AppendLine('## Averages / totals')
    [void]$md.AppendLine(("- Turns: {0:F1} | Tool calls: {1:F1} | TTFT: {2:F0} ms | Duration: {3:F0} ms" -f `
        ($turnSum / $total), ($toolSum / $total), $avgTtft, ($durSum / $total)))
    [void]$md.AppendLine(("- Tokens: in {0:N0} / out {1:N0}" -f $inTok, $outTok))
    [void]$md.AppendLine()
    [void]$md.AppendLine('## By agent')
    foreach ($k in $byAgent.Keys | Sort-Object) {
        [void]$md.AppendLine("- $k`: $($byAgent[$k])")
    }
    if ($byCat.Count -gt 0) {
        [void]$md.AppendLine()
        [void]$md.AppendLine('## By task category')
        [void]$md.AppendLine('| Category | Total | Success | Rate |')
        [void]$md.AppendLine('|----------|------:|--------:|-----:|')
        foreach ($k in $byCat.Keys | Sort-Object) {
            $v = $byCat[$k]
            $r = '-'
            if ($v.Total -gt 0) { $r = '{0:P0}' -f ($v.Success / $v.Total) }
            [void]$md.AppendLine("| $k | $($v.Total) | $($v.Success) | $r |")
        }
    }

    $text = $md.ToString()
    Write-Host $text
    $dest = $OutFile
    if (-not $dest) {
        $dest = Join-Path $TelemetryDir ('benchmark-report_{0:yyyyMMdd-HHmmss}.md' -f (Get-Date))
    }
    [System.IO.File]::WriteAllText($dest, $text, (New-Object System.Text.UTF8Encoding($false)))
    Write-Host ''
    Write-Host "Report written to: $dest" -ForegroundColor Green
}

if ($ReportOnly) {
    New-Report
} else {
    Add-TaskAnnotation
}
