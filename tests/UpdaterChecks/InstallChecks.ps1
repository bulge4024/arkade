$ErrorActionPreference = 'Stop'
$root = Join-Path ([IO.Path]::GetTempPath()) ('Arkade-install-check-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
try {
    $installed = Join-Path $root 'installed'
    $payload = Join-Path $root 'stage\extracted\package\app'
    $helperFolder = Join-Path $root 'stage\extracted'
    New-Item -ItemType Directory -Path $installed,$payload -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $installed 'Arkade.exe'), 'original fixture - not executable')
    [IO.File]::WriteAllText((Join-Path $installed 'old-only.txt'), 'old')
    [IO.File]::WriteAllText((Join-Path $payload 'Arkade.exe'), 'updated fixture - not executable')
    [IO.File]::WriteAllText((Join-Path $payload 'retrobat.settings.json'), '{"InstallationPath":"wrong default"}')
    $settings = '{"InstallationPath":"D:\\MyRetroBat","RomsRoot":"E:\\Games","LaunchProfiles":{"cabinet":"custom"}}'
    [IO.File]::WriteAllText((Join-Path $installed 'retrobat.settings.json'), $settings)
    $helper = Join-Path $helperFolder 'ApplyUpdate.ps1'
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot '..\..\ArcadeOS\Assets\ApplyUpdate.ps1') -Destination $helper
    $powershell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    & $powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $helper -Payload $payload -InstallDirectory $installed -ParentId 0 -NoLaunch
    if ($LASTEXITCODE -ne 0) { throw 'Isolated install failed.' }
    if ([IO.File]::ReadAllText((Join-Path $installed 'Arkade.exe')) -ne 'updated fixture - not executable') { throw 'New application not installed.' }
    if ([IO.File]::ReadAllText((Join-Path $installed 'retrobat.settings.json')) -ne $settings) { throw 'Cabinet settings were changed.' }
    if (Test-Path -LiteralPath (Join-Path $installed 'old-only.txt')) { throw 'Stale application files survived the swap.' }
    $backup = Get-ChildItem -LiteralPath $root -Directory -Filter 'installed.backup-*' | Select-Object -First 1
    if (-not $backup -or [IO.File]::ReadAllText((Join-Path $backup.FullName 'Arkade.exe')) -ne 'original fixture - not executable') { throw 'Original version not retained.' }
    # A missing payload must leave the installed version untouched.
    & $powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $helper -Payload (Join-Path $root 'missing') -InstallDirectory $installed -ParentId 0 -NoLaunch
    if ($LASTEXITCODE -eq 0 -or [IO.File]::ReadAllText((Join-Path $installed 'Arkade.exe')) -ne 'updated fixture - not executable') { throw 'Failed preparation damaged the installation.' }
    $global:LASTEXITCODE = 0 # The deliberately failing child above was expected and asserted.
    Write-Output 'PASS: Windows PowerShell 5 installer, staged replacement, exact settings preservation, stale file removal, previous-version backup and safe preparation failure. No real app was launched.'
}
finally {
    $resolved = [IO.Path]::GetFullPath($root)
    if (-not $resolved.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()),[StringComparison]::OrdinalIgnoreCase) -or (Split-Path $resolved -Leaf) -notlike 'Arkade-install-check-*') { throw 'Unsafe test cleanup path.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
