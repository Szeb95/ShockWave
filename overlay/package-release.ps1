param(
    [string]$Version = '0.8.0',
    [string]$OutputDirectory = '',
    [string[]]$PublishArguments = @(),
    [switch]$NoZip
)

$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') { throw 'Version must contain three or four numeric parts.' }
$releaseRepo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$releaseOutput = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $PSScriptRoot 'dist' }
$releasePayload = Join-Path $releaseOutput "ShockWave-$Version-win-x64"
$releaseZip = "$releasePayload.zip"
if ((Test-Path -LiteralPath $releasePayload) -or (Test-Path -LiteralPath $releaseZip)) { throw 'This release already exists. Use a new version/output folder; previous releases are preserved.' }
New-Item -ItemType Directory -Path $releaseOutput -Force | Out-Null
& (Join-Path $PSScriptRoot 'prepare-buttplug.ps1')
& (Join-Path $PSScriptRoot 'prepare-steamvr.ps1')
$releaseBridge = Join-Path $releaseRepo 'bridge'
Push-Location $releaseBridge
try {
    npm ci --ignore-scripts --no-audit --no-fund
    if ($LASTEXITCODE -ne 0) { throw 'Restoring locked bridge dependencies failed.' }
}
finally { Pop-Location }

$releaseTools = Join-Path $releaseRepo 'work/installer-tools'
New-Item -ItemType Directory -Path $releaseTools -Force | Out-Null
$releaseNodeVersion = '24.21.0'
$releaseRuntime = '8.0.31'
$releaseNodeArchive = Join-Path $releaseTools "node-v$releaseNodeVersion-win-x64.zip"
$releaseNodeHash = '158f7685b44de51f6c0df1d153526cbcd3e1bc739a8dfc607721cef75de9e541'
if (-not (Test-Path -LiteralPath $releaseNodeArchive)) {
    Invoke-WebRequest "https://nodejs.org/download/release/v$releaseNodeVersion/node-v$releaseNodeVersion-win-x64.zip" -OutFile $releaseNodeArchive
}
if ((Get-FileHash -LiteralPath $releaseNodeArchive -Algorithm SHA256).Hash -ne $releaseNodeHash) { throw 'Official Node.js archive checksum mismatch.' }

dotnet publish (Join-Path $PSScriptRoot 'ShockWave.Overlay.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:IncludeLegacyLovense=false "-p:RuntimeFrameworkVersion=$releaseRuntime" "-p:Version=$Version" -o $releasePayload --nologo @PublishArguments
if ($LASTEXITCODE -ne 0) { throw 'Publishing the Windows release failed.' }
$releaseLicenses = Join-Path $releasePayload 'licenses'
New-Item -ItemType Directory -Path $releaseLicenses -Force | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
$releaseArchive = [IO.Compression.ZipFile]::OpenRead($releaseNodeArchive)
try {
    foreach ($releaseNodeFile in @('node.exe', 'LICENSE')) {
        $releaseEntry = $releaseArchive.GetEntry("node-v$releaseNodeVersion-win-x64/$releaseNodeFile")
        if (-not $releaseEntry) { throw 'The official Node.js archive is incomplete.' }
        $releaseDestination = if ($releaseNodeFile -eq 'LICENSE') { Join-Path $releaseLicenses 'Node-LICENSE.txt' } else { Join-Path $releasePayload $releaseNodeFile }
        [IO.Compression.ZipFileExtensions]::ExtractToFile($releaseEntry, $releaseDestination, $false)
    }
}
finally { $releaseArchive.Dispose() }
$releaseNuget = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget/packages' }
foreach ($releaseNotice in @('LICENSE.TXT', 'THIRD-PARTY-NOTICES.TXT')) {
    Copy-Item -LiteralPath (Join-Path $releaseNuget "microsoft.netcore.app.runtime.win-x64/$releaseRuntime/$releaseNotice") -Destination (Join-Path $releaseLicenses "DotNet-Runtime-$releaseNotice")
}
Copy-Item -LiteralPath (Join-Path $releaseNuget "microsoft.windowsdesktop.app.runtime.win-x64/$releaseRuntime/LICENSE") -Destination (Join-Path $releaseLicenses 'DotNet-Desktop-LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $releaseRepo 'work/ShaderPanelTest/THIRD-PARTY.txt') -Destination (Join-Path $releaseLicenses 'Rendering-THIRD-PARTY.txt')
Copy-Item -LiteralPath (Join-Path $releaseRepo 'work/ShaderPanelTest/licenses') -Destination (Join-Path $releaseLicenses 'Rendering') -Recurse
Copy-Item -LiteralPath (Join-Path $releaseRepo 'overlay/SteamVr/vendor/LICENSE-OpenVR.txt') -Destination (Join-Path $releaseLicenses 'LICENSE-OpenVR.txt') -Force
Copy-Item -LiteralPath (Join-Path $releaseBridge 'package-lock.json') -Destination (Join-Path $releasePayload 'bridge/package-lock.json')
# SignalR's npm package references an upstream license that must accompany it.
$releaseSignalLicense = Join-Path $releaseTools 'SignalR-10.0.11-LICENSE.txt'
if (-not (Test-Path -LiteralPath $releaseSignalLicense)) { Invoke-WebRequest 'https://raw.githubusercontent.com/dotnet/aspnetcore/v10.0.11/LICENSE.txt' -OutFile $releaseSignalLicense }
Copy-Item -LiteralPath $releaseSignalLicense -Destination (Join-Path $releaseLicenses 'SignalR-LICENSE.txt')
$releaseTr46License = Join-Path $releaseTools 'tr46-LICENSE.md'
if (-not (Test-Path -LiteralPath $releaseTr46License)) { Invoke-WebRequest 'https://raw.githubusercontent.com/Sebmaster/tr46.js/master/LICENSE.md' -OutFile $releaseTr46License }
Copy-Item -LiteralPath $releaseTr46License -Destination (Join-Path $releasePayload 'bridge/node_modules/tr46/LICENSE.md')
$releaseEditor = Join-Path $releasePayload 'UnityEditor/SzebsToysToyControl'
New-Item -ItemType Directory -Path $releaseEditor -Force | Out-Null
Get-ChildItem -LiteralPath (Join-Path $releaseRepo 'work/ToyControlEditor') -File | Where-Object Name -ne 'ToyControlVerification.cs' | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination $releaseEditor
}
@"
ShockWave $Version - Windows x64

Extract the WHOLE ZIP into a new folder and run ShockWave.exe.
Node.js $releaseNodeVersion, .NET $releaseRuntime and Bluetooth engine 5.0.4 are included.
Existing settings and encrypted credentials in LOCALAPPDATA/SzebsToys are reused.

When moving from 0.7.x to 0.8.0, download and extract the full package manually
once. Older updaters do not recognize the new SteamVR files. Version 0.8.0
includes the updated validation for future SteamVR packages.

Update is below Settings. It checks Szeb95/ShockWave's published stable releases.
Update downloads and verifies a complete package, closes normally and restarts.
Automatic update is off by default; when enabled it downloads updates and applies
them on your next normal app close. Cancelling the existing close warning keeps
the app running. Never assume that a physical stop succeeded based on software.

Source is available under the included contribution-only LICENSE. Other users
may propose fixes; independent modified releases and unrelated code reuse need
permission. Third-party licenses remain separate.

UnityEditor contains optional tool sources. Installation, avatar Update and upload
are explicit user actions; updating this app never changes an existing Unity project.
Read STEAMVR.md, REMOTE.md, TOY-CONTROL.md, SPS.md and PISHOCK.md for setup and current limits.

Toy control and Shocker control recreate the classic desktop layout without HLSL.
Touch, Toys/Shockers page-switch buttons and duplicate page headings are removed.
The top toolbar is removed. Bottom left on both pages: Open in VR, Recenter, Stop.
Record follows Stop on toys. The old Clear curve and Stop all buttons are removed.
Play loop is removed.
Use the app sidebar to choose Toy control or Shocker control.
SteamVR keeps the approved wrist / floating panel design.
The wrist starts closed. Within about 18 cm, its fixed Move icon appears.
Freshly hold grip near/above the icon, then pull upward to unfold the watch.
A separate bar follows the pull, with the same design as the panel handles.
When expanded, the bar/icon only appear near the other controller (18/24 cm).
Grip near/above the top bar and pull back to the anchor to close. Active waves and
submitted shocks keep running. Pending confirmation and unfinished input cancel.
Settings -> SteamVR panel -> Open wrist menu opens it without the gesture.
Adjust wrist position restores pointer-held Move; release saves the wrist fit.
Start SteamVR, then use the bottom-left headset icon on either control page.
Settings -> SteamVR panel saves hand, Wrist/Panel mode and optional automatic
opening when ShockWave starts. Automatic opening waits for SteamVR.
Close VR, Recall and Reconnect VRC are in Settings. No avatar setup is needed.
Settings -> SteamVR panel -> Transparency fades the whole overlay; Save settings
keeps it for next launch. Frames and gaps are already fully transparent.
VR Shock uses two released clicks within three seconds: arm, then confirm.
Desktop uses one rectangular Shock button: press -> release -> press to confirm.
Existing Test mode, owner limits and provider no-retry rules still apply.
"@ | Set-Content -LiteralPath (Join-Path $releasePayload 'README.txt') -Encoding utf8
if($NoZip) {
    $releaseGuide=Get-Content -LiteralPath (Join-Path $releasePayload 'README.txt') -Raw
    $releaseGuide.Replace('Extract the WHOLE ZIP into a new folder and run ShockWave.exe.','Run ShockWave.exe from this complete folder. Keep its files together.') | Set-Content -LiteralPath (Join-Path $releasePayload 'README.txt') -Encoding utf8
}

$releaseFiles = @(Get-ChildItem -LiteralPath $releasePayload -File -Recurse | Sort-Object FullName)
foreach ($releaseFile in $releaseFiles) {
    if ($releaseFile.Name -match '^(api-token\.bin|pishock-api-key\.bin|remote-friends\.bin|bridge-config\.json|config\.json|activity\.log|.*\.pdb|.*\.test\.mjs|LovenseBLE_Lib\.dll)$') { throw "Private/debug/vendor file in release: $($releaseFile.Name)" }
}
$releaseManifestFiles = @($releaseFiles | ForEach-Object {
    [ordered]@{ path = [IO.Path]::GetRelativePath($releasePayload, $_.FullName).Replace('\','/'); sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
})
[ordered]@{ format=1; version=$Version; platform='win-x64'; files=$releaseManifestFiles } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $releasePayload 'update-manifest.json') -Encoding utf8
& (Join-Path $releaseRepo 'tests/local-toys/CheckPublishedWorker.ps1') -Executable (Join-Path $releasePayload 'ShockWave.exe')
& (Join-Path $releaseRepo 'tests/remote/CheckPublishedRemoteWorker.ps1') -PackageDirectory $releasePayload
if($NoZip) {
    [ordered]@{version=$Version;app=(Join-Path $releasePayload 'ShockWave.exe');files=$releaseFiles.Count;zipCreated=$false;physicalVerification=$false;published=$false} | ConvertTo-Json
    return
}
[IO.Compression.ZipFile]::CreateFromDirectory($releasePayload, $releaseZip, [IO.Compression.CompressionLevel]::Optimal, $false)
$releaseZipHash = (Get-FileHash -LiteralPath $releaseZip -Algorithm SHA256).Hash.ToLowerInvariant()
"$releaseZipHash  $([IO.Path]::GetFileName($releaseZip))" | Set-Content -LiteralPath "$releaseZip.sha256.txt" -Encoding ascii
[ordered]@{ version=$Version; zip=$releaseZip; bytes=(Get-Item -LiteralPath $releaseZip).Length; sha256=$releaseZipHash; files=$releaseFiles.Count; physicalVerification=$false; published=$false } | ConvertTo-Json
