<#
.SYNOPSIS
  Reproducible startup and idle-memory benchmark for a published FinanceOS.exe.

.DESCRIPTION
  Startup: launches the exe N times with --bench. The app records the time from process start
  to its first rendered frame plus memory one second later, then exits.
  Idle: one run with --bench-idle=S: memory after S seconds with the window open, then after
  S seconds with the window closed (tray only).
  Run 1 is reported separately: for single-file builds it includes extracting native libraries.

.EXAMPLE
  ./build/bench.ps1 -Exe artifacts/publish/win-x64-single/FinanceOS.exe
#>
param(
    [Parameter(Mandatory)] [string]$Exe,
    [int]$Runs = 10,
    [int]$IdleSeconds = 60
)
$ErrorActionPreference = 'Stop'
$exePath = (Resolve-Path $Exe).Path
$work = Join-Path $env:TEMP 'financeos-bench'
New-Item -ItemType Directory -Force $work | Out-Null
$resultsFile = Join-Path $work ("results-{0:yyyyMMdd-HHmmss}.jsonl" -f (Get-Date))

function Invoke-Bench([string[]]$appArgs, [int]$timeoutSec) {
    $p = Start-Process -FilePath $exePath -ArgumentList ($appArgs + "--out=`"$resultsFile`"") -PassThru
    if (-not $p.WaitForExit($timeoutSec * 1000)) { $p.Kill(); throw "benchmark run timed out" }
}

for ($i = 1; $i -le $Runs; $i++) { Invoke-Bench @('--bench') 60 }
Invoke-Bench @("--bench-idle=$IdleSeconds") ($IdleSeconds * 2 + 60)

$all = Get-Content $resultsFile | ForEach-Object { $_ | ConvertFrom-Json }
$startup = @($all | Where-Object label -eq 'startup')
$warm = @($startup | Select-Object -Skip 1)

function Median([double[]]$xs) {
    $s = $xs | Sort-Object
    if ($s.Count -eq 0) { return $null }
    if ($s.Count % 2) { return $s[[int][Math]::Floor($s.Count / 2)] }
    return ($s[$s.Count / 2 - 1] + $s[$s.Count / 2]) / 2
}

$exeMb = (Get-Item $exePath).Length / 1MB
"Exe:                 $exePath ({0:N1} MB)" -f $exeMb
"Run 1 startup:       {0} ms, working set {1} MB" -f $startup[0].startupMs, $startup[0].workingSetMb
"Runs 2..$Runs median:  {0} ms (min {1}, max {2}), working set {3} MB, private {4} MB" -f `
    (Median ($warm.startupMs)), ($warm.startupMs | Measure-Object -Minimum).Minimum, ($warm.startupMs | Measure-Object -Maximum).Maximum,
    (Median ($warm.workingSetMb)), (Median ($warm.privateMb))
$idleWin = $all | Where-Object label -eq 'idle-window'
$idleTray = $all | Where-Object label -eq 'idle-tray'
"Idle {0}s window:     working set {1} MB, private {2} MB" -f $IdleSeconds, $idleWin.workingSetMb, $idleWin.privateMb
"Idle {0}s tray:       working set {1} MB, private {2} MB" -f $IdleSeconds, $idleTray.workingSetMb, $idleTray.privateMb
"Raw results:         $resultsFile"
