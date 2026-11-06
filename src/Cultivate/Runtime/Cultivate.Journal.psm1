#requires -Version 5.1
# Cultivate deployment journal: per-destination lock, state file, backups, SHA-256 verified copies and restore.
# The journal format (Version 1) is shared with the original WebfarmWorkspace utility, so deployments made by
# either tool stay restorable. Called only through Bridge.ps1.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$script:Mappings = @{}
$script:Kept = @()

function Get-WorkspacePath([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path) -or -not [IO.Path]::IsPathRooted($Path) -or
        [IO.Path]::GetPathRoot($Path).EndsWith(':')) {
        throw "An absolute path is required: '$Path'."
    }
    $fullPath = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    if ($fullPath -eq [IO.Path]::GetPathRoot($fullPath).TrimEnd('\')) { throw 'Choose a directory below the drive/share root.' }
    $fullPath
}

function Get-WorkspaceContext {
    param([string]$WebfarmRoot, [string]$StateRoot = "$env:LOCALAPPDATA\Meyer\WebfarmWorkspace")
    $root = Get-WorkspacePath $WebfarmRoot
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $key = [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($root.ToUpperInvariant()))).Replace('-', '') }
    finally { $sha.Dispose() }
    $directory = Join-Path (Get-WorkspacePath $StateRoot) $key
    [IO.Directory]::CreateDirectory($directory) | Out-Null
    [pscustomobject]@{ Root = $root; Directory = $directory; StateFile = Join-Path $directory 'workspace.json' }
}

function Invoke-WorkspaceLock($Context, [scriptblock]$Action) {
    $lockPath = Join-Path $Context.Directory 'workspace.lock'
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    $stream = $null
    while ($null -eq $stream) {
        try { $stream = [IO.File]::Open($lockPath, 'OpenOrCreate', 'ReadWrite', 'None') }
        catch [IO.IOException] {
            if ([DateTime]::UtcNow -ge $deadline) { throw "Workspace is busy. Retry after the current copy or switch completes." }
            Start-Sleep -Milliseconds 100
        }
    }
    try { & $Action } finally { $stream.Dispose() }
}

function Read-WorkspaceState($Context) {
    if (Test-Path -LiteralPath $Context.StateFile) {
        $state = Get-Content -LiteralPath $Context.StateFile -Encoding UTF8 -Raw | ConvertFrom-Json
        if ($state.Version -ne 1 -or $state.WebfarmRoot -ne $Context.Root) { throw 'Invalid workspace state. Do not delete the deployment backups.' }
        return $state
    }
}

function Save-WorkspaceState($Context, $State) {
    $temporary = "$($Context.StateFile).tmp"
    [IO.File]::WriteAllText($temporary, ($State | ConvertTo-Json -Depth 8))
    if (Test-Path -LiteralPath $Context.StateFile) { [IO.File]::Replace($temporary, $Context.StateFile, [System.Management.Automation.Language.NullString]::Value) }
    else { [IO.File]::Move($temporary, $Context.StateFile) }
}

function Assert-WorkspaceChildPath([string]$Path, [string]$Root) {
    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not $fullPath.StartsWith($Root.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "Path escapes its workspace: '$Path'."
    }
    $current = $fullPath
    while ($current.Length -ge $Root.Length) {
        if ((Test-Path -LiteralPath $current) -and
            ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Linked files/directories cannot be deployed: '$current'."
        }
        $current = [IO.Path]::GetDirectoryName($current)
    }
    $fullPath
}

function Get-WorkspaceHash([string]$Path) {
    if (Test-Path -LiteralPath $Path -PathType Leaf) { return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }
    return $null
}

function Get-WorkspaceFileMapping([string]$FileName, [string]$AssemblyPath) {
    if ($script:Mappings.ContainsKey($FileName)) { return $script:Mappings[$FileName] }
    if ([string]::IsNullOrWhiteSpace($AssemblyPath) -or -not (Test-Path -LiteralPath $AssemblyPath)) {
        throw "Missing mapping dependency '$AssemblyPath'. Build MeyerObjects in your normal checkout or set MappingAssembly with 'cultivate setup'."
    }
    [Reflection.Assembly]::LoadFrom([IO.Path]::GetFullPath($assemblyPath)) | Out-Null
    $connection = New-Object Data.SqlClient.SqlConnection ([Meyer.Objects.DataBase.Connection]::MeyerLogicAoConnectionString)
    try {
        $connection.Open()
        $command = $connection.CreateCommand()
        try {
            $command.CommandText = 'SELECT FilePath FROM MEYER..MY_CodeRelease_WebfarmFiles WITH (NOLOCK) WHERE FileName = @FileName'
            $command.Parameters.Add('@FileName', [Data.SqlDbType]::VarChar, 260).Value = $FileName
            $reader = $command.ExecuteReader()
            try {
                $folders = @()
                while ($reader.Read()) { if (-not $reader.IsDBNull(0)) { $folders += $reader.GetString(0) } }
            } finally { $reader.Dispose() }
            $folders = @($folders | Sort-Object -Unique)
            if ($folders.Count -gt 1) { throw "Ambiguous Code Release Manager mapping for '$FileName'." }
            $folder = $null
            if ($folders.Count -eq 1) { $folder = $folders[0] }
            else { Write-Warning "No Code Release Manager mapping for '$FileName'; not deployed." }
            $script:Mappings[$FileName] = $folder
            $folder
        } finally { $command.Dispose() }
    } finally { $connection.Dispose() }
}

function Copy-WorkspaceFile($Context, $State, [string]$Source, [string]$RelativePath, [switch]$Delete) {
    $sourcePath = Assert-WorkspaceChildPath $Source $State.WorkspaceRoot
    if ([IO.Path]::IsPathRooted($RelativePath)) { throw "A deployment mapping must be relative: '$RelativePath'." }
    $destination = Assert-WorkspaceChildPath (Join-Path $Context.Root $RelativePath) $Context.Root
    $relative = $destination.Substring($Context.Root.Length + 1)
    $entry = @($State.Files | Where-Object { $_.RelativePath -eq $relative })
    if ($entry.Count -gt 1) { throw "Duplicate deployment journal entry: '$relative'." }
    $snapshot = Join-Path $Context.Directory ([Guid]::NewGuid().ToString('N') + '.upload')
    try {
        $sourceHash = $null
        if (-not $Delete) {
            if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) { throw "Build or source file is missing: '$sourcePath'." }
            [IO.File]::Copy($sourcePath, $snapshot)
            $sourceHash = Get-WorkspaceHash $snapshot
        }
        $destinationHash = Get-WorkspaceHash $destination
        if ($entry.Count -eq 1 -and $destinationHash -ne $entry[0].DeployedHash -and $destinationHash -ne $entry[0].OriginalHash) {
            # The outside change becomes the original this deployment backs up and later restores.
            Remove-WorkspaceEntry $Context $State $entry[0]
            Write-Host "[REPLACED] $destination changed outside Cultivate; backing it up before copying."
            $entry = @()
        }
        if ($entry.Count -eq 1) {
            $entry = $entry[0]
            if ($entry.Source -ne $sourcePath) { throw "Multiple source files map to '$destination'." }
            if ($sourceHash -eq $destinationHash) { return }
        } else {
            $backup = [Guid]::NewGuid().ToString('N') + '.backup'
            if ($destinationHash) { [IO.File]::Copy($destination, (Join-Path $Context.Directory $backup)) }
            $entry = [pscustomobject]@{
                RelativePath = $relative; Backup = $backup; OriginalHash = $destinationHash
                DeployedHash = $null; Source = $sourcePath
            }
            $State.Files = @($State.Files) + $entry
        }
        $entry.DeployedHash = $sourceHash
        Save-WorkspaceState $Context $State
        if ($sourceHash -eq $destinationHash) { return }
        if ($Delete) { [IO.File]::Delete($destination) }
        else {
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
            [IO.File]::Copy($snapshot, $destination, $true)
        }
        if ((Get-WorkspaceHash $destination) -ne $sourceHash) { throw "Copy verification failed: '$destination'." }
        if ($Delete) { Write-Host "[DELETED] $destination" }
        else { Write-Host "[OK] $sourcePath -> $destination" }
    } finally {
        if (Test-Path -LiteralPath $snapshot) { [IO.File]::Delete($snapshot) }
    }
}

function Test-WorkspaceFileChanged($Context, $Entry) {
    $hash = Get-WorkspaceHash (Assert-WorkspaceChildPath (Join-Path $Context.Root $Entry.RelativePath) $Context.Root)
    $hash -ne $Entry.DeployedHash -and $hash -ne $Entry.OriginalHash
}

function Remove-WorkspaceEntry($Context, $State, $Entry) {
    $State.Files = @($State.Files | Where-Object { $_.RelativePath -ne $Entry.RelativePath })
    Save-WorkspaceState $Context $State
    $backupPath = Join-Path $Context.Directory $Entry.Backup
    if (Test-Path -LiteralPath $backupPath) { [IO.File]::Delete($backupPath) }
}

function Assert-WorkspaceFiles($Context, $Entries) {
    foreach ($entry in $Entries) {
        # Files changed outside Cultivate, such as by a manual build, are kept rather than restored.
        if (Test-WorkspaceFileChanged $Context $entry) { continue }
        if ($entry.OriginalHash -and
            (Get-WorkspaceHash (Join-Path $Context.Directory $entry.Backup)) -ne $entry.OriginalHash) {
            throw "Missing or damaged backup for '$(Join-Path $Context.Root $entry.RelativePath)'."
        }
    }
}

function Restore-WorkspaceFiles($Context, $State, $Entries = $State.Files) {
    Assert-WorkspaceFiles $Context $Entries
    foreach ($entry in @($Entries)) {
        $destination = Join-Path $Context.Root $entry.RelativePath
        if (Test-WorkspaceFileChanged $Context $entry) {
            Remove-WorkspaceEntry $Context $State $entry
            $script:Kept += $entry.RelativePath
            Write-Host "[KEPT] $destination changed outside Cultivate; leaving it as is."
            continue
        }
        $backupPath = Join-Path $Context.Directory $entry.Backup
        if ($entry.OriginalHash) { [IO.File]::Copy($backupPath, $destination, $true) }
        elseif (Test-Path -LiteralPath $destination) { [IO.File]::Delete($destination) }
        if ((Get-WorkspaceHash $destination) -ne $entry.OriginalHash) { throw "Restore verification failed: '$destination'." }
        $State.Files = @($State.Files | Where-Object { $_.RelativePath -ne $entry.RelativePath })
        Save-WorkspaceState $Context $State
        if (Test-Path -LiteralPath $backupPath) { [IO.File]::Delete($backupPath) }
        Write-Host "[RESTORED] $destination"
    }
}

function Disable-WebfarmWorkspace($Context, [string]$ExpectedToken) {
    Invoke-WorkspaceLock $Context {
        $state = Read-WorkspaceState $Context
        if ($null -eq $state) { return }
        if ($ExpectedToken -and $state.Token -ne $ExpectedToken) { return }
        Assert-WorkspaceFiles $Context $state.Files
        $state.Active = $false
        $state.Token = [Guid]::NewGuid().ToString('N')
        Save-WorkspaceState $Context $state
        Restore-WorkspaceFiles $Context $state
        Write-Host '[STOPPED] Previous webfarm files restored.'
    }
}

Export-ModuleMember -Function Get-WorkspaceContext, Read-WorkspaceState, Disable-WebfarmWorkspace
