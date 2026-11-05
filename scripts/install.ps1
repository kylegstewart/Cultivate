#requires -Version 5.1
<#
.SYNOPSIS
    Installs or upgrades Cultivate for the current Windows user.
.DESCRIPTION
    * Copies Cultivate to -InstallDir (default %LOCALAPPDATA%\Programs\Cultivate).
    * Adds that folder to your user PATH, so `cultivate` works in any new terminal.
    * Installs Cultivate's per-user MSBuild hook and replaces the old WebfarmWorkspace hook, if present.
    * Creates Start menu and desktop shortcuts.

    Run it from a clone of the repository (builds from source first), from an extracted release package,
    or pass -Package with a release zip. Re-run it to upgrade. Settings, logs, and the deployment journal
    in %LOCALAPPDATA%\Meyer\Cultivate are never touched.
.EXAMPLE
    .\scripts\install.ps1
.EXAMPLE
    .\install.ps1 -Package .\Cultivate-1.1.0-win-x64.zip -Force
#>
[CmdletBinding()]
param(
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'Programs\Cultivate'),
    [string]$Package,
    [switch]$NoDesktopShortcut,
    [switch]$NoPath,
    # Stop Cultivate processes (such as the saved-file worker) running from the install folder.
    [switch]$Force
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$InstallDir = [IO.Path]::GetFullPath($InstallDir).TrimEnd('\')
$dataHome = if ($env:CULTIVATE_HOME) { $env:CULTIVATE_HOME } else { Join-Path $env:LOCALAPPDATA 'Meyer\Cultivate' }
$hookDirectories = @('Current', '15.0') | ForEach-Object { Join-Path $env:LOCALAPPDATA "Microsoft\MSBuild\$_\Microsoft.Common.targets\ImportAfter" }
$extracted = $null

try {
    # 1. Find the files to install.
    if ($Package) {
        $extracted = Join-Path ([IO.Path]::GetTempPath()) ('cultivate-' + [Guid]::NewGuid().ToString('N'))
        Expand-Archive -LiteralPath $Package -DestinationPath $extracted
        $source = $extracted
    } elseif (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'Cultivate.exe')) {
        $source = $PSScriptRoot
    } elseif (Test-Path -LiteralPath (Join-Path $PSScriptRoot '..\src\Cultivate\Cultivate.csproj')) {
        & (Join-Path $PSScriptRoot 'build.ps1')
        $source = Join-Path (Split-Path $PSScriptRoot) 'artifacts\publish'
    } else {
        throw 'Run install.ps1 from a Cultivate repository clone or an extracted release package, or pass -Package <zip>.'
    }
    $source = [IO.Path]::GetFullPath($source).TrimEnd('\')
    if ($source -eq $InstallDir) { throw "Cultivate is already installed in $InstallDir. Run install.ps1 from a repository clone or a newer package to upgrade." }
    foreach ($name in 'Cultivate.exe', 'Bridge.ps1', 'Cultivate.Journal.psm1', 'Capture.targets', 'Cultivate.Build.targets', 'Cultivate.ico') {
        if (-not (Test-Path -LiteralPath (Join-Path $source $name))) { throw "The package is incomplete: $name is missing from $source." }
    }

    # 2. A running worker locks Cultivate.exe.
    $running = @(Get-Process -Name Cultivate -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path.StartsWith($InstallDir + '\', [StringComparison]::OrdinalIgnoreCase) })
    if ($running.Count) {
        if (-not $Force) {
            throw ("Cultivate is running from $InstallDir (PID $($running.Id -join ', ')). Close it, or re-run with -Force. " +
                'Stopping the saved-file worker keeps the current deployment; activate again afterwards to resume automatic updates.')
        }
        $running | Stop-Process -Force
        $running | Wait-Process -Timeout 15 -ErrorAction SilentlyContinue
        Write-Host "Stopped Cultivate (PID $($running.Id -join ', '))."
    }

    # 3. Copy into a staging folder, then swap, so a failed copy leaves the previous version intact.
    $staging = "$InstallDir.new"
    $previous = "$InstallDir.old"
    foreach ($path in $staging, $previous) { if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force } }
    New-Item -ItemType Directory -Force -Path $staging | Out-Null
    Get-ChildItem -LiteralPath $source | Where-Object Name -ne 'install.ps1' | Copy-Item -Destination $staging -Recurse
    if (Test-Path -LiteralPath $InstallDir) { Rename-Item -LiteralPath $InstallDir -NewName (Split-Path $previous -Leaf) }
    Rename-Item -LiteralPath $staging -NewName (Split-Path $InstallDir -Leaf)
    if (Test-Path -LiteralPath $previous) { Remove-Item -LiteralPath $previous -Recurse -Force }
    $exe = Join-Path $InstallDir 'Cultivate.exe'
    $icon = Join-Path $InstallDir 'Cultivate.ico'

    # 4. Per-user MSBuild hook.
    $targets = [Security.SecurityElement]::Escape((Join-Path $InstallDir 'Cultivate.Build.targets'))
    $hookContent = "<Project xmlns=`"http://schemas.microsoft.com/developer/msbuild/2003`">`r`n  <Import Project=`"$targets`"`r`n" +
        "          Condition=`"'`$(CultivateRunDirectory)' != '' and Exists('$targets')`" />`r`n</Project>`r`n"
    $hooks = @()
    foreach ($directory in $hookDirectories) {
        New-Item -ItemType Directory -Force -Path $directory | Out-Null
        $hook = Join-Path $directory 'Meyer.Cultivate.targets'
        [IO.File]::WriteAllText($hook, $hookContent)
        $hooks += $hook
        # The original WebfarmWorkspace hook would intercept the same builds a second time.
        $legacy = Join-Path $directory 'Meyer.WebfarmWorkspace.targets'
        if (Test-Path -LiteralPath $legacy) {
            if ((Get-Content -LiteralPath $legacy -Raw) -match 'WebfarmWorkspace\\App\\Webfarm\.Local\.targets') {
                $backup = Join-Path $dataHome 'Migrated'
                New-Item -ItemType Directory -Force -Path $backup | Out-Null
                $version = Split-Path (Split-Path (Split-Path $directory)) -Leaf
                Copy-Item -LiteralPath $legacy -Destination (Join-Path $backup "$version-Meyer.WebfarmWorkspace.targets") -Force
                Remove-Item -LiteralPath $legacy
                Write-Host "Replaced the old WebfarmWorkspace MSBuild hook ($version). A copy is in $backup."
            } else {
                Write-Warning "Left an unrecognized $legacy in place. Remove it if it duplicates Cultivate's hook."
            }
        }
    }

    # 5. User PATH, preserving unexpanded %VARIABLES% in existing entries.
    if (-not $NoPath) {
        $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey('Environment')
        try {
            $current = [string]$key.GetValue('Path', '', [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
            $entries = @($current -split ';' | Where-Object { $_ })
            if (-not ($entries | Where-Object { $_.TrimEnd('\') -ieq $InstallDir })) {
                $key.SetValue('Path', ((@($entries) + $InstallDir) -join ';'), [Microsoft.Win32.RegistryValueKind]::ExpandString)
                Write-Host "Added $InstallDir to your user PATH."
            }
        } finally { $key.Dispose() }
        if (-not ('CultivateInstall.Native' -as [type])) {
            Add-Type -Namespace CultivateInstall -Name Native -MemberDefinition @'
[DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, UIntPtr wParam, string lParam, uint flags, uint timeout, out UIntPtr result);
'@
        }
        $ignored = [UIntPtr]::Zero
        # WM_SETTINGCHANGE to all windows so newly opened terminals see the new PATH.
        [void][CultivateInstall.Native]::SendMessageTimeout([IntPtr]0xffff, 0x1A, [UIntPtr]::Zero, 'Environment', 2, 5000, [ref]$ignored)
        if (-not (($env:Path -split ';') | Where-Object { $_.TrimEnd('\') -ieq $InstallDir })) { $env:Path = "$env:Path;$InstallDir" }
    }

    # 6. Shortcuts. Also retarget the shortcuts made by the earlier local-folder install.
    $shell = New-Object -ComObject WScript.Shell
    $desktop = [Environment]::GetFolderPath('Desktop')
    $shortcuts = @(Join-Path ([Environment]::GetFolderPath('Programs')) 'Cultivate.lnk')
    if (-not $NoDesktopShortcut) { $shortcuts += Join-Path $desktop 'Cultivate.lnk' }
    $legacyExe = Join-Path $env:LOCALAPPDATA 'Meyer\Cultivate\App\Cultivate.exe'
    foreach ($name in 'Cultivate.lnk', 'Webfarm Workspace.lnk') {
        $path = Join-Path $desktop $name
        if ((Test-Path -LiteralPath $path) -and $path -notin $shortcuts) {
            $existing = $shell.CreateShortcut($path)
            if ($existing.TargetPath -ieq $legacyExe -or $existing.Arguments -match 'Activate-WebfarmWorkspace\.ps1') { $shortcuts += $path }
        }
    }
    foreach ($path in $shortcuts) {
        $shortcut = $shell.CreateShortcut($path)
        $shortcut.TargetPath = $exe
        $shortcut.Arguments = ''
        $shortcut.WorkingDirectory = $env:USERPROFILE
        $shortcut.Description = 'Cultivate - preview, build, and activate a development version on your webfarm'
        $shortcut.IconLocation = "$icon,0"
        $shortcut.Save()
    }

    # 7. Record what was installed, for uninstall.ps1 and troubleshooting.
    $version = (Get-Item -LiteralPath $exe).VersionInfo.ProductVersion
    [ordered]@{
        Version = $version; InstalledAt = (Get-Date).ToString('o'); InstallDir = $InstallDir
        OnPath = -not $NoPath; Hooks = $hooks; Shortcuts = $shortcuts
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $InstallDir 'installation.json') -Encoding UTF8

    Write-Host ''
    Write-Host "Installed Cultivate $version in $InstallDir" -ForegroundColor Green
    if (-not $NoPath) { Write-Host 'Open a new terminal and run:  cultivate' }
    Write-Host 'Check prerequisites with:     cultivate doctor'
    Write-Host 'Get new versions later with:  cultivate update'
    Write-Host 'Reload open Visual Studio solutions so they load the MSBuild hook.'
} finally {
    if ($extracted -and (Test-Path -LiteralPath $extracted)) { Remove-Item -LiteralPath $extracted -Recurse -Force }
}
