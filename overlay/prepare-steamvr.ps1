$ErrorActionPreference = 'Stop'
$steamVrVendor = Join-Path $PSScriptRoot 'SteamVr/vendor'
$steamVrVersion = Get-Content -LiteralPath (Join-Path $steamVrVendor 'version.json') -Raw | ConvertFrom-Json
if ($steamVrVersion.Commit -notmatch '^[a-f0-9]{40}$') { throw 'Invalid pinned OpenVR commit.' }
foreach ($steamVrFile in $steamVrVersion.Files) {
    if ($steamVrFile.File -notin @('LICENSE-OpenVR.txt', 'openvr_api.cs', 'openvr_api.dll') -or $steamVrFile.Sha256 -notmatch '^[A-Fa-f0-9]{64}$') {
        throw 'Invalid pinned OpenVR file.'
    }
    $steamVrTarget = Join-Path $steamVrVendor $steamVrFile.File
    if ($steamVrFile.File -eq 'openvr_api.dll' -and -not (Test-Path -LiteralPath $steamVrTarget -PathType Leaf)) {
        $steamVrTemporary = Join-Path $steamVrVendor ('openvr-' + [guid]::NewGuid().ToString('N') + '.tmp')
        try {
            Invoke-WebRequest -Uri "https://raw.githubusercontent.com/ValveSoftware/openvr/$($steamVrVersion.Commit)/bin/win64/openvr_api.dll" -OutFile $steamVrTemporary
            if ((Get-FileHash -LiteralPath $steamVrTemporary -Algorithm SHA256).Hash -ne $steamVrFile.Sha256) { throw 'Pinned OpenVR download checksum mismatch.' }
            [IO.File]::Move($steamVrTemporary, $steamVrTarget, $false)
        }
        finally { if (Test-Path -LiteralPath $steamVrTemporary) { Remove-Item -LiteralPath $steamVrTemporary -Force } }
    }
    if (-not (Test-Path -LiteralPath $steamVrTarget -PathType Leaf) -or (Get-FileHash -LiteralPath $steamVrTarget -Algorithm SHA256).Hash -ne $steamVrFile.Sha256) {
        throw ('Pinned OpenVR file missing or changed: ' + $steamVrFile.File)
    }
}
Write-Host "PASS pinned OpenVR $($steamVrVersion.Tag) library, bindings and license checksums"
