<#
.SYNOPSIS
  Publishes FinanceOS.exe as a self-contained Windows build (no .NET needed on the target PC).

.PARAMETER Variant
  single             single-file exe, native libraries self-extract on first run (default)
  single-compressed  same, with compression (smaller, slower startup)
  single-native      single-file exe with the ~6 native WPF DLLs next to it, nothing extracted to %TEMP%
                     (fallback for PCs whose policy blocks DLLs loaded from %TEMP%, ARCHITECTURE R8)
  folder             exe + DLLs in one folder (no self-extraction; fallback if antivirus/AppLocker objects)

.EXAMPLE
  ./build/publish.ps1
  ./build/publish.ps1 -Variant folder
#>
param(
    [ValidateSet('single', 'single-native', 'single-compressed', 'folder')]
    [string]$Variant = 'single',
    [string]$Runtime = 'win-x64',
    [string]$Configuration = 'Release',
    # Skip ReadyToRun precompilation: much smaller output, more JIT work at startup.
    [switch]$NoReadyToRun
)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$project = Join-Path $root 'src\FinanceOS.App\FinanceOS.App.csproj'
$suffix = if ($NoReadyToRun) { '-jit' } else { '' }
$out = Join-Path $root "artifacts\publish\$Runtime-$Variant$suffix"

$publishArgs = @(
    'publish', $project,
    '-c', $Configuration,
    '-r', $Runtime,
    '--self-contained', 'true',
    "-p:PublishReadyToRun=$(-not $NoReadyToRun)",
    '-p:DebugType=None',
    '-o', $out
)
switch ($Variant) {
    'single'            { $publishArgs += '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true' }
    'single-native'     { $publishArgs += '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=false' }
    'single-compressed' { $publishArgs += '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:EnableCompressionInSingleFile=true' }
    'folder'            { }
}

# Only ever clears this script's own output folder under artifacts/.
if (Test-Path $out) { Remove-Item $out -Recurse -Force }

& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

$files = Get-ChildItem $out -Recurse -File
$exe = Get-Item (Join-Path $out 'FinanceOS.exe')
'{0}: FinanceOS.exe {1:N1} MB | folder {2:N1} MB in {3} files | {4}' -f `
    $Variant, ($exe.Length / 1MB), (($files | Measure-Object Length -Sum).Sum / 1MB), $files.Count, $out
