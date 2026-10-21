$ErrorActionPreference = 'Stop'
$engineDirectory = Join-Path $PSScriptRoot 'native/buttplug'
$engine = Join-Path $engineDirectory 'intiface-engine.exe'
$engineHash = '06500F5B85D8534BAE845D83B231453B821B4F86EAECC461A34AF2C8407A93B7'
if (-not (Test-Path -LiteralPath $engine)) {
    $cache = Join-Path $PSScriptRoot 'obj/buttplug-download'
    New-Item -ItemType Directory -Path $cache -Force | Out-Null
    $archive = Join-Path $cache 'intiface-engine-v5.0.4-win-x64.zip'
    Invoke-WebRequest -Uri 'https://github.com/buttplugio/buttplug/releases/download/intiface-engine-5.0.4/intiface-engine-v5.0.4-win-x64.zip' -OutFile $archive
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne '29EA7051B23F7A05124E65581DF48C8600070C83ADC0348F0738B9BA75B58782') { throw 'Bundled engine archive checksum mismatch.' }
    $extraction = Join-Path $cache ([guid]::NewGuid().ToString('N'))
    Expand-Archive -LiteralPath $archive -DestinationPath $extraction
    Copy-Item -LiteralPath (Join-Path $extraction 'intiface-engine.exe') -Destination $engine
}
if ((Get-FileHash -LiteralPath $engine -Algorithm SHA256).Hash -ne $engineHash) { throw 'Bundled engine EXE checksum mismatch. Do not silently replace the pinned engine.' }
foreach ($required in @('lovense-devices.json', 'LICENSE.txt', 'README.md', 'VERSION.md')) {
    if (-not (Test-Path -LiteralPath (Join-Path $engineDirectory $required))) { throw "Missing bundled engine component: $required" }
}
Write-Host 'Verified bundled Bluetooth engine 5.0.4.'
