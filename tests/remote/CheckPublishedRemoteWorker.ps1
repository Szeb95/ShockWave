param([Parameter(Mandatory = $true)][string]$PackageDirectory)

$ErrorActionPreference = 'Stop'
$remoteCheckRoot = (Resolve-Path -LiteralPath $PackageDirectory).Path
foreach ($remoteCheckRequired in @('node.exe', 'bridge/remote-worker.mjs', 'bridge/remote-peer.mjs')) {
    if (-not (Test-Path -LiteralPath (Join-Path $remoteCheckRoot $remoteCheckRequired) -PathType Leaf)) {
        throw "The remote package is incomplete: $remoteCheckRequired"
    }
}
$remoteCheckSource = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../bridge/node_modules'))
if (-not (Test-Path -LiteralPath $remoteCheckSource -PathType Container)) { throw 'Restore locked bridge dependencies before checking the package.' }
foreach ($remoteCheckFile in Get-ChildItem -LiteralPath $remoteCheckSource -Recurse -File) {
    if ($remoteCheckFile.Extension -in '.ts', '.map' -or $remoteCheckFile.Name -eq '.package-lock.json') { continue }
    $remoteCheckRelative = 'bridge/node_modules/' + [IO.Path]::GetRelativePath($remoteCheckSource, $remoteCheckFile.FullName)
    $remoteCheckTarget = Join-Path $remoteCheckRoot $remoteCheckRelative
    if (-not (Test-Path -LiteralPath $remoteCheckTarget -PathType Leaf)) { throw "The remote package is incomplete: $remoteCheckRelative" }
    if ((Get-FileHash -LiteralPath $remoteCheckTarget).Hash -ne (Get-FileHash -LiteralPath $remoteCheckFile.FullName).Hash) {
        throw "The packaged dependency differs from the locked restore: $remoteCheckRelative"
    }
}
Write-Host 'PASS every restored runtime dependency is present with its expected hash'

# Run only the bundled Node connection worker, never the app or a hardware adapter.
# The invitation stays in memory: neither it nor the worker's raw output is logged.
$remoteCheckInfo = [Diagnostics.ProcessStartInfo]::new((Join-Path $remoteCheckRoot 'node.exe'))
$remoteCheckInfo.WorkingDirectory = Join-Path $remoteCheckRoot 'bridge'
$remoteCheckInfo.UseShellExecute = $false
$remoteCheckInfo.CreateNoWindow = $true
$remoteCheckInfo.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
$remoteCheckInfo.RedirectStandardInput = $true
$remoteCheckInfo.RedirectStandardOutput = $true
$remoteCheckInfo.RedirectStandardError = $true
$remoteCheckInfo.ArgumentList.Add('remote-worker.mjs')
foreach ($remoteCheckSecret in @('OPENSHOCK_TOKEN', 'PISHOCK_API_KEY')) {
    [void]$remoteCheckInfo.Environment.Remove($remoteCheckSecret)
}
$remoteCheckProcess = [Diagnostics.Process]::new()
$remoteCheckProcess.StartInfo = $remoteCheckInfo
$remoteCheckStarted = $false
try {
    $remoteCheckStarted = $remoteCheckProcess.Start()
    if (-not $remoteCheckStarted) { throw 'The bundled connection worker could not launch.' }
    $remoteCheckErrors = $remoteCheckProcess.StandardError.ReadToEndAsync()
    $remoteCheckReady = $remoteCheckProcess.StandardOutput.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds(15)).GetAwaiter().GetResult()
    if (-not $remoteCheckReady -or ($remoteCheckReady | ConvertFrom-Json).kind -ne 'ready') {
        throw 'The bundled connection worker stopped before readiness. Check the packaged Node dependencies.'
    }
    Write-Host 'PASS bundled connection worker reaches readiness'
    $remoteCheckProcess.StandardInput.WriteLine('{"kind":"enable","code":""}')
    $remoteCheckFound = $false
    for ($remoteCheckAttempt = 0; $remoteCheckAttempt -lt 8; $remoteCheckAttempt++) {
        $remoteCheckLine = $remoteCheckProcess.StandardOutput.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds(5)).GetAwaiter().GetResult()
        if (-not $remoteCheckLine) { throw 'The bundled connection worker stopped before generating an invitation.' }
        $remoteCheckMessage = $remoteCheckLine | ConvertFrom-Json
        if ($remoteCheckMessage.kind -ne 'invitation') { continue }
        $remoteCheckCode = $remoteCheckMessage.code
        if ($remoteCheckCode -notmatch '^SW1\.[A-Za-z0-9_-]{1,596}$') { throw 'The bundled invitation format is invalid.' }
        $remoteCheckEncoded = $remoteCheckCode.Substring(4).Replace('-', '+').Replace('_', '/')
        $remoteCheckEncoded = $remoteCheckEncoded.PadRight([int]([Math]::Ceiling($remoteCheckEncoded.Length / 4.0) * 4), '=')
        $remoteCheckInvitation = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($remoteCheckEncoded)) | ConvertFrom-Json
        $remoteCheckNow = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
        if ($remoteCheckInvitation.id -notmatch '^sw-[a-f0-9]{32}$' -or $remoteCheckInvitation.secret -notmatch '^[a-f0-9]{64}$' -or
            $remoteCheckInvitation.expires -le $remoteCheckNow -or $remoteCheckInvitation.expires -gt $remoteCheckNow + 86400000) {
            throw 'The bundled invitation identity or expiry is invalid.'
        }
        $remoteCheckFound = $true
        break
    }
    if (-not $remoteCheckFound) { throw 'The bundled connection worker did not generate an invitation.' }
    Write-Host 'PASS bundled connection worker generates a valid private sharing code'
    $remoteCheckProcess.StandardInput.WriteLine('{"kind":"disable"}')
    $remoteCheckProcess.StandardInput.Close()
    if (-not $remoteCheckProcess.WaitForExit(5000) -or $remoteCheckProcess.ExitCode -ne 0) {
        throw 'The bundled connection worker did not shut down cleanly.'
    }
    if ($remoteCheckErrors.GetAwaiter().GetResult()) { throw 'The bundled connection worker reported a runtime error.' }
    Write-Host 'PASS bundled connection worker disconnects and exits without hardware access'
}
finally {
    if ($remoteCheckStarted -and -not $remoteCheckProcess.HasExited) {
        $remoteCheckProcess.Kill($true)
        [void]$remoteCheckProcess.WaitForExit(2000)
    }
    $remoteCheckProcess.Dispose()
}
