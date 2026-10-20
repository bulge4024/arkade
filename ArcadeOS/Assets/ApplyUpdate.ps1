[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$Payload,
    [Parameter(Mandatory=$true)][string]$InstallDirectory,
    [Parameter(Mandatory=$true)][int]$ParentId,
    [switch]$NoLaunch,
    [string]$ShortcutDirectory,
    [string]$ResultFile
)
$ErrorActionPreference = 'Stop'
$Payload = [IO.Path]::GetFullPath($Payload).TrimEnd('\')
$InstallDirectory = [IO.Path]::GetFullPath($InstallDirectory).TrimEnd('\')
# Release BOTH PowerShell's provider location and the Win32 process working-directory handle.
# Older updaters launch this helper from the installation being replaced.
[Environment]::CurrentDirectory = $PSScriptRoot
Set-Location -LiteralPath $PSScriptRoot
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
if ([string]::IsNullOrWhiteSpace($ResultFile)) { $ResultFile = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'ArcadeOS\Updates\last-install.txt' }
function Write-InstallResult([string]$message) {
    [IO.File]::WriteAllText($log, $message)
    try { New-Item -ItemType Directory -Path (Split-Path $ResultFile -Parent) -Force | Out-Null; [IO.File]::WriteAllText($ResultFile, $message) } catch { }
}
function Refresh-ArkadeShortcuts {
    $folders = if ($ShortcutDirectory) { @($ShortcutDirectory) } else { @([Environment]::GetFolderPath('Desktop'), (Join-Path ([Environment]::GetFolderPath('StartMenu')) 'Programs')) }
    $shell = New-Object -ComObject WScript.Shell
    foreach ($folder in $folders) {
        New-Item -ItemType Directory -Path $folder -Force | Out-Null
        $paths = @((Join-Path $folder 'Arkade Test.lnk'))
        foreach ($link in (Get-ChildItem -LiteralPath $folder -Filter '*.lnk' -File -ErrorAction SilentlyContinue)) {
            $target = $shell.CreateShortcut($link.FullName).TargetPath
            if ($target -eq (Join-Path $InstallDirectory 'Arkade.exe') -or $target -eq (Join-Path $backup 'Arkade.exe')) { $paths += $link.FullName }
        }
        foreach ($path in ($paths | Select-Object -Unique)) {
            # A fresh link drops Shell link-tracking IDs that can follow the old EXE into the backup.
            $temporary = Join-Path $folder ([Guid]::NewGuid().ToString('N') + '.lnk')
            $shortcut = $shell.CreateShortcut($temporary)
            $shortcut.TargetPath = Join-Path $InstallDirectory 'Arkade.exe'
            $shortcut.WorkingDirectory = $InstallDirectory
            $shortcut.IconLocation = (Join-Path $InstallDirectory 'Arkade.exe') + ',0'
            $shortcut.Description = 'Arkade cabinet frontend'
            $shortcut.Save()
            Move-Item -LiteralPath $temporary -Destination $path -Force
        }
    }
}
try {
    if ($Payload -eq $InstallDirectory -or $Payload.StartsWith($InstallDirectory + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Update staging must be outside the installation.' }
    if (-not (Test-Path -LiteralPath (Join-Path $Payload 'Arkade.exe')) -or -not (Test-Path -LiteralPath (Join-Path $InstallDirectory 'Arkade.exe'))) { throw 'The update or existing installation is incomplete.' }
    # Prepare the full replacement before asking Arkade to exit. This also checks folder permissions.
    New-Item -ItemType Directory -Path $replacement | Out-Null
    Get-ChildItem -LiteralPath $Payload -Force | Copy-Item -Destination $replacement -Recurse -Force
    $buildInfo = Join-Path (Split-Path $Payload -Parent) 'BUILD-INFO.json'
    $expectedVersion = $null
    if (Test-Path -LiteralPath $buildInfo) {
        $expectedVersion = [Version](Get-Content -LiteralPath $buildInfo -Raw | ConvertFrom-Json).Version
        $fileVersion = [Version]([Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $replacement 'Arkade.dll')).FileVersion)
        if ($fileVersion.ToString(3) -ne $expectedVersion.ToString(3)) { throw 'The downloaded app version does not match the update package.' }
        Copy-Item -LiteralPath $buildInfo -Destination (Join-Path $replacement 'BUILD-INFO.json') -Force
    } elseif (-not $NoLaunch) { throw 'The update build information is missing.' }
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
    $shortcutNote = ''
    try { Refresh-ArkadeShortcuts } catch { $shortcutNote = ' Shortcut refresh failed: ' + $_.Exception.Message }
    Write-InstallResult "Update installed $(if($expectedVersion){$expectedVersion.ToString(3)}). Folder: $InstallDirectory. Previous version retained at $backup.$shortcutNote"
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
    Write-InstallResult ('Update failed: ' + $message + '. Folder: ' + $InstallDirectory)
    # Before readiness the running app shows the error. After readiness, relaunch the original.
    if ($parentExited -and -not $NoLaunch -and (Test-Path -LiteralPath (Join-Path $InstallDirectory 'Arkade.exe'))) {
        Start-Process -FilePath (Join-Path $InstallDirectory 'Arkade.exe') -WorkingDirectory $InstallDirectory
    }
    exit 1
}
