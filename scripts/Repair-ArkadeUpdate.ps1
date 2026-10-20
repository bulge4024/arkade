[CmdletBinding()]
param([string]$InstallDirectory, [string]$ShortcutDirectory)
$ErrorActionPreference='Stop'
$helper=Join-Path $PSScriptRoot 'ApplyUpdate.ps1'
if(-not (Test-Path -LiteralPath $helper)){ $helper=Join-Path $PSScriptRoot '..\ArcadeOS\Assets\ApplyUpdate.ps1' }
if(-not (Test-Path -LiteralPath $helper)){throw 'Extract the complete repair ZIP before running this tool.'}
$shell=New-Object -ComObject WScript.Shell
$desktop=if($ShortcutDirectory){$ShortcutDirectory}else{[Environment]::GetFolderPath('Desktop')}
if([string]::IsNullOrWhiteSpace($InstallDirectory)){
    $link=Join-Path $desktop 'Arkade.lnk'
    if(-not (Test-Path -LiteralPath $link)){$link=Join-Path $desktop 'Arkade Test.lnk'}
    if(Test-Path -LiteralPath $link){$target=$shell.CreateShortcut($link).TargetPath;if($target){$InstallDirectory=Split-Path $target -Parent}}
    if([string]::IsNullOrWhiteSpace($InstallDirectory)){$InstallDirectory=Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs\Arkade-Test'}
    # Shell link tracking may have followed the old executable into an updater backup.
    $canonical=$InstallDirectory -replace '\.backup-[0-9a-fA-F]{32}$',''
    if(Test-Path -LiteralPath (Join-Path $canonical 'Arkade.exe')){$InstallDirectory=$canonical}
}
$InstallDirectory=[IO.Path]::GetFullPath($InstallDirectory).TrimEnd('\')
if(-not (Test-Path -LiteralPath (Join-Path $InstallDirectory 'Arkade.exe'))){throw "Arkade was not found at $InstallDirectory. Run this tool with -InstallDirectory followed by your Arkade folder."}
$running=Get-Process -Name Arkade -ErrorAction SilentlyContinue | Where-Object {$_.Path -eq (Join-Path $InstallDirectory 'Arkade.exe') -or $_.Path -like ($InstallDirectory+'.backup-*\Arkade.exe')}
if($running){throw 'Close Arkade first, then run this repair again. No application files have been changed.'}
$destination=Join-Path $InstallDirectory 'Assets\ApplyUpdate.ps1'
if(-not (Test-Path -LiteralPath $destination)){throw 'This copy does not contain the in-app updater. Install an updater-enabled cabinet build first.'}
$backup=Join-Path $InstallDirectory 'Assets\ApplyUpdate.before-repair.ps1'
if(-not (Test-Path -LiteralPath $backup)){Copy-Item -LiteralPath $destination -Destination $backup}
Copy-Item -LiteralPath $helper -Destination $destination -Force
if((Get-FileHash -LiteralPath $helper).Hash -ne (Get-FileHash -LiteralPath $destination).Hash){throw 'Updater helper verification failed.'}
$folders=if($ShortcutDirectory){@($ShortcutDirectory)}else{@($desktop,(Join-Path ([Environment]::GetFolderPath('StartMenu')) 'Programs'))}
foreach($folder in $folders){
    New-Item -ItemType Directory -Path $folder -Force | Out-Null
    $temporary=Join-Path $folder ([Guid]::NewGuid().ToString('N')+'.lnk')
    $link=$shell.CreateShortcut($temporary);$link.TargetPath=Join-Path $InstallDirectory 'Arkade.exe';$link.WorkingDirectory=$InstallDirectory;$link.IconLocation=(Join-Path $InstallDirectory 'Arkade.exe')+',0';$link.Description='Arkade cabinet frontend';$link.Save()
    Move-Item -LiteralPath $temporary -Destination (Join-Path $folder 'Arkade.lnk') -Force
    $legacy=Join-Path $folder 'Arkade Test.lnk'
    if(Test-Path -LiteralPath $legacy){$oldTarget=$shell.CreateShortcut($legacy).TargetPath;if($oldTarget -eq (Join-Path $InstallDirectory 'Arkade.exe') -or $oldTarget -like ($InstallDirectory+'.backup-*\Arkade.exe')){Remove-Item -LiteralPath $legacy -Force}}
}
Write-Host 'Arkade updater repaired successfully.' -ForegroundColor Green
Write-Host "Application folder: $InstallDirectory"
Write-Host 'Open Arkade using the repaired desktop shortcut, then choose Settings > Check for updates.'
Write-Host 'The next update will now replace the app correctly. Your ROMs and settings were not changed.'
