<#
.SYNOPSIS
  Builds the portable release zips into artifacts/release:
    FinanceOS-<version>-win-x64.zip         single exe (M0 choice: no ReadyToRun), the one to use
    FinanceOS-<version>-win-x64-native.zip  exe + native WPF DLLs next to it, for PCs whose policy
                                            blocks DLLs loaded from %TEMP% (ARCHITECTURE R8)
  Each zip holds the app, the Inter font licence and CITESTE.txt. Prints size and SHA-256.

.EXAMPLE
  ./build/release.ps1
  ./build/release-smoke.ps1 -Exe artifacts/publish/win-x64-single-jit/FinanceOS.exe
#>
param([string]$Runtime = 'win-x64')
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$release = Join-Path $root 'artifacts\release'
New-Item -ItemType Directory -Force $release | Out-Null

foreach ($variant in @(@{ Name = 'single'; Suffix = '' }, @{ Name = 'single-native'; Suffix = '-native' })) {
    & (Join-Path $PSScriptRoot 'publish.ps1') -Variant $variant.Name -Runtime $Runtime -NoReadyToRun | Out-Host
    $published = Join-Path $root "artifacts\publish\$Runtime-$($variant.Name)-jit"
    Copy-Item (Join-Path $PSScriptRoot 'release-readme.txt') (Join-Path $published 'CITESTE.txt')

    # Only ever replaces this script's own zip.
    $zip = Join-Path $release "FinanceOS-$version-$Runtime$($variant.Suffix).zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path (Join-Path $published '*') -DestinationPath $zip -CompressionLevel Optimal
    $hash = (Get-FileHash $zip -Algorithm SHA256).Hash
    '{0}  {1:N1} MB  SHA256 {2}' -f (Split-Path $zip -Leaf), ((Get-Item $zip).Length / 1MB), $hash
}
