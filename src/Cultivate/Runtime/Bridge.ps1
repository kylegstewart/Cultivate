#requires -Version 5.1
param(
    [Parameter(Mandatory)][ValidateSet('Mappings', 'Status', 'Check', 'Deploy', 'Restore', 'Heartbeat')][string]$Action,
    [Parameter(Mandatory)][string]$InputPath,
    [Parameter(Mandatory)][string]$OutputPath
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module "$PSHOME\Modules\Microsoft.PowerShell.Utility" -Global
$request = Get-Content -LiteralPath $InputPath -Encoding UTF8 -Raw | ConvertFrom-Json
# The journal module ships beside this script in Cultivate's install folder.
Import-Module (Join-Path $PSScriptRoot 'Cultivate.Journal.psm1') -Force -DisableNameChecking
$result = & (Get-Module Cultivate.Journal) {
    param($Action, $request)
    if ($Action -eq 'Mappings') {
        $mappings = @{}
        foreach ($name in $request.Payload.Names) {
            $folder = Get-WorkspaceFileMapping $name $request.MappingAssembly
            if ($folder) { $mappings[$name] = $folder }
        }
        return $mappings
    }
    $context = Get-WorkspaceContext $request.WebfarmRoot $request.StateRoot
    if ($Action -eq 'Status') {
        return (Invoke-WorkspaceLock $context { Read-WorkspaceState $context })
    }
    if ($Action -eq 'Restore') {
        Disable-WebfarmWorkspace $context
        return @{ Restored = $true; Kept = @($script:Kept) }
    }
    if ($Action -eq 'Heartbeat') {
        return (Invoke-WorkspaceLock $context {
            $state = Read-WorkspaceState $context
            if ($null -eq $state -or -not $state.Active -or $state.Token -ne $request.Payload.Token) { return @{ Current = $false } }
            $state.WatcherPid = $request.Payload.ProcessId
            $state.Heartbeat = [DateTime]::UtcNow.ToString('o')
            $state.LastError = $request.Payload.Error
            Save-WorkspaceState $context $state
            return @{ Current = $true }
        })
    }
    if (-not (Test-Path -LiteralPath $context.Root -PathType Container)) { throw "Webfarm is unavailable: $($context.Root)" }
    $configured = [Environment]::GetEnvironmentVariable('WebfarmPath', 'User')
    if ($configured -and (Get-WorkspacePath $configured) -eq $context.Root) {
        $legacy = @(Get-CimInstance Win32_Process -Filter "Name='WebfarmFileSync.exe'" |
            Where-Object { $_.SessionId -eq (Get-Process -Id $PID).SessionId })
        if ($legacy.Count) { throw 'Close the old WebfarmFileSync watcher before activating Cultivate. It could overwrite this deployment.' }
    }
    if ($Action -eq 'Check') {
        return (Invoke-WorkspaceLock $context {
            $state = Read-WorkspaceState $context
            if ($null -ne $state) { Assert-WorkspaceFiles $context $state.Files }
            return @{ Ready = $true }
        })
    }
    $payload = $request.Payload
    Invoke-WorkspaceLock $context {
        $previous = Read-WorkspaceState $context
        $actualToken = ''
        if ($null -ne $previous) { $actualToken = $previous.Token }
        if ($actualToken -ne $payload.ExpectedToken) { throw 'The active workspace changed during preparation. Preview again.' }
        if ($null -ne $previous) { Assert-WorkspaceFiles $context $previous.Files }
        $snapshot = Get-WorkspacePath $payload.Snapshot
        if ($snapshot -eq $context.Root -or $snapshot.StartsWith($context.Root + '\', [StringComparison]::OrdinalIgnoreCase) -or
            $context.Root.StartsWith($snapshot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Source and webfarm cannot overlap.' }
        $seen = @{}
        foreach ($file in $payload.Files) {
            $source = Assert-WorkspaceChildPath $file.Source $snapshot
            if ([IO.Path]::IsPathRooted($file.RelativePath)) { throw 'Destination paths must be relative.' }
            $destination = Assert-WorkspaceChildPath (Join-Path $context.Root $file.RelativePath) $context.Root
            if ($seen.ContainsKey($destination)) { throw "Duplicate deployment destination: $destination" }
            $seen[$destination] = $true
            if (-not $file.Delete -and (Get-WorkspaceHash $source) -ne $file.Hash) { throw "Staged artifact changed: $source" }
        }
        $inPlace = $payload.WorkerUpdate -and $null -ne $previous -and
            ($previous.PSObject.Properties.Name -contains 'Mode') -and $previous.Mode -eq 'Cultivate' -and
            $previous.SourceRoot -eq $payload.SourceRoot -and $previous.SourceRef -eq $payload.SourceRef -and $previous.Active
        if ($inPlace) {
            $state = $previous
        } elseif ($null -ne $previous) {
            $previous.Active = $false
            $previous.Token = [Guid]::NewGuid().ToString('N')
            Save-WorkspaceState $context $previous
            Restore-WorkspaceFiles $context $previous
        }
        if (-not $inPlace) {
            $state = [pscustomobject]@{
                Version = 1; WebfarmRoot = $context.Root; WorkspaceRoot = $snapshot; Branch = $payload.Branch
                Token = ''; Active = $false; Files = @()
                WatcherPid = 0; Heartbeat = $null; LastError = $null; Mode = 'Cultivate'
                SourceRoot = $payload.SourceRoot; SourceRef = $payload.SourceRef; Fingerprint = ''
                WatchEnabled = $false; BuildFingerprint = ''; RuntimeFiles = @()
            }
        }
        $state.WorkspaceRoot = $snapshot
        $state.Token = [Guid]::NewGuid().ToString('N')
        $state.Fingerprint = $payload.Fingerprint
        $state.BuildFingerprint = $payload.BuildFingerprint
        $state.RuntimeFiles = @($payload.RuntimeFiles)
        $state.WatchEnabled = $payload.WatchEnabled
        $state.LastError = $null
        $state.Active = $false
        $state.WatcherPid = 0
        $state.Heartbeat = $null
        Save-WorkspaceState $context $state
        try {
            if ($inPlace) {
                $wanted = @($payload.Files | ForEach-Object { $_.RelativePath })
                $obsolete = @($state.Files | Where-Object { $_.RelativePath -notin $wanted })
                if ($obsolete.Count) { Restore-WorkspaceFiles $context $state $obsolete }
            }
            foreach ($file in $payload.Files) {
                if ($inPlace) {
                    foreach ($entry in @($state.Files | Where-Object { $_.RelativePath -eq $file.RelativePath })) { $entry.Source = $file.Source }
                }
                Copy-WorkspaceFile $context $state $file.Source $file.RelativePath -Delete:([bool]$file.Delete)
            }
            $state.Active = $true
            Save-WorkspaceState $context $state
        } catch {
            $state.LastError = $_.Exception.Message
            Save-WorkspaceState $context $state
            Restore-WorkspaceFiles $context $state
            throw
        }
        # Reported to Cultivate.exe after the final save, so it is not stored in the journal.
        $state | Add-Member -NotePropertyName Kept -NotePropertyValue @($script:Kept)
        return $state
    }
} $Action $request
$json = 'null'
if ($null -ne $result) { $json = ConvertTo-Json -InputObject $result -Depth 10 }
[IO.File]::WriteAllText($OutputPath, $json)
