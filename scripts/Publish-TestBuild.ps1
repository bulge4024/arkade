[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z][0-9A-Za-z.-]*)?$') { throw 'Use a version such as 0.1.3 or 0.1.3-test.1.' }
$repo = Split-Path $PSScriptRoot -Parent
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { $OutputDirectory = Join-Path $repo 'artifacts' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$packageName = "Arkade-Cabinet-Test-$Version"
$package = Join-Path $OutputDirectory $packageName
$app = Join-Path $package 'app'
New-Item -ItemType Directory -Path $app -Force | Out-Null
$project = Join-Path $repo 'ArcadeOS\ArcadeOS.csproj'
& dotnet publish $project -c Release -r win-x64 --self-contained true '-p:PublishSingleFile=false' '-p:PublishTrimmed=false' '-p:AssemblyName=Arkade' '-p:Product=Arkade' '-p:AssemblyTitle=Arkade' "-p:Version=$Version" "-p:InformationalVersion=$Version" -o $app --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with code $LASTEXITCODE" }
foreach ($name in @('Setup.cmd', 'Install-Arkade.ps1', 'READ-ME-FIRST.txt')) { Copy-Item -LiteralPath (Join-Path $repo "packaging\$name") -Destination $package -Force }
Copy-Item -LiteralPath (Join-Path $repo 'THIRD-PARTY-NOTICES.md') -Destination $package -Force
@{
    Product = 'Arkade'; Version = $Version; Platform = 'win-x64'; SelfContained = $true
    BuiltUtc = [DateTime]::UtcNow.ToString('O'); Commit = $env:GITHUB_SHA
} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $package 'BUILD-INFO.json') -Encoding UTF8
$zip = Join-Path $OutputDirectory "$packageName-win-x64.zip"
Compress-Archive -LiteralPath $package -DestinationPath $zip -CompressionLevel Optimal -Force
$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText((Join-Path $OutputDirectory "$packageName-win-x64.sha256"), $hash + '  ' + [IO.Path]::GetFileName($zip) + [Environment]::NewLine)
Write-Host "Cabinet package: $zip"

# Tiny bootstrap repair for cabinets running the original updater helper.
$repair = Join-Path $OutputDirectory 'updater-repair'
New-Item -ItemType Directory -Path $repair -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $repo 'scripts\Repair-ArkadeUpdate.ps1') -Destination $repair -Force
Copy-Item -LiteralPath (Join-Path $repo 'packaging\Repair-ArkadeUpdate.cmd') -Destination $repair -Force
Copy-Item -LiteralPath (Join-Path $repo 'ArcadeOS\Assets\ApplyUpdate.ps1') -Destination $repair -Force
$repairZip = Join-Path $OutputDirectory 'Arkade-Updater-Repair.zip'
Compress-Archive -Path (Join-Path $repair '*') -DestinationPath $repairZip -CompressionLevel Optimal -Force
$repairHash = (Get-FileHash -LiteralPath $repairZip -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'Arkade-Updater-Repair.sha256'), $repairHash + '  Arkade-Updater-Repair.zip' + [Environment]::NewLine)