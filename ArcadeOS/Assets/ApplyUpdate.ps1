[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$Payload,
    [Parameter(Mandatory=$true)][string]$InstallDirectory,
    [Parameter(Mandatory=$true)][int]$ParentId,
    [switch]$NoLaunch
)
$ErrorActionPreference = 'Stop'
$Payload = [IO.Path]::GetFullPath($Payload).TrimEnd('\')
$InstallDirectory = [IO.Path]::GetFullPath($InstallDirectory).TrimEnd('\')
$stageRoot = Split-Path $PSScriptRoot -Parent
$log = Join-Path $PSScriptRoot 'install-result.txt'
$ready = Join-Path $PSScriptRoot 'install.ready'
$cancel = Join-Path $PSScriptRoot 'install.cancel'
$parentDirectory = Split-Path $InstallDirectory -Parent
$leaf = Split-Path $InstallDirectory -Leaf
$replacement = Join-Path $parentDirectory ($leaf + '.update-' + [Guid]::NewGuid().ToString('N'))
$backup = Join-Path $parentDirectory ($leaf + '.backup-' + [Guid]::NewGuid().ToString('N'))
$swapped = $false
$parentExited = $false
try {
    if ($Payload -eq $InstallDirectory -or $Payload.StartsWith($InstallDirectory + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Update staging must be outside the installation.' }
    if (-not (Test-Path -LiteralPath (Join-Path $Payload 'Arkade.exe')) -or -not (Test-Path -LiteralPath (Join-Path $InstallDirectory 'Arkade.exe'))) { throw 'The update or existing installation is incomplete.' }
    # Prepare the full replacement before asking Arkade to exit. This also checks folder permissions.
    New-Item -ItemType Directory -Path $replacement | Out-Null
    Get-ChildItem -LiteralPath $Payload -Force | Copy-Item -Destination $replacement -Recurse -Force
    $settings = Join-Path $InstallDirectory 'retrobat.settings.json'
    if (Test-Path -LiteralPath $settings) { Copy-Item -LiteralPath $settings -Destination (Join-Path $replacement 'retrobat.settings.json') -Force }
    if (Test-Path -LiteralPath $cancel) { throw 'Update preparation was cancelled.' }
    [IO.File]::WriteAllText($ready, 'ready')
    if ($ParentId -gt 0) {
        $parent = Get-Process -Id $ParentId -ErrorAction SilentlyContinue
        if ($parent -and -not $parent.WaitForExit(120000)) { throw 'Arkade did not close in time. No files were replaced.' }
    }
    $parentExited = $true
    if (Test-Path -LiteralPath $cancel) { throw 'Update preparation was cancelled.' }
    # Check again after the app closes in case settings were saved during shutdown.
    if (Test-Path -LiteralPath $settings) { Copy-Item -LiteralPath $settings -Destination (Join-Path $replacement 'retrobat.settings.json') -Force }
    Move-Item -LiteralPath $InstallDirectory -Destination $backup
    try { Move-Item -LiteralPath $replacement -Destination $InstallDirectory; $swapped = $true }
    catch { Move-Item -LiteralPath $backup -Destination $InstallDirectory; throw }
    [IO.File]::WriteAllText($log, "Update installed. Previous version retained at $backup")
    if (-not $NoLaunch) { Start-Process -FilePath (Join-Path $InstallDirectory 'Arkade.exe') -WorkingDirectory $InstallDirectory }
}
catch {
    $message = $_.Exception.Message
    if ($swapped) {
        try {
            $failed = $InstallDirectory + '.failed-' + [Guid]::NewGuid().ToString('N')
            Move-Item -LiteralPath $InstallDirectory -Destination $failed
            Move-Item -LiteralPath $backup -Destination $InstallDirectory
        } catch { $message += ' Automatic restore failed: ' + $_.Exception.Message }
    }
    [IO.File]::WriteAllText($log, 'Update failed: ' + $message)
    # Before readiness the running app shows the error. After readiness, relaunch the original.
    if ($parentExited -and -not $NoLaunch -and (Test-Path -LiteralPath (Join-Path $InstallDirectory 'Arkade.exe'))) {
        Start-Process -FilePath (Join-Path $InstallDirectory 'Arkade.exe') -WorkingDirectory $InstallDirectory
    }
    exit 1
}
