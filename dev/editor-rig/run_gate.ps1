# Editor gate driver (adapted from humanoid-retargeter's run_ui_smoke.ps1).
#
# Creates a scratch s&box game project OUTSIDE the repo, links this library into it (NTFS junction),
# launches its own sbox-dev.exe on it and waits for the in-editor hook
# (Editor/TextToAnimation/Testing/EditorGate.cs, armed by T2A_GATE + a one-shot .arm marker) to write a
# JSON result. Then scans this run's slice of sbox-dev.log for compile / whitelist errors in our code.
# Only the editor process started here is ever stopped.
#
# Usage:  powershell -ExecutionPolicy Bypass -File dev\editor-rig\run_gate.ps1 [-Clean] [-TimeoutSec 900]
# Exit codes: 0 = passed, 1 = ran but failed, 2 = no result (compile failure / crash / never armed)

[CmdletBinding()]
param(
    [string]$SboxRoot = "C:\Program Files (x86)\Steam\steamapps\common\sbox",
    [int]$TimeoutSec = 900,
    [switch]$Clean
)
$ErrorActionPreference = "Stop"
$repoRoot   = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$scratch    = Join-Path $env:TEMP "t2a-editor-rig\scratch"
$sbproj     = Join-Path $scratch "t2ascratch.sbproj"
$libDir     = Join-Path $scratch "Libraries\local.chomnr_text_to_animation"
$resultPath = Join-Path $PSScriptRoot "gate_result.json"
$sboxExe    = Join-Path $SboxRoot "sbox-dev.exe"
$sboxLog    = Join-Path $SboxRoot "logs\sbox-dev.log"
$template   = Join-Path $SboxRoot "templates\game.minimal"

function Fail([int]$code, [string]$msg) { Write-Host "RESULT: $msg" -ForegroundColor Red; exit $code }
if (-not (Test-Path $sboxExe)) { Fail 2 "sbox-dev.exe not found at $sboxExe" }

if ($Clean -and (Test-Path $scratch)) {
    if (Test-Path $libDir) { cmd /c rmdir "$libDir" | Out-Null }
    Remove-Item -Recurse -Force $scratch
}
if (-not (Test-Path $sbproj)) {
    Write-Host "Creating scratch project at $scratch"
    New-Item -ItemType Directory -Force $scratch | Out-Null
    foreach ($d in "Assets", "Code", "Editor") { Copy-Item (Join-Path $template $d) (Join-Path $scratch $d) -Recurse -Force }
    $proj = Get-Content (Join-Path $template "`$ident.sbproj") -Raw
    $proj = $proj -replace '"Title":\s*"[^"]*"', '"Title": "T2A Gate"'
    $proj = $proj -replace '"Ident":\s*"[^"]*"', '"Ident": "t2ascratch"'
    [System.IO.File]::WriteAllText($sbproj, $proj)
}
New-Item -ItemType Directory -Force (Join-Path $scratch "Libraries") | Out-Null
if (Test-Path $libDir) {
    $item = Get-Item $libDir -Force
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -and $item.Target -ne $repoRoot) { cmd /c rmdir "$libDir" | Out-Null }
}
if (-not (Test-Path $libDir)) { New-Item -ItemType Junction -Path $libDir -Value $repoRoot | Out-Null }

# fresh state: previous outputs, workspaces and backups of the scratch project
Remove-Item $resultPath -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $PSScriptRoot "gate_shots") -Recurse -Force -ErrorAction SilentlyContinue
foreach ($d in "Assets\t2a_gate", "Assets\t2a_gate_export", "text_to_animation") {
    $p = Join-Path $scratch $d; if (Test-Path $p) { Remove-Item -Recurse -Force $p }
}
Set-Content -Path "$resultPath.arm" -Value (Get-Date -Format o) -Encoding ascii
$preLogLen = 0; if (Test-Path $sboxLog) { $preLogLen = (Get-Item $sboxLog).Length }

$env:T2A_GATE = $resultPath
try {
    Write-Host "Launching: `"$sboxExe`" -project `"$sbproj`""
    $proc = Start-Process -FilePath $sboxExe -ArgumentList @("-project", "`"$sbproj`"") -WorkingDirectory $SboxRoot -PassThru
} finally { Remove-Item Env:T2A_GATE -ErrorAction SilentlyContinue }

$deadline = (Get-Date).AddSeconds($TimeoutSec)
$completed = $false
$lastLines = 0
while ((Get-Date) -lt $deadline) {
    if (Test-Path $resultPath) {
        try {
            $j = Get-Content $resultPath -Raw | ConvertFrom-Json
            if ($j.log.Count -gt $lastLines) { $j.log | Select-Object -Skip $lastLines | ForEach-Object { Write-Host "  $_" }; $lastLines = $j.log.Count }
            if ($j.completed) { $completed = $true; break }
        } catch { }
    }
    if ($proc.HasExited) { break }
    Start-Sleep -Seconds 2
}
if ($completed -and -not $proc.HasExited) { $proc.WaitForExit(30000) | Out-Null }
if (-not $proc.HasExited) { Write-Warning "Stopping the gate editor (pid $($proc.Id))."; taskkill /PID $proc.Id /T /F | Out-Null }
Remove-Item "$resultPath.arm" -Force -ErrorAction SilentlyContinue

# per-run log slice
$newLog = @()
if (Test-Path $sboxLog) {
    $fs = [System.IO.File]::Open($sboxLog, 'Open', 'Read', 'ReadWrite')
    try {
        if ($fs.Length -lt $preLogLen) { $preLogLen = 0 }
        $fs.Seek($preLogLen, 'Begin') | Out-Null
        $newLog = (New-Object System.IO.StreamReader($fs)).ReadToEnd() -split "`r?`n"
    } finally { $fs.Dispose() }
}
$compileErrors = @($newLog | Where-Object { ($_ -match '(?i)error' -and $_ -match '(?i)TextToAnimation|text_to_animation|chomnr_text_to_animation') -or $_ -match 'not allowed when whitelist' -or $_ -match 'Broken Reference' })
Write-Host ""
Write-Host "===== compile / whitelist errors (this run) =====" -ForegroundColor Cyan
if ($compileErrors.Count -gt 0) { $compileErrors | Select-Object -First 40 | ForEach-Object { Write-Host $_ -ForegroundColor Red } } else { Write-Host "(none)" }
Write-Host "===== gate-related log lines =====" -ForegroundColor Cyan
$newLog | Where-Object { $_ -match 't2a-gate|text-to-animation|Exception' } | Select-Object -Last 60 | ForEach-Object { Write-Host $_ }

if (-not (Test-Path $resultPath)) { Fail 2 "NO RESULT - the gate never ran (library compile failure or editor crash; see errors above)." }
$res = Get-Content $resultPath -Raw | ConvertFrom-Json
Write-Host ""
Write-Host "===== checks =====" -ForegroundColor Cyan
$res.checks.PSObject.Properties | ForEach-Object { Write-Host ("{0,-50} {1}" -f $_.Name, $(if ($_.Value) { "PASS" } else { "FAIL" })) -ForegroundColor $(if ($_.Value) { "Green" } else { "Red" }) }
if (-not $res.completed) { Fail 2 "PARTIAL RESULT - the gate did not finish." }
if ($compileErrors.Count -gt 0) { Fail 1 "FAIL - compile/whitelist errors in our code." }
if ($res.passed) { Write-Host "RESULT: PASS" -ForegroundColor Green; exit 0 }
Fail 1 "FAIL - see the checks above."
