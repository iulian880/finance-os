<#
.SYNOPSIS
  Screenshot of the real Finance OS window (title bar, caption buttons, layout as Windows shows them).

.DESCRIPTION
  Starts the exe with the given arguments, waits for its main window and renders it with
  PrintWindow(PW_RENDERFULLCONTENT). PrintWindow draws only this window, so other windows on the
  screen can never end up in the image (a screen capture could, if something was on top).
  The process is then killed rather than closed, so nothing is saved (window placement, settings).
  Refuses to run while another Finance OS instance is open: single-instance would forward the
  arguments to it and the capture would show the wrong window.

  Note: the Mica backdrop is composed by DWM and may not appear in PrintWindow output; the areas
  over Mica then show the window's own fallback.

.EXAMPLE
  ./build/capture-window.ps1 -Exe src/FinanceOS.App/bin/Debug/net10.0-windows10.0.19041.0/FinanceOS.exe `
      -Arguments '--theme=dark','--section=tasks' -Out shots/tasks-dark.png
#>
param(
    [Parameter(Mandatory)] [string]$Exe,
    [Parameter(Mandatory)] [string]$Out,
    [string[]]$Arguments = @(),
    [int]$SettleMilliseconds = 2500
)
$ErrorActionPreference = 'Stop'

if (Get-Process -Name FinanceOS -ErrorAction SilentlyContinue) {
    throw 'Finance OS is already running. Close it (tray > Exit) before capturing.'
}

Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class CaptureNative {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT rect, int size);
}
'@
# Physical pixels: without this, Windows scales the coordinates for a DPI-unaware PowerShell.
[void][CaptureNative]::SetProcessDPIAware()

$process = Start-Process -FilePath (Resolve-Path $Exe).Path -ArgumentList $Arguments -PassThru
try {
    $deadline = (Get-Date).AddSeconds(20)
    while ($process.MainWindowHandle -eq [IntPtr]::Zero) {
        if ((Get-Date) -gt $deadline) { throw 'The window did not appear within 20 s.' }
        Start-Sleep -Milliseconds 200
        $process.Refresh()
    }

    $hwnd = $process.MainWindowHandle
    Start-Sleep -Milliseconds $SettleMilliseconds

    # PrintWindow paints the whole window rect, including the invisible resize borders;
    # crop to the visible frame (DWM extended frame bounds).
    $window = New-Object CaptureNative+RECT
    $visible = New-Object CaptureNative+RECT
    [void][CaptureNative]::GetWindowRect($hwnd, [ref]$window)
    [void][CaptureNative]::DwmGetWindowAttribute($hwnd, 9, [ref]$visible, 16) # DWMWA_EXTENDED_FRAME_BOUNDS

    $full = New-Object System.Drawing.Bitmap ($window.Right - $window.Left), ($window.Bottom - $window.Top)
    $graphics = [System.Drawing.Graphics]::FromImage($full)
    $hdc = $graphics.GetHdc()
    $ok = [CaptureNative]::PrintWindow($hwnd, $hdc, 2) # PW_RENDERFULLCONTENT
    $graphics.ReleaseHdc($hdc)
    $graphics.Dispose()
    if (-not $ok) { throw 'PrintWindow failed.' }

    $crop = New-Object System.Drawing.Rectangle ($visible.Left - $window.Left), ($visible.Top - $window.Top), ($visible.Right - $visible.Left), ($visible.Bottom - $visible.Top)
    $image = $full.Clone($crop, $full.PixelFormat)
    New-Item -ItemType Directory -Force (Split-Path -Parent ([IO.Path]::GetFullPath($Out))) | Out-Null
    $image.Save([IO.Path]::GetFullPath($Out), [System.Drawing.Imaging.ImageFormat]::Png)
    $image.Dispose()
    $full.Dispose()
    "{0} ({1}x{2})" -f $Out, $crop.Width, $crop.Height
}
finally {
    if (-not $process.HasExited) { $process.Kill(); [void]$process.WaitForExit(10000) }
}
