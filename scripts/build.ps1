#requires -Version 5.1
<#
.SYNOPSIS
    Publishes Cultivate as a self-contained win-x64 executable, optionally packaged as a release zip.
.EXAMPLE
    .\scripts\build.ps1             # artifacts\publish
.EXAMPLE
    .\scripts\build.ps1 -Package    # also artifacts\Cultivate-<version>-win-x64.zip
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [string]$Output,  # default: <repository>\artifacts
    [switch]$Package
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot
if (-not $Output) { $Output = Join-Path $root 'artifacts' }
$project = Join-Path $root 'src\Cultivate\Cultivate.csproj'
$publish = Join-Path $Output 'publish'
if (Test-Path -LiteralPath $publish) { Remove-Item -LiteralPath $publish -Recurse -Force }

& dotnet publish $project -nologo -c $Configuration -r $Runtime --self-contained true `
    -p:PublishSingleFile=true -p:DebugType=none -o $publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)." }

# The package installs itself: extract it and run install.ps1.
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'install.ps1'), (Join-Path $PSScriptRoot 'uninstall.ps1') -Destination $publish

$version = (& dotnet msbuild $project -nologo -getProperty:Version).Trim()
Write-Host "Published Cultivate $version to $publish"

if ($Package) {
    $zip = Join-Path $Output "Cultivate-$version-$Runtime.zip"
    if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip }
    Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $zip
    Write-Host "Packaged $zip"
}
