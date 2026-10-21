param(
    [string]$Version = '0.3.0',
    [string]$Compiler = '',
    [string]$OutputDirectory = ''
)

$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$overlay = Join-Path $repository 'overlay'
$toolCache = Join-Path $repository 'work\installer-tools'
$output = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $overlay 'dist\installer' }
$buildRoot = Join-Path $overlay ('dist\installer-work\' + [Guid]::NewGuid().ToString('N'))
$payload = Join-Path $buildRoot 'payload'
$runtimeVersion = '8.0.31'
$nodeVersion = '24.21.0'
$nodeArchiveHash = '158f7685b44de51f6c0df1d153526cbcd3e1bc739a8dfc607721cef75de9e541'
$vendorHash = '74424f9047078d0e02735dc9f7106897161e401de502d9f18597a3d9d3afa9cb'
if ($Version -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') { throw 'Version must contain three or four numeric parts.' }
New-Item -ItemType Directory -Path $toolCache,$output,$payload -Force | Out-Null

function Get-VerifiedDownload([string]$Url, [string]$Destination, [string]$Hash) {
    if (-not (Test-Path -LiteralPath $Destination) -or (Get-FileHash -LiteralPath $Destination -Algorithm SHA256).Hash -ne $Hash) {
        Invoke-WebRequest -Uri $Url -OutFile $Destination
    }
    if ((Get-FileHash -LiteralPath $Destination -Algorithm SHA256).Hash -ne $Hash) {
        throw "Checksum verification failed: $Destination"
    }
}

if (-not $Compiler) {
    $compilerHome = Join-Path $toolCache 'inno'
    $Compiler = Join-Path $compilerHome 'ISCC.exe'
    if (-not (Test-Path -LiteralPath $Compiler)) {
        $compilerSetup = Join-Path $toolCache 'innosetup-6.7.3.exe'
        if (-not (Test-Path -LiteralPath $compilerSetup)) {
            Invoke-WebRequest 'https://github.com/jrsoftware/issrc/releases/download/is-6_7_3/innosetup-6.7.3.exe' -OutFile $compilerSetup
        }
        $signature = Get-AuthenticodeSignature -LiteralPath $compilerSetup
        if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'Pyrsys B\.V\.') {
            throw 'The installer compiler must have a valid signature from Pyrsys B.V.'
        }
        $compilerLog = Join-Path $toolCache 'compiler-install.log'
        $arguments = @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-','/CURRENTUSER','/NOICONS','/MERGETASKS=!associatefiles',('/DIR="' + $compilerHome + '"'),('/LOG="' + $compilerLog + '"'))
        $process = Start-Process -FilePath $compilerSetup -ArgumentList $arguments -PassThru -WindowStyle Hidden
        if (-not $process.WaitForExit(60000)) { throw "Compiler setup is still running. Inspect $compilerLog before continuing." }
        if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $Compiler)) { throw 'Installing the workspace compiler failed.' }
    }
}
$Compiler = (Resolve-Path -LiteralPath $Compiler).Path

$archiveName = "node-v$nodeVersion-win-x64.zip"
$nodeArchive = Join-Path $toolCache $archiveName
Get-VerifiedDownload "https://nodejs.org/download/release/v$nodeVersion/$archiveName" $nodeArchive $nodeArchiveHash
$nodeHome = Join-Path $toolCache "node-v$nodeVersion-win-x64"
if (-not (Test-Path -LiteralPath (Join-Path $nodeHome 'node.exe'))) {
    Expand-Archive -LiteralPath $nodeArchive -DestinationPath $toolCache
}
# Take the binary and LICENSE directly from the verified archive on every build,
# rather than trusting an edited cache or the developer's system Node install.
Add-Type -AssemblyName System.IO.Compression.FileSystem
$nodeZip = [IO.Compression.ZipFile]::OpenRead($nodeArchive)
try {
    foreach ($name in @('node.exe','LICENSE')) {
        $entry = $nodeZip.GetEntry("node-v$nodeVersion-win-x64/$name")
        if (-not $entry) { throw "Official Node archive is missing $name." }
        $destination = Join-Path $buildRoot $name
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $destination, $true)
    }
}
finally { $nodeZip.Dispose() }

$bridge = Join-Path $repository 'bridge'
if (-not (Test-Path -LiteralPath (Join-Path $bridge 'node_modules\@microsoft\signalr\package.json'))) {
    Push-Location $bridge
    try {
        npm ci --ignore-scripts --no-audit --no-fund
        if ($LASTEXITCODE -ne 0) { throw 'Installing the locked bridge dependencies failed.' }
    }
    finally { Pop-Location }
}
dotnet publish (Join-Path $overlay 'ShockWave.Overlay.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true "-p:RuntimeFrameworkVersion=$runtimeVersion" "-p:Version=$Version" -o $payload --nologo
if ($LASTEXITCODE -ne 0) { throw 'Publishing the self-contained application failed.' }

# Native Lovense binaries may exist on the developer's machine. They are never
# embedded in the shareable payload: Setup obtains the pinned official file.
$nativeRoot = [IO.Path]::GetFullPath((Join-Path $payload 'native'))
if (-not $nativeRoot.StartsWith($payload + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Native staging path escaped the unique payload directory.'
}
if (Test-Path -LiteralPath $nativeRoot) { Remove-Item -LiteralPath $nativeRoot -Recurse -Force }
Copy-Item -LiteralPath (Join-Path $buildRoot 'node.exe') -Destination (Join-Path $payload 'node.exe')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'GETTING-STARTED.txt') -Destination (Join-Path $payload 'README.txt')
foreach ($directory in @('docs','licenses','Avatar-tools\Assets\Editor\SzebsToysToyControl')) {
    New-Item -ItemType Directory -Path (Join-Path $payload $directory) -Force | Out-Null
}
foreach ($document in @('SPS.md','TOY-CONTROL.md','PISHOCK.md')) {
    Move-Item -LiteralPath (Join-Path $payload $document) -Destination (Join-Path $payload "docs\$document")
}
Copy-Item -LiteralPath (Join-Path $overlay 'README.md') -Destination (Join-Path $payload 'docs\WINDOWS-APP.md')
Copy-Item -LiteralPath (Join-Path $bridge 'README.md') -Destination (Join-Path $payload 'docs\BRIDGE.md')
Copy-Item -LiteralPath (Join-Path $repository 'docs\lovense-remote\LOCAL-TOYS.md') -Destination (Join-Path $payload 'docs\LOCAL-TOYS.md')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'SETUP-INFO.txt') -Destination (Join-Path $payload 'docs\INSTALLATION.txt')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'AVATAR-TOOLS.txt') -Destination (Join-Path $payload 'Avatar-tools\README.txt')
foreach ($file in @('ToyControlSetupWindow.cs','ToyControlShaderFactory.cs','ToyControlContract.cs','ToyControl.Editor.asmdef','GraphCore.hlsli','TouchVisuals.hlsli','Panel.cginc','GripStrip.shader')) {
    Copy-Item -LiteralPath (Join-Path $repository "work\ToyControlEditor\$file") -Destination (Join-Path $payload "Avatar-tools\Assets\Editor\SzebsToysToyControl\$file")
}

Copy-Item -LiteralPath (Join-Path $buildRoot 'LICENSE') -Destination (Join-Path $payload 'licenses\Node-LICENSE.txt')
$nugetRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }
Copy-Item -LiteralPath (Join-Path $nugetRoot "microsoft.netcore.app.runtime.win-x64\$runtimeVersion\LICENSE.TXT") -Destination (Join-Path $payload 'licenses\DotNet-Runtime-LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $nugetRoot "microsoft.netcore.app.runtime.win-x64\$runtimeVersion\THIRD-PARTY-NOTICES.TXT") -Destination (Join-Path $payload 'licenses\DotNet-Runtime-THIRD-PARTY-NOTICES.txt')
Copy-Item -LiteralPath (Join-Path $nugetRoot "microsoft.windowsdesktop.app.runtime.win-x64\$runtimeVersion\LICENSE") -Destination (Join-Path $payload 'licenses\DotNet-Desktop-LICENSE.txt')
$signalrLicense = Join-Path $toolCache 'SignalR-10.0.11-LICENSE.txt'
if (-not (Test-Path -LiteralPath $signalrLicense)) {
    Invoke-WebRequest 'https://raw.githubusercontent.com/dotnet/aspnetcore/v10.0.11/LICENSE.txt' -OutFile $signalrLicense
}
Copy-Item -LiteralPath $signalrLicense -Destination (Join-Path $payload 'licenses\SignalR-LICENSE.txt')
$tr46License = Join-Path $toolCache 'tr46-LICENSE.md'
if (-not (Test-Path -LiteralPath $tr46License)) {
    Invoke-WebRequest 'https://raw.githubusercontent.com/Sebmaster/tr46.js/master/LICENSE.md' -OutFile $tr46License
}
Copy-Item -LiteralPath $tr46License -Destination (Join-Path $payload 'bridge\node_modules\tr46\LICENSE.md')
$renderingNotices = Join-Path $payload 'ToyControl\THIRD-PARTY.txt'
$notice = [IO.File]::ReadAllText($renderingNotices).Replace("The machine's Windows/.NET Desktop runtime is not bundled.", "This installer bundles the .NET 8 runtime. Its additional license and notices are in ../licenses.")
[IO.File]::WriteAllText($renderingNotices, $notice)
@"
ShockWave installer dependency notices

.NET Windows Desktop / .NET runtime ${runtimeVersion}: see DotNet-*-LICENSE.txt and DotNet-Runtime-THIRD-PARTY-NOTICES.txt.
Node.js ${nodeVersion} and its bundled dependencies: see Node-LICENSE.txt.
Microsoft SignalR 10.0.11: see SignalR-LICENSE.txt and bridge/node_modules/@microsoft/signalr/src/third-party-notices.txt.
Other bridge dependencies retain their original LICENSE files under bridge/node_modules. The bridge package-lock is included for exact versions.
Vortice, SharpGen and the additional managed .NET libraries: see ToyControl/THIRD-PARTY.txt and ToyControl/licenses.
Inno Setup: see Inno-Setup-LICENSE.txt.
Lovense BLE v1.2 is downloaded separately from the official vendor. It is not embedded in this installer. Terms: https://www.lovense.com/end-user-license-agreement
"@ | Set-Content -LiteralPath (Join-Path $payload 'licenses\README.txt') -Encoding utf8
Copy-Item -LiteralPath (Join-Path (Split-Path $Compiler -Parent) 'License.txt') -Destination (Join-Path $payload 'licenses\Inno-Setup-LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $bridge 'package-lock.json') -Destination (Join-Path $payload 'bridge\package-lock.json')
foreach ($document in @('WINDOWS-APP.md','BRIDGE.md','LOCAL-TOYS.md')) {
    $documentPath = Join-Path $payload "docs\$document"
    $documentText = [IO.File]::ReadAllText($documentPath)
    $documentText = $documentText.Replace('../docs/lovense-remote/SPS.md','SPS.md').Replace('../bridge/README.md','BRIDGE.md').Replace('../work/ToyControlEditor/README.md','TOY-CONTROL.md').Replace('../docs/PISHOCK.md','PISHOCK.md')
    [IO.File]::WriteAllText($documentPath, $documentText)
}

# Fail closed on extra published roots instead of silently omitting a newly
# required runtime file. Only clean sample settings are shipped.
$allowedRoots = @('ShockWave.exe','node.exe','README.txt','bridge','ToyControl','docs','licenses','Avatar-tools')
foreach ($entry in Get-ChildItem -LiteralPath $payload -Force) {
    if ($entry.Name -notin $allowedRoots) { throw "Review new publish content before packaging: $($entry.Name)" }
}
$sample = Get-Content -LiteralPath (Join-Path $payload 'bridge\config.example.json') -Raw | ConvertFrom-Json
if ($sample.dryRun -ne $false -or @($sample.shockers | Where-Object { $_.uuid }).Count -gt 0) { throw 'Recipient default configuration must have Test mode off and no hardware IDs.' }
$files = @(Get-ChildItem -LiteralPath $payload -File -Recurse -Force)
foreach ($file in $files) {
    if ($file.Name -match '^(config\.json|bridge-config\.json|.*\.dpapi|.*\.log|.*\.pdb)$' -or $file.Name -eq 'LovenseBLE_Lib.dll') {
        throw "Personal/debug/native file must not be distributed: $($file.Name)"
    }
}
$manifestFiles = @($files | Sort-Object FullName | ForEach-Object {
    [ordered]@{ path = [IO.Path]::GetRelativePath($payload, $_.FullName).Replace('\','/'); bytes = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
})
[ordered]@{ version=$Version; platform='win-x64'; selfContained=$true; dotnetRuntime=$runtimeVersion; node=$nodeVersion; bluetooth=[ordered]@{ url='https://developer.lovense.com/LovenseBLE_Lib.dll'; sha256=$vendorHash; embedded=$false }; files=$manifestFiles } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $payload 'payload-manifest.json') -Encoding utf8

& (Join-Path $repository 'tests\local-toys\CheckPublishedWorker.ps1') -Executable (Join-Path $payload 'ShockWave.exe')
& $Compiler '/Qp' "/DPayloadDir=$payload" "/DSetupOutputDir=$output" "/DAppVersion=$Version" (Join-Path $PSScriptRoot 'ShockWave.iss')
if ($LASTEXITCODE -ne 0) { throw 'Compiling the installer failed.' }
$setup = Join-Path $output "ShockWave-Setup-$Version-win-x64.exe"
$setupHash = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash
"$setupHash  $([IO.Path]::GetFileName($setup))" | Set-Content -LiteralPath "$setup.sha256.txt" -Encoding ascii
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'GETTING-STARTED.txt') -Destination (Join-Path $output 'Getting-started.txt') -Force
[ordered]@{ setup=$setup; bytes=(Get-Item -LiteralPath $setup).Length; sha256=$setupHash; payload=$payload; fileCount=$manifestFiles.Count } | ConvertTo-Json | Tee-Object -FilePath (Join-Path $output 'build-result.json')
