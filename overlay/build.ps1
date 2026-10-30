param([string]$OutputDirectory = '')

$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'prepare-buttplug.ps1')
& (Join-Path $PSScriptRoot 'prepare-steamvr.ps1')

$project = Join-Path $PSScriptRoot 'ShockWave.Overlay.csproj'
$output = if ($OutputDirectory) { [System.IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $PSScriptRoot 'dist\shockwave' }
$bridge = Join-Path (Split-Path $PSScriptRoot -Parent) 'bridge'
if (-not (Test-Path -LiteralPath (Join-Path $bridge 'node_modules\@microsoft\signalr\package.json')) -or
    -not (Test-Path -LiteralPath (Join-Path $bridge 'node_modules\werift\package.json'))) {
    Push-Location $bridge
    try {
        npm ci --ignore-scripts --no-audit --no-fund
        if ($LASTEXITCODE -ne 0) { throw 'Installing the live log client failed.' }
    }
    finally { Pop-Location }
}

dotnet publish $project -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o $output --nologo
if ($LASTEXITCODE -ne 0) {
    throw 'The native overlay build failed.'
}

$node = (Get-Command node.exe -ErrorAction Stop).Source
Copy-Item -LiteralPath $node -Destination (Join-Path $output 'node.exe') -Force

foreach ($stale in @('ShockWave.pdb', 'bridge\bridge.test.mjs', 'bridge\simulate.mjs')) {
    $stalePath = Join-Path $output $stale
    if (Test-Path -LiteralPath $stalePath) {
        Remove-Item -LiteralPath $stalePath -Force
    }
}

$exe = Join-Path $output 'ShockWave.exe'
if (-not (Test-Path -LiteralPath $exe)) {
    throw 'The published EXE was not found.'
}
$workerCheck = Join-Path (Split-Path $PSScriptRoot -Parent) 'tests\local-toys\CheckPublishedWorker.ps1'
& $workerCheck -Executable $exe
& (Join-Path (Split-Path $PSScriptRoot -Parent) 'tests\remote\CheckPublishedRemoteWorker.ps1') -PackageDirectory $output
Write-Host "Native overlay ready: $exe"
