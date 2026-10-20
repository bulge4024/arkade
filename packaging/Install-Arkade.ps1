[CmdletBinding()]
param(
    [string]$RetroBatPath,
    [string]$InstallDirectory,
    [switch]$NoShortcut,
    [switch]$NoLaunch,
    [switch]$NonInteractive
)
$ErrorActionPreference = 'Stop'
if (-not [Environment]::Is64BitOperatingSystem) { throw 'This test build requires 64-bit Windows.' }
$packageApp = Join-Path $PSScriptRoot 'app'
$buildVersion = 'test'
$buildInfo = Join-Path $PSScriptRoot 'BUILD-INFO.json'
if (Test-Path -LiteralPath $buildInfo) { $buildVersion = (Get-Content -LiteralPath $buildInfo -Raw | ConvertFrom-Json).Version }
if (-not (Test-Path -LiteralPath (Join-Path $packageApp 'Arkade.exe'))) { throw 'Extract the entire ZIP before running setup. The app folder is missing.' }
if ([string]::IsNullOrWhiteSpace($InstallDirectory)) { $InstallDirectory = Join-Path $env:LOCALAPPDATA 'Programs\Arkade-Test' }
$InstallDirectory = [IO.Path]::GetFullPath($InstallDirectory)
if ([string]::IsNullOrWhiteSpace($RetroBatPath)) {
    if ($NonInteractive) { $RetroBatPath = 'C:\RetroBat' }
    else {
        $answer = Read-Host 'RetroBat installation folder [C:\RetroBat]'
        $RetroBatPath = if ([string]::IsNullOrWhiteSpace($answer)) { 'C:\RetroBat' } else { $answer.Trim().Trim('"') }
    }
}
$RetroBatPath = [IO.Path]::GetFullPath($RetroBatPath)
if (-not (Test-Path -LiteralPath (Join-Path $RetroBatPath 'emulationstation\emulatorLauncher.exe'))) {
    throw "RetroBat's emulatorLauncher.exe was not found under $RetroBatPath. Install RetroBat or rerun setup with its installation folder."
}
$executable = Join-Path $InstallDirectory 'Arkade.exe'
$running = Get-Process -Name 'Arkade' -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $executable }
if ($running) { throw 'Close the installed Arkade test build before updating it, then run setup again.' }
$settingsFile = Join-Path $InstallDirectory 'retrobat.settings.json'
# Preserve an existing cabinet's custom launch profiles and ROM root during updates.
$settings = if (Test-Path -LiteralPath $settingsFile) { Get-Content -LiteralPath $settingsFile -Raw | ConvertFrom-Json }
    else { Get-Content -LiteralPath (Join-Path $packageApp 'retrobat.settings.json') -Raw | ConvertFrom-Json }
if ($null -eq $settings) { throw 'The RetroBat settings file is empty.' }
$settings | Add-Member -NotePropertyName InstallationPath -NotePropertyValue $RetroBatPath -Force
New-Item -ItemType Directory -Path $InstallDirectory -Force | Out-Null
Get-ChildItem -LiteralPath $packageApp -Force | Copy-Item -Destination $InstallDirectory -Recurse -Force
[IO.File]::WriteAllText($settingsFile, ($settings | ConvertTo-Json -Depth 16), [Text.UTF8Encoding]::new($false))
if (-not $NoShortcut) {
    $shell = New-Object -ComObject WScript.Shell
    foreach ($folder in @([Environment]::GetFolderPath('Desktop'), (Join-Path ([Environment]::GetFolderPath('StartMenu')) 'Programs'))) {
        $shortcut = $shell.CreateShortcut((Join-Path $folder 'Arkade Test.lnk'))
        $shortcut.TargetPath = $executable
        $shortcut.WorkingDirectory = $InstallDirectory
        $shortcut.Description = "Arkade cabinet test build $buildVersion"
        $shortcut.Save()
    }
}
Write-Host "Installed Arkade Test to $InstallDirectory" -ForegroundColor Green
Write-Host "RetroBat: $RetroBatPath"
Write-Host 'Your ROMs and RetroBat configuration were not changed.'
if (-not $NoLaunch -and -not $NonInteractive) {
    $answer = Read-Host 'Start Arkade now? [Y/n]'
    if ($answer -notmatch '^[Nn]') { Start-Process -FilePath $executable -WorkingDirectory $InstallDirectory }
}


