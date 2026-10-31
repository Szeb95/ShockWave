param(
    [Parameter(Mandatory)][ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+$')][string]$Version,
    [string]$DistributionRoot=([IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../overlay/dist')))
)
$ErrorActionPreference='Stop'
$distribution=[IO.Path]::GetFullPath($DistributionRoot)
$appFolder=Join-Path $distribution "ShockWave-$Version-win-x64"
$app=Join-Path $appFolder 'ShockWave.exe'
$nested=Test-Path -LiteralPath (Join-Path $appFolder 'ToyControl/SteamVR') -PathType Container
$required=if($nested) {@('ShockWave.exe','docs/build-info.json','ToyControl/SteamVR/openvr_api.dll','ToyControl/SteamVR/bindings/actions.json','node.exe','update-manifest.json')} else {@('ShockWave.exe','build-info.json','openvr_api.dll','app.vrmanifest','bindings/actions.json','node.exe','update-manifest.json')}
foreach($relative in $required) {
    if(-not (Test-Path -LiteralPath (Join-Path $appFolder $relative) -PathType Leaf)) {throw "Completed app is missing $relative"}
}
$buildPath=if($nested) {'docs/build-info.json'} else {'build-info.json'}
$build=Get-Content -LiteralPath (Join-Path $appFolder $buildPath) -Raw | ConvertFrom-Json
if($build.Version -ne $Version -or $build.SelfChecks -lt 39) {throw 'The app is not a matching checked build.'}
$latestFolder=Join-Path $distribution '00-Latest'
New-Item -ItemType Directory -Path $latestFolder -Force | Out-Null
$launcher=Join-Path $latestFolder 'Open Latest ShockWave.exe'
$source=Join-Path $PSScriptRoot 'Launcher.cs'
$sourceHash=(Get-FileHash -LiteralPath $source).Hash
$sourceRecord=Join-Path $latestFolder 'launcher-source.sha256'
if(-not (Test-Path -LiteralPath $launcher) -or -not (Test-Path -LiteralPath $sourceRecord) -or (Get-Content -LiteralPath $sourceRecord -Raw).Trim() -ne $sourceHash) {
    $compiler=Join-Path ([Environment]::GetFolderPath('Windows')) 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
    $temporaryExe=Join-Path $latestFolder ('launcher-'+[Guid]::NewGuid().ToString('N')+'.exe')
    & $compiler '/nologo' '/target:winexe' '/platform:x64' '/optimize+' '/reference:System.Windows.Forms.dll' '/reference:System.Runtime.Serialization.dll' "/out:$temporaryExe" $source
    if($LASTEXITCODE -ne 0) {throw 'Latest-version launcher compilation failed.'}
    [IO.File]::Move($temporaryExe,$launcher,$true)
    $sourceHash | Set-Content -LiteralPath $sourceRecord -Encoding ascii
}
$manifestPath=Join-Path $latestFolder 'latest.json'
if(Test-Path -LiteralPath $manifestPath) {
    $current=Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if([version]$Version -lt [version]$current.Revision) {throw 'Refusing to downgrade Latest.'}
}
$manifest=[ordered]@{Revision=$Version;Sha256=(Get-FileHash -LiteralPath $app).Hash;Length=(Get-Item -LiteralPath $app).Length;Complete=$true}
$temporaryManifest=Join-Path $latestFolder ('latest-'+[Guid]::NewGuid().ToString('N')+'.tmp')
$manifest | ConvertTo-Json | Set-Content -LiteralPath $temporaryManifest -Encoding utf8
[IO.File]::Move($temporaryManifest,$manifestPath,$true)
$shell=New-Object -ComObject WScript.Shell
$shortcutPath=Join-Path $distribution '00 - Open Latest ShockWave.lnk'
$shortcut=$shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath=$launcher;$shortcut.WorkingDirectory=$latestFolder
$shortcut.Description='Open the newest completed ShockWave app'
$shortcut.IconLocation="$app,0";$shortcut.Save()
[pscustomobject]@{Launcher=$launcher;Shortcut=$shortcutPath;LatestVersion=$Version}
