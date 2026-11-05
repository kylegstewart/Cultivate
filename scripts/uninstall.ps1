#requires -Version 5.1
<#
.SYNOPSIS
    Removes Cultivate's program files, PATH entry, MSBuild hook, and shortcuts for the current user.
.DESCRIPTION
    Refuses while a deployment is active, because Cultivate is what restores the original webfarm files.
    Run `cultivate restore --yes` first. Settings, logs, and the deployment journal are kept.
.EXAMPLE
    & "$env:LOCALAPPDATA\Programs\Cultivate\uninstall.ps1"
#>
[CmdletBinding()]
param(
    [string]$InstallDir,
    # Uninstall even though a deployment is active (its files stay on the webfarm as they are).
    [switch]$Force
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not $InstallDir) {
    $InstallDir = if (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'Cultivate.exe')) { $PSScriptRoot } else { Join-Path $env:LOCALAPPDATA 'Programs\Cultivate' }
}
$InstallDir = [IO.Path]::GetFullPath($InstallDir).TrimEnd('\')
$exe = Join-Path $InstallDir 'Cultivate.exe'
$dataHome = if ($env:CULTIVATE_HOME) { $env:CULTIVATE_HOME } else { Join-Path $env:LOCALAPPDATA 'Meyer\Cultivate' }

# 1. Cultivate is needed to restore an active deployment.
if (Test-Path -LiteralPath $exe) {
    # Windows PowerShell turns redirected native stderr into errors; an unconfigured Cultivate simply has no deployment.
    $ErrorActionPreference = 'Continue'
    try { $status = & $exe status 2>$null } finally { $ErrorActionPreference = 'Stop' }
    if ($LASTEXITCODE -eq 0 -and $status) {
        $state = ($status -join "`n") | ConvertFrom-Json
        if ($null -ne $state -and $state.Active -and -not $Force) {
            throw "A deployment of '$($state.Branch)' is active. Run 'cultivate restore --yes' first, or pass -Force to leave it on the webfarm."
        }
    }
}

# 2. Stop Cultivate processes (the saved-file worker) running from this folder.
$running = @(Get-Process -Name Cultivate -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.StartsWith($InstallDir + '\', [StringComparison]::OrdinalIgnoreCase) })
if ($running.Count) {
    $running | Stop-Process -Force
    $running | Wait-Process -Timeout 15 -ErrorAction SilentlyContinue
}

# 3. MSBuild hooks that import this installation.
foreach ($version in 'Current', '15.0') {
    $hook = Join-Path $env:LOCALAPPDATA "Microsoft\MSBuild\$version\Microsoft.Common.targets\ImportAfter\Meyer.Cultivate.targets"
    if (-not (Test-Path -LiteralPath $hook)) { continue }
    if ((Get-Content -LiteralPath $hook -Raw).IndexOf($InstallDir, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        Remove-Item -LiteralPath $hook
        Write-Host "Removed MSBuild hook: $hook"
    } else { Write-Warning "Kept $hook; it belongs to another Cultivate installation." }
}

# 4. User PATH entry.
$key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey('Environment')
try {
    $current = [string]$key.GetValue('Path', '', [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
    $entries = @($current -split ';' | Where-Object { $_ })
    $remaining = @($entries | Where-Object { $_.TrimEnd('\') -ine $InstallDir })
    if ($remaining.Count -ne $entries.Count) {
        $key.SetValue('Path', ($remaining -join ';'), [Microsoft.Win32.RegistryValueKind]::ExpandString)
        Write-Host "Removed $InstallDir from your user PATH."
    }
} finally { $key.Dispose() }

# 5. Shortcuts that open this installation.
$shell = New-Object -ComObject WScript.Shell
foreach ($folder in [Environment]::GetFolderPath('Programs'), [Environment]::GetFolderPath('Desktop')) {
    foreach ($link in Get-ChildItem -LiteralPath $folder -Filter '*.lnk' -ErrorAction SilentlyContinue) {
        if ($shell.CreateShortcut($link.FullName).TargetPath -ieq $exe) {
            Remove-Item -LiteralPath $link.FullName
            Write-Host "Removed shortcut: $($link.FullName)"
        }
    }
}

# 6. Program files.
if ((Get-Location).Path.StartsWith($InstallDir, [StringComparison]::OrdinalIgnoreCase)) { Set-Location $env:USERPROFILE }
if (Test-Path -LiteralPath $InstallDir) { Remove-Item -LiteralPath $InstallDir -Recurse -Force }
Write-Host "Uninstalled Cultivate from $InstallDir." -ForegroundColor Green
Write-Host "Settings, logs, and deployment history were kept in $dataHome."
