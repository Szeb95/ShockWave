param([string]$OutputDirectory='work/panel-vcc-checks/package')
$ErrorActionPreference='Stop'
$panelRepo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$panelOut=[IO.Path]::GetFullPath((Join-Path $panelRepo $OutputDirectory))
$panelManifest=Get-Content -LiteralPath (Join-Path $panelRepo 'vpm/package.json') -Raw | ConvertFrom-Json
$panelStage=Join-Path $panelOut ('ShockWave-Avatar-Panel-'+$panelManifest.version)
$panelZip=$panelStage+'.zip'
if((Test-Path -LiteralPath $panelStage) -or (Test-Path -LiteralPath $panelZip)) {throw 'Package already exists. Use another output directory/version.'}
New-Item -ItemType Directory -Path (Join-Path $panelStage 'Editor') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $panelRepo 'vpm/package.json') -Destination $panelStage
Copy-Item -LiteralPath (Join-Path $panelRepo 'LICENSE') -Destination (Join-Path $panelStage 'LICENSE.txt')
Get-ChildItem -LiteralPath (Join-Path $panelRepo 'work/ToyControlEditor') -File | Where-Object Name -ne 'ToyControlVerification.cs' | ForEach-Object {Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $panelStage 'Editor')}
# Keep existing Unity GUIDs; generate stable GUIDs for the remaining package files.
foreach($panelFile in @(Get-ChildItem -LiteralPath $panelStage -File -Recurse)) {
    if($panelFile.Extension -eq '.meta' -or (Test-Path -LiteralPath ($panelFile.FullName+'.meta'))) {continue}
    $panelRelative=[IO.Path]::GetRelativePath($panelStage,$panelFile.FullName).Replace('\','/')
    $panelHash=[Security.Cryptography.SHA256]::Create()
    try {$panelGuid=([BitConverter]::ToString($panelHash.ComputeHash([Text.Encoding]::UTF8.GetBytes($panelManifest.name+'/'+$panelRelative)))).Replace('-','').Substring(0,32).ToLowerInvariant()}
    finally {$panelHash.Dispose()}
    "fileFormatVersion: 2`nguid: $panelGuid`n" | Set-Content -LiteralPath ($panelFile.FullName+'.meta') -Encoding ascii
}
$panelFolderHash=[Security.Cryptography.SHA256]::Create()
try {$panelFolderGuid=([BitConverter]::ToString($panelFolderHash.ComputeHash([Text.Encoding]::UTF8.GetBytes($panelManifest.name+'/Editor')))).Replace('-','').Substring(0,32).ToLowerInvariant()}
finally {$panelFolderHash.Dispose()}
"fileFormatVersion: 2`nguid: $panelFolderGuid`nfolderAsset: yes`n" | Set-Content -LiteralPath (Join-Path $panelStage 'Editor.meta') -Encoding ascii
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($panelStage,$panelZip,[IO.Compression.CompressionLevel]::Optimal,$false)
$panelSha=(Get-FileHash -LiteralPath $panelZip -Algorithm SHA256).Hash.ToLowerInvariant()
"$panelSha  $([IO.Path]::GetFileName($panelZip))" | Set-Content -LiteralPath ($panelZip+'.sha256.txt') -Encoding ascii
$panelListing=Join-Path $panelRepo 'vpm/index.json'
$panelIndex=if(Test-Path -LiteralPath $panelListing) {Get-Content -LiteralPath $panelListing -Raw | ConvertFrom-Json -AsHashtable} else {[ordered]@{name='ShockWave';id='com.szebs.shockwave.repository';url='https://szeb95.github.io/ShockWave/index.json';author='Szeb95';packages=@{}}}
$panelEntry=Get-Content -LiteralPath (Join-Path $panelStage 'package.json') -Raw | ConvertFrom-Json -AsHashtable
$panelEntry.zipSHA256=$panelSha
if(!$panelIndex.packages.Contains($panelManifest.name)) {$panelIndex.packages[$panelManifest.name]=@{versions=@{}}}
$panelIndex.packages[$panelManifest.name].versions[$panelManifest.version]=$panelEntry
$panelIndex | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $panelListing -Encoding utf8
[ordered]@{version=$panelManifest.version;stage=$panelStage;zip=$panelZip;sha256=$panelSha;files=@(Get-ChildItem -LiteralPath $panelStage -File -Recurse).Count} | ConvertTo-Json
