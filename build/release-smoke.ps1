<#
.SYNOPSIS
  Release smoke test: drives a published FinanceOS.exe with UI Automation through the PROMPT scenarios
  A (simple user), F (Advanced -> Simple) and E (abrupt shutdown, restart), on a temporary data folder.

.DESCRIPTION
  The real data is never touched: FINANCEOS_DATA_ROOT points the app at a new folder under %TEMP%,
  which also keeps it away from the toasts Windows holds for the real data. The app is killed, never
  closed, so nothing is scheduled with Windows. Database checks use Python's sqlite3 when Python is on
  PATH (read-only); without it they are reported as SKIP.
  Needs the desktop unlocked: it types with SendKeys and clicks two checkboxes with the mouse.

.EXAMPLE
  ./build/release-smoke.ps1 -Exe artifacts/publish/win-x64-single-jit/FinanceOS.exe
#>
param([Parameter(Mandatory)] [string]$Exe)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms, System.Drawing
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class SmokeNative {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern void mouse_event(int f, int x, int y, int d, int e);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
}
'@

$exePath = (Resolve-Path $Exe).Path
$root = Join-Path $env:TEMP ("FinanceOS-smoke-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $root | Out-Null
'{"language":"ro"}' | Set-Content -Encoding ascii (Join-Path $root 'preferences.json') # Romanian names below
$env:FINANCEOS_DATA_ROOT = $root
$db = Join-Path $root 'data\financeos.db'
$AE = [Windows.Automation.AutomationElement]
$Scope = [Windows.Automation.TreeScope]
$failures = 0
$python = Get-Command python -ErrorAction SilentlyContinue

function Say([string]$label, $ok) {
    if ($ok -is [string] -and $ok -eq 'SKIP') { "{0,-62} SKIP" -f $label; return }
    if (-not $ok) { $script:failures++ }
    "{0,-62} {1}" -f $label, ($(if ($ok) { 'OK' } else { 'FAIL' }))
}
function Query([string]$sql) {
    if (-not $python) { return $null }
    # No double quotes: Windows PowerShell 5.1 drops them from native arguments.
    $code = 'import sqlite3,sys; c=sqlite3.connect(''file:''+sys.argv[1]+''?mode=ro'',uri=True); print(c.execute(sys.argv[2]).fetchone()[0])'
    $value = (& $python.Source -I -c $code $db $sql | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or -not $value) { throw "Database query failed: $sql" }
    return $value
}
function Start-App {
    $p = Start-Process $exePath -PassThru
    for ($i = 0; $i -lt 150 -and $p.MainWindowHandle -eq 0; $i++) { Start-Sleep -Milliseconds 200; $p.Refresh() }
    if ($p.MainWindowHandle -eq 0) { throw 'The window did not appear within 30 s.' }
    Start-Sleep -Milliseconds 2500
    # Maximized: lists are virtualized, so only rows on screen exist for UI Automation.
    $AE::FromHandle($p.MainWindowHandle).GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern).SetWindowVisualState([Windows.Automation.WindowVisualState]::Maximized)
    Start-Sleep -Milliseconds 500
    [void][SmokeNative]::SetForegroundWindow($p.MainWindowHandle)
    Start-Sleep -Milliseconds 300
    $p
}
function Window($p) { $AE::FromHandle($p.MainWindowHandle) }
function Find($win, [string]$name) { $win.FindFirst($Scope::Descendants, (New-Object Windows.Automation.PropertyCondition($AE::NameProperty, $name))) }
function Invoke($win, [string]$name) {
    $e = Find $win $name
    if (-not $e) { throw "Not found: $name" }
    $e.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep -Milliseconds 600
}
function Select-Item($element) { $element.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select(); Start-Sleep -Milliseconds 600 }
function Keys([string]$keys, [int]$wait = 500) { [System.Windows.Forms.SendKeys]::SendWait($keys); Start-Sleep -Milliseconds $wait }
function Visible-Sections($win) {
    $nav = $win.FindFirst($Scope::Descendants, (New-Object Windows.Automation.PropertyCondition($AE::AutomationIdProperty, 'Navigation')))
    @($nav.FindAll($Scope::Children, [Windows.Automation.Condition]::TrueCondition) | Where-Object { -not $_.Current.IsOffscreen }).Count
}
function Click-Center($element) {
    $r = $element.Current.BoundingRectangle
    [void][SmokeNative]::SetCursorPos([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2))
    [SmokeNative]::mouse_event(2, 0, 0, 0, 0); [SmokeNative]::mouse_event(4, 0, 0, 0, 0); Start-Sleep -Milliseconds 600
}

$app = $null
try {
    # --- First run: guide, Advanced mode, sample data ------------------------------------------------
    $app = Start-App
    $win = Window $app
    Say 'First run shows the setup guide' ($null -ne (Find $win 'Bun venit în Finance OS'))
    Invoke $win 'Continuă'
    Select-Item (Find $win 'Avansat')
    Invoke $win 'Continuă'; Invoke $win 'Continuă'; Invoke $win 'Continuă'
    Invoke $win 'Deschide Finance OS'
    Say 'Guide finished: all 10 sections in Advanced mode' ((Visible-Sections $win) -eq 10)
    Say 'Sample data added (6 tasks, marked is_demo)' ($(if ($python) { (Query 'select count(*) from tasks where is_demo = 1') -eq '6' } else { 'SKIP' }))

    # --- Scenario A: five tasks for today, close two -------------------------------------------------
    [void][SmokeNative]::SetForegroundWindow($app.MainWindowHandle)
    Keys '^n' 800
    $focused = $AE::FocusedElement.Current.Name
    Say "A: Ctrl+N puts the cursor in quick add ($focused)" ($focused -eq 'Task nou')
    foreach ($i in 1..5) { Keys "Sarcina $i{ENTER}" 400 }
    Start-Sleep -Milliseconds 800
    $missing = @(1..5 | Where-Object { -not (Find $win "Sarcina $_") })
    Say "A: five tasks added from the keyboard (missing: $($missing -join ','))" ($missing.Count -eq 0)
    Say 'A: five tasks saved' ($(if ($python) { (Query "select count(*) from tasks where title like 'Sarcina %'") -eq '5' } else { 'SKIP' }))
    foreach ($i in 1, 2) {
        $row = Find $win "Sarcina $i"
        $box = $row.FindFirst($Scope::Descendants, (New-Object Windows.Automation.PropertyCondition($AE::NameProperty, 'Marchează ca finalizat')))
        Click-Center $box
    }
    Start-Sleep -Milliseconds 800
    Say 'A: two tasks completed with their checkbox' ($(if ($python) { (Query "select count(*) from tasks where title like 'Sarcina %' and completed_at is not null") -eq '2' } else { 'SKIP' }))

    # --- Scenario F: Advanced -> Simple, data untouched ----------------------------------------------
    $before = Query "select count(*) || '/' || max(updated_at) from tasks"
    [void][SmokeNative]::SetForegroundWindow($app.MainWindowHandle)
    Keys '^k' 600
    Keys 'Treci la modul Simplu' 600
    Keys '{ENTER}' 900
    Say 'F: Simple mode shows 6 sections (from 10)' ((Visible-Sections $win) -eq 6)
    Say 'F: switching modes changed no task' ($(if ($python) { (Query "select count(*) || '/' || max(updated_at) from tasks") -eq $before } else { 'SKIP' }))

    # --- Scenario E: power cut (kill), restart, everything still there -------------------------------
    $app.Kill(); [void]$app.WaitForExit(10000)
    $app = Start-App
    $win = Window $app
    Say 'E: restart after an abrupt stop opens the main window' (($null -eq (Find $win 'Bun venit în Finance OS')) -and ($null -ne (Find $win 'Taskuri')))
    Select-Item (Find $win 'Taskuri')
    $missing = @(3..5 | Where-Object { -not (Find $win "Sarcina $_") })
    Say 'E: open tasks are still in the list' ($missing.Count -eq 0)
    Say 'E: completed tasks stayed completed' ($(if ($python) { (Query "select count(*) from tasks where title like 'Sarcina %' and completed_at is not null") -eq '2' } else { 'SKIP' }))
    Say 'E: still in Simple mode after restart' ((Visible-Sections $win) -eq 6)
}
finally {
    if ($app -and -not $app.HasExited) { $app.Kill(); [void]$app.WaitForExit(10000) }
    Remove-Item Env:FINANCEOS_DATA_ROOT -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500
    Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue
}
if ($failures -gt 0) { throw "$failures check(s) failed." }
'All checks passed.'
