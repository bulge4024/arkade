$ErrorActionPreference = 'Stop'
$root = Join-Path ([IO.Path]::GetTempPath()) ('Arkade-install-check-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
try {
    $installed = Join-Path $root 'installed'
    $payload = Join-Path $root 'stage\extracted\package\app'
    $helperFolder = Join-Path $root 'stage\extracted'
    $shortcuts = Join-Path $root 'shortcuts'
    $resultFile = Join-Path $root 'last-install.txt'
    New-Item -ItemType Directory -Path $installed,$payload -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $installed 'Arkade.exe'), 'original fixture - not executable')
    [IO.File]::WriteAllText((Join-Path $installed 'old-only.txt'), 'old')
    [IO.File]::WriteAllText((Join-Path $payload 'Arkade.exe'), 'updated fixture - not executable')
    [IO.File]::WriteAllText((Join-Path $payload 'retrobat.settings.json'), '{"InstallationPath":"wrong default"}')
    $testAssembly = Join-Path $PSScriptRoot 'bin\Release\net10.0-windows\UpdaterChecks.dll'
    Copy-Item -LiteralPath $testAssembly -Destination (Join-Path $payload 'Arkade.dll')
    $expectedVersion = ([Version]([Diagnostics.FileVersionInfo]::GetVersionInfo($testAssembly).FileVersion)).ToString(3)
    @{Version=$expectedVersion} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path (Split-Path $payload -Parent) 'BUILD-INFO.json')
    New-Item -ItemType Directory -Path $shortcuts | Out-Null
    $shell = New-Object -ComObject WScript.Shell
    foreach($name in @('Arkade Test.lnk','My Cabinet.lnk')) {
        $link=$shell.CreateShortcut((Join-Path $shortcuts $name));$link.TargetPath=Join-Path $installed 'Arkade.exe';$link.WorkingDirectory=$installed;$link.Save()
    }
    $settings = '{"InstallationPath":"D:\\MyRetroBat","RomsRoot":"E:\\Games","LaunchProfiles":{"cabinet":"custom"}}'
    [IO.File]::WriteAllText((Join-Path $installed 'retrobat.settings.json'), $settings)
    $helper = Join-Path $helperFolder 'ApplyUpdate.ps1'
    $assets=Join-Path $installed 'Assets'
    New-Item -ItemType Directory -Path $assets | Out-Null
    [IO.File]::WriteAllText((Join-Path $assets 'ApplyUpdate.ps1'),'# Old helper fixture')
    & (Join-Path $PSScriptRoot '..\..\scripts\Repair-ArkadeUpdate.ps1') -InstallDirectory $installed -ShortcutDirectory $shortcuts
    if([IO.File]::ReadAllText((Join-Path $installed 'Arkade.exe')) -ne 'original fixture - not executable' -or [IO.File]::ReadAllText((Join-Path $installed 'retrobat.settings.json')) -ne $settings){throw 'Bootstrap repair modified application files or settings.'}
    if((Get-FileHash (Join-Path $assets 'ApplyUpdate.ps1')).Hash -ne (Get-FileHash (Join-Path $PSScriptRoot '..\..\ArcadeOS\Assets\ApplyUpdate.ps1')).Hash){throw 'Bootstrap repair did not patch the helper.'}
    Copy-Item -LiteralPath (Join-Path $assets 'ApplyUpdate.ps1') -Destination $helper
    $powershell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    # Reproduce a desktop shortcut launch: the updater INHERITS the directory it must replace.
    $info = New-Object Diagnostics.ProcessStartInfo
    $info.FileName=$powershell; $info.UseShellExecute=$false; $info.CreateNoWindow=$true; $info.WorkingDirectory=$installed
    $info.Arguments='-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "'+$helper+'" -Payload "'+$payload+'" -InstallDirectory "'+$installed+'" -ParentId 0 -NoLaunch -ShortcutDirectory "'+$shortcuts+'" -ResultFile "'+$resultFile+'"'
    $process=[Diagnostics.Process]::Start($info)
    if (-not $process.WaitForExit(30000) -or $process.ExitCode -ne 0) { throw ('Isolated install failed: '+(Get-Content (Join-Path $helperFolder 'install-result.txt') -Raw)) }
    if ([IO.File]::ReadAllText((Join-Path $installed 'Arkade.exe')) -ne 'updated fixture - not executable') { throw 'New application not installed.' }
    if ([IO.File]::ReadAllText((Join-Path $installed 'retrobat.settings.json')) -ne $settings) { throw 'Cabinet settings were changed.' }
    if (Test-Path -LiteralPath (Join-Path $installed 'old-only.txt')) { throw 'Stale application files survived the swap.' }
    $backup = Get-ChildItem -LiteralPath $root -Directory -Filter 'installed.backup-*' | Select-Object -First 1
    if (-not $backup -or [IO.File]::ReadAllText((Join-Path $backup.FullName 'Arkade.exe')) -ne 'original fixture - not executable') { throw 'Original version not retained.' }
    foreach($name in @('Arkade.lnk','My Cabinet.lnk')) {
        $link=$shell.CreateShortcut((Join-Path $shortcuts $name))
        if($link.TargetPath -ne (Join-Path $installed 'Arkade.exe') -or $link.WorkingDirectory -ne $installed){throw 'Shortcut still points to old version or backup.'}
    }
    if(Test-Path -LiteralPath (Join-Path $shortcuts 'Arkade Test.lnk')){throw 'Legacy test shortcut was not migrated.'}
    if(-not (Test-Path -LiteralPath (Join-Path $installed 'BUILD-INFO.json')) -or -not ([IO.File]::ReadAllText($resultFile)).Contains($expectedVersion)){throw 'Installed version/result was not recorded.'}
    # A missing payload must leave the installed version untouched.
    & $powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $helper -Payload (Join-Path $root 'missing') -InstallDirectory $installed -ParentId 0 -NoLaunch -ShortcutDirectory $shortcuts -ResultFile $resultFile
    if ($LASTEXITCODE -eq 0 -or [IO.File]::ReadAllText((Join-Path $installed 'Arkade.exe')) -ne 'updated fixture - not executable') { throw 'Failed preparation damaged the installation.' }
    $global:LASTEXITCODE = 0 # The deliberately failing child above was expected and asserted.
    Write-Output 'PASS: inherited installation working-directory regression, Windows PowerShell 5 installer, staged replacement, version verification, exact settings preservation, shortcut repair, backup and safe preparation failure. No real app was launched; only temporary shortcuts changed.'
}
finally {
    $resolved = [IO.Path]::GetFullPath($root)
    if (-not $resolved.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()),[StringComparison]::OrdinalIgnoreCase) -or (Split-Path $resolved -Leaf) -notlike 'Arkade-install-check-*') { throw 'Unsafe test cleanup path.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
