<#
.SYNOPSIS
  M0 automated checks on a real Windows session (run as the normal, non-elevated user).

.DESCRIPTION
  1. Launch the app, then launch it again: the second instance must forward and exit.
  2. Schedule a toast from the UI (via UI Automation), then EXIT the app completely.
  3. Probe: the toast must still be scheduled in Windows after the app exited.
  4. Wait past the delivery time; probe again: Windows must have delivered it (Action Center)
     although the app was not running.
  5. Report where toast registration was written (must be per-user: HKCU / %LOCALAPPDATA%).

  Not covered (manual, see docs/spike-m0.md): clicking toast buttons, reboot, sleep.

.EXAMPLE
  ./build/spike-m0-check.ps1 -Exe artifacts/publish/win-x64-single-jit/FinanceOS.exe
#>
param(
    [string]$Exe = (Join-Path $PSScriptRoot '..\artifacts\publish\win-x64-single-jit\FinanceOS.exe'),
    [int]$DeliveryGraceSeconds = 20
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$exePath = (Resolve-Path $Exe).Path
# Since M5 the app logs to daily files (financeos-yyyyMMdd.log) with the same line prefix.
$logDir = Join-Path $env:LOCALAPPDATA 'FinanceOS\logs'
$work = Join-Path $env:TEMP 'financeos-m0-check'
New-Item -ItemType Directory -Force $work | Out-Null
$checks = New-Object System.Collections.Generic.List[object]
function Add-Check($name, [bool]$pass, $detail) {
    $checks.Add([pscustomobject]@{ Check = $name; Result = $(if ($pass) { 'PASS' } else { 'FAIL' }); Detail = $detail })
}
function Get-AppProcesses { @(Get-Process -Name FinanceOS -ErrorAction SilentlyContinue) }
function Get-LogSince([datetime]$since) {
    $files = @(Get-ChildItem $logDir -Filter 'financeos-*.log' -ErrorAction SilentlyContinue | Sort-Object Name | Select-Object -Last 2)
    if ($files.Count -eq 0) { return @() }
    Get-Content $files.FullName -Encoding UTF8 | Where-Object {
        $_.Length -ge 23 -and [datetime]::ParseExact($_.Substring(0, 23), 'yyyy-MM-dd HH:mm:ss.fff', $null) -ge $since
    }
}
function Invoke-Probe {
    $out = Join-Path $work ("probe-{0}.json" -f [guid]::NewGuid().ToString('N'))
    $p = Start-Process $exePath -ArgumentList '--probe', "--out=`"$out`"" -PassThru
    if (-not $p.WaitForExit(20000)) { $p.Kill(); throw 'probe timed out' }
    Get-Content $out -Raw | ConvertFrom-Json
}
function Find-AppWindow([int]$timeoutSec) {
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $deadline = (Get-Date).AddSeconds($timeoutSec)
    while ((Get-Date) -lt $deadline) {
        foreach ($w in $root.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)) {
            if ($w.Current.Name -like 'Finance OS*') { return $w }
        }
        Start-Sleep -Milliseconds 250
    }
    throw 'Finance OS window not found'
}
function Invoke-AppButton($window, [string]$automationId) {
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $automationId)
    $button = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
    if (-not $button) { throw "button $automationId not found" }
    $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}

if ((Get-AppProcesses).Count -gt 0) { throw 'Close Finance OS (tray: Ieșire) before running this check.' }
$t0 = Get-Date

# 1. Start + single instance
$primary = Start-Process $exePath -PassThru
$window = Find-AppWindow 20
$second = Start-Process $exePath -PassThru
$secondExited = $second.WaitForExit(10000)
Start-Sleep -Milliseconds 500
$forwardLine = Get-LogSince $t0 | Where-Object { $_ -match 'secondary instance, forwarded=True' }
$receivedLine = Get-LogSince $t0 | Where-Object { $_ -match "\[$($primary.Id)\] forwarded args" }
Add-Check 'Single instance' ($secondExited -and $forwardLine -and $receivedLine -and (Get-AppProcesses).Count -eq 1) `
    "second exited=$secondExited, forwarded=$([bool]$forwardLine), primary received=$([bool]$receivedLine)"

$setting = (Invoke-Probe).setting
Add-Check 'Windows notifications enabled for app' ($setting -eq 'Enabled') "setting=$setting"

# 2. Schedule a toast via the UI, then exit the app
$scheduledAt = Get-Date
Invoke-AppButton $window 'ScheduleOneMinute'
Start-Sleep -Seconds 1
$line = Get-LogSince $scheduledAt | Where-Object { $_ -match 'toast scheduled tag=(\S+) at=(\S+)' } | Select-Object -Last 1
if (-not $line) { throw 'scheduling did not log a tag' }
$null = $line -match 'toast scheduled tag=(\S+) at=(\S+)'
$tag = $Matches[1]; $deliveryTime = [datetimeoffset]::Parse($Matches[2])
Invoke-AppButton $window 'Exit'
$exited = $primary.WaitForExit(10000)
Add-Check 'App exits completely' ($exited -and (Get-AppProcesses).Count -eq 0) "exited=$exited"

# 3. Still scheduled after exit
$probe = Invoke-Probe
$stillScheduled = @($probe.scheduled | Where-Object Tag -eq $tag).Count -eq 1
Add-Check 'Scheduled toast survives app exit' $stillScheduled "tag=$tag due=$($deliveryTime.ToString('HH:mm:ss'))"

# 4. Delivered by Windows while the app is closed
$wait = ($deliveryTime - [datetimeoffset]::Now).TotalSeconds + $DeliveryGraceSeconds
if ($wait -gt 0) { Start-Sleep -Seconds ([int][Math]::Ceiling($wait)) }
$running = (Get-AppProcesses).Count
$probe = Invoke-Probe
$gone = @($probe.scheduled | Where-Object Tag -eq $tag).Count -eq 0
$delivered = @($probe.delivered) -contains $tag
Add-Check 'Delivered by Windows with app closed' ($gone -and $delivered -and $running -eq 0) `
    "left schedule=$gone, in Action Center=$delivered, app processes during wait=$running"

# 5. Where was the registration written?
$aumidKeys = @(Get-ChildItem 'HKCU:\Software\Classes\AppUserModelId' -ErrorAction SilentlyContinue | Where-Object {
    (Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue).DisplayName -like '*Finance*' })
$compatDir = Join-Path $env:LOCALAPPDATA 'ToastNotificationManagerCompat'
Add-Check 'Registration is per-user (HKCU)' ($aumidKeys.Count -ge 1) `
    "HKCU AUMID keys=$($aumidKeys.Count) [$($aumidKeys.PSChildName -join ', ')]; icon cache exists=$(Test-Path $compatDir)"

$checks | Format-Table -AutoSize -Wrap | Out-String -Width 200
$report = Join-Path $work ("m0-check-{0:yyyyMMdd-HHmmss}.json" -f (Get-Date))
$checks | ConvertTo-Json | Set-Content -Encoding UTF8 $report
"Report: $report"
if ($checks.Result -contains 'FAIL') { exit 1 }
