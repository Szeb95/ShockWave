param([string]$Executable = '')

$ErrorActionPreference = 'Stop'
$workerCheckRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if (-not $Executable) { $Executable = Join-Path $workerCheckRoot 'overlay\dist\shockwave\ShockWave.exe' }
$workerCheckSource = (Resolve-Path -LiteralPath $Executable).Path
$workerCheckDirectory = Join-Path $workerCheckRoot ('work\fixture-diagnostics\published-worker-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $workerCheckDirectory -Force | Out-Null
$workerCheckExe = Join-Path $workerCheckDirectory 'ShockWave.exe'

try {
    Copy-Item -LiteralPath $workerCheckSource -Destination $workerCheckExe
    if (Test-Path -LiteralPath (Join-Path $workerCheckDirectory 'native')) { throw 'Published-worker probe must have no native dependencies.' }
    # Only the worker entry point runs. The isolated folder has no DLL, so this cannot access BLE/toys.
    for ($workerCheckAttempt = 1; $workerCheckAttempt -le 3; $workerCheckAttempt++) {
        $workerCheckInfo = [System.Diagnostics.ProcessStartInfo]::new($workerCheckExe)
        $workerCheckInfo.UseShellExecute = $false
        $workerCheckInfo.CreateNoWindow = $true
        $workerCheckInfo.WindowStyle = [System.Diagnostics.ProcessWindowStyle]::Hidden
        $workerCheckInfo.RedirectStandardInput = $true
        $workerCheckInfo.RedirectStandardOutput = $true
        $workerCheckInfo.RedirectStandardError = $true
        $workerCheckInfo.WorkingDirectory = $workerCheckDirectory
        $workerCheckInfo.ArgumentList.Add('--lovense-worker')
        [void]$workerCheckInfo.Environment.Remove('OPENSHOCK_TOKEN')
        $workerCheckProcess = [System.Diagnostics.Process]::new()
        $workerCheckProcess.StartInfo = $workerCheckInfo
        $workerCheckStarted = $false
        try {
            $workerCheckStarted = $workerCheckProcess.Start()
            if (-not $workerCheckStarted) { throw 'Published worker process could not launch.' }
            $workerCheckErrors = $workerCheckProcess.StandardError.ReadToEndAsync()
            $workerCheckLine = $workerCheckProcess.StandardOutput.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds(5)).GetAwaiter().GetResult()
            if (-not $workerCheckProcess.WaitForExit(3000)) { throw 'Published worker did not exit after the missing-library result.' }
            $workerCheckMessage = $workerCheckLine | ConvertFrom-Json
            $workerCheckStderr = $workerCheckErrors.GetAwaiter().GetResult()
            if ($workerCheckProcess.ExitCode -ne 1 -or $workerCheckMessage.Kind -ne 'fault' -or $workerCheckMessage.Code -ne 1 -or $workerCheckStderr) {
                throw "Published worker did not reach its native dependency check (exit $($workerCheckProcess.ExitCode))."
            }
            Write-Host "PASS published WinExe worker startup/pipe check $workerCheckAttempt (no native DLL)"
        }
        finally {
            if ($workerCheckStarted -and -not $workerCheckProcess.HasExited) { $workerCheckProcess.Kill($true); [void]$workerCheckProcess.WaitForExit(2000) }
            $workerCheckProcess.Dispose()
        }
    }
}
finally {
    # Remove only files created by this check; no recursive deletion or user settings are touched.
    if (Test-Path -LiteralPath $workerCheckExe) { Remove-Item -LiteralPath $workerCheckExe -Force }
    Remove-Item -LiteralPath $workerCheckDirectory -Force
}
