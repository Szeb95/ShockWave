param(
    [string]$Installer = '',
    [string]$Payload = '',
    [string]$InvalidHashInstaller = ''
)

$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$resultPath = Join-Path $repository 'overlay\dist\installer\build-result.json'
if (-not $Installer -or -not $Payload) {
    $build = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
    if (-not $Installer) { $Installer = $build.setup }
    if (-not $Payload) { $Payload = $build.payload }
}
$Installer = (Resolve-Path -LiteralPath $Installer).Path
$Payload = (Resolve-Path -LiteralPath $Payload).Path
$testId = [Guid]::NewGuid().ToString('N')
$testRoot = [IO.Path]::GetFullPath((Join-Path $repository "work\installer-checks\$testId"))
if (-not $testRoot.StartsWith($repository + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Installer test must stay inside this workspace.'
}
New-Item -ItemType Directory -Path $testRoot | Out-Null
$installRoot = Join-Path $testRoot 'Installed app'
$groupName = 'ShockWave'
$groupRoot = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\$groupName"
$registration = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\A82D794F-972C-494B-8EF3-9D9FCB5D4032_is1'
if (Test-Path -LiteralPath $registration) { throw 'An existing installed app is registered; do not replace it with a fixture.' }
if (Test-Path -LiteralPath $groupRoot) { throw 'An existing Start menu folder exists; do not replace its shortcuts with a fixture.' }
$userSettings = Join-Path $env:LOCALAPPDATA 'SzebsToys'
$settingsExisted = Test-Path -LiteralPath $userSettings
$beforeProcesses = @(Get-Process ShockWave,SzebsToys -ErrorAction SilentlyContinue | Select-Object Id,Path)
$checkCount = 0
$installed = $false

function Assert-Installer([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "FAIL $Message" }
    $script:checkCount++
    Write-Host "PASS $Message"
}

function Settings-Snapshot {
    if (-not (Test-Path -LiteralPath $userSettings)) { return '' }
    $snapshot = @(Get-ChildItem -LiteralPath $userSettings -File -Recurse -Force | Sort-Object FullName | ForEach-Object {
        [ordered]@{ file=$_.FullName; sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    })
    return ConvertTo-Json -InputObject $snapshot -Compress
}
$beforeSettings = Settings-Snapshot

function Run-Setup([string]$Exe, [string[]]$Arguments, [string]$LogName) {
    $logPath = Join-Path $testRoot $LogName
    $setupArguments = @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-',('/LOG="' + $logPath + '"')) + $Arguments
    $process = Start-Process -FilePath $Exe -ArgumentList $setupArguments -PassThru -WindowStyle Hidden
    if (-not $process.WaitForExit(45000)) {
        # Stop only the fixture process created here, never an app/worker.
        $process.Kill($true)
        throw "Installer fixture timed out. Inspect $logPath."
    }
    return $process.ExitCode
}

# Setup intentionally hides the program-group page, so Inno uses the fixed
# default group even if /GROUP is supplied. Verify its real recipient location.
$arguments = @(('/DIR="' + $installRoot + '"'),'/COMPONENTS=app,bluetooth,avatar','/TASKS=')
try {
    if ($InvalidHashInstaller) {
        $invalidRoot = Join-Path $testRoot 'Rejected install'
        $invalidArguments = @(('/DIR="' + $invalidRoot + '"'),'/COMPONENTS=app,bluetooth,avatar','/TASKS=')
        $code = Run-Setup $InvalidHashInstaller $invalidArguments 'rejected-new-install.log'
        Assert-Installer ($code -ne 0) 'Wrong Bluetooth checksum rejects a fresh installation'
        Assert-Installer (-not (Test-Path (Join-Path $invalidRoot 'ShockWave.exe')) -and -not (Test-Path $registration)) 'Failed preparation leaves no partial app or registration'
    }
    $code = Run-Setup $Installer $arguments 'install.log'
    $installed = Test-Path -LiteralPath (Join-Path $installRoot 'unins000.exe')
    Assert-Installer ($code -eq 0) 'Actual shareable EXE installs all selected components'
    $manifest = Get-Content -LiteralPath (Join-Path $Payload 'payload-manifest.json') -Raw | ConvertFrom-Json
    foreach ($file in $manifest.files) {
        $destination = Join-Path $installRoot $file.path
        if (-not (Test-Path -LiteralPath $destination) -or (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -ne $file.sha256) {
            throw "Installed file differs from the clean payload: $($file.path)"
        }
    }
    Assert-Installer ($manifest.files.Count -gt 200) 'Every app, bridge, runtime notice, shader and optional avatar file matches the manifest'
    $library = Join-Path $installRoot 'native\lovense\LovenseBLE_Lib.dll'
    Assert-Installer ((Get-FileHash -LiteralPath $library -Algorithm SHA256).Hash -eq $manifest.bluetooth.sha256) 'Official Bluetooth dependency is downloaded and verified'
    Assert-Installer (-not (Test-Path -LiteralPath (Join-Path $Payload 'native'))) 'Shared installer payload contains no copied developer Bluetooth binary'
    Assert-Installer (-not (Test-Path -LiteralPath (Join-Path $installRoot 'bridge\config.json'))) 'Recipient receives clean Test-mode defaults without personal settings'
    $appRegistration = Get-ItemProperty -LiteralPath $registration
    Assert-Installer ($appRegistration.DisplayName -eq 'ShockWave' -and $appRegistration.UninstallString.Contains($installRoot)) 'Windows Apps entry points to the installed uninstaller'
    Assert-Installer ($appRegistration.QuietUninstallString -match 'SILENT') 'Windows silent uninstall command is registered'
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut((Join-Path $groupRoot 'ShockWave.lnk'))
    Assert-Installer ($shortcut.TargetPath -eq (Join-Path $installRoot 'ShockWave.exe')) 'Start menu app shortcut points to the installed app'
    $shortcut = $shell.CreateShortcut((Join-Path $groupRoot 'Uninstall ShockWave.lnk'))
    Assert-Installer ($shortcut.TargetPath -eq (Join-Path $installRoot 'unins000.exe')) 'Start menu uninstall shortcut works'

    $node = Join-Path $installRoot 'node.exe'
    $nodeVersion = & $node --version
    Assert-Installer ($LASTEXITCODE -eq 0 -and $nodeVersion.Trim() -eq "v$($manifest.node)") 'Bundled Node runtime runs independently'
    Push-Location (Join-Path $installRoot 'bridge')
    try {
        & $node --input-type=module -e 'await import("./bridge.mjs"); await import("@microsoft/signalr"); await import("ws"); console.log("Installed bridge dependencies load.");'
        Assert-Installer ($LASTEXITCODE -eq 0) 'Installed bridge and live-event dependencies load without npm installation or account access'
    }
    finally { Pop-Location }

    $oldExtractionRoot = $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR
    try {
        $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = Join-Path $testRoot 'Runtime cache'
        & (Join-Path $repository 'tests\local-toys\CheckPublishedWorker.ps1') -Executable (Join-Path $installRoot 'ShockWave.exe')
        $nugetRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }
        $desktopNative = Join-Path $nugetRoot ("microsoft.windowsdesktop.app.runtime.win-x64\" + $manifest.dotnetRuntime + '\runtimes\win-x64\native')
        # The single-file host need not extract a separate coreclr.dll. Verify
        # the desktop runtime files that this published EXE actually extracts.
        $extracted = @(Get-ChildItem -LiteralPath $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR -File -Recurse)
        foreach ($nativeFile in Get-ChildItem -LiteralPath $desktopNative -File) {
            $bundleFile = $extracted | Where-Object { $_.Name -eq $nativeFile.Name } | Select-Object -First 1
            if (-not $bundleFile -or (Get-FileHash -LiteralPath $bundleFile.FullName -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $nativeFile.FullName -Algorithm SHA256).Hash) {
                throw "Published EXE is missing or has changed desktop runtime file: $($nativeFile.Name)"
            }
        }
        Assert-Installer ($extracted.Count -ge 5) 'DLL-free worker starts and extracts the matching bundled desktop runtime'
    }
    finally { $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = $oldExtractionRoot }

    if ($InvalidHashInstaller) {
        $code = Run-Setup $InvalidHashInstaller $arguments 'rejected-update.log'
        Assert-Installer ($code -ne 0) 'Wrong Bluetooth checksum rejects an update'
        foreach ($file in $manifest.files) {
            if ((Get-FileHash -LiteralPath (Join-Path $installRoot $file.path) -Algorithm SHA256).Hash -ne $file.sha256) {
                throw "A failed download changed the existing installation: $($file.path)"
            }
        }
        Assert-Installer (Test-Path -LiteralPath $registration) 'Failed update preserves installed files and registration'
    }
    $lockedApp = [IO.File]::Open((Join-Path $installRoot 'ShockWave.exe'), [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        $code = Run-Setup $Installer $arguments 'locked-update.log'
        Assert-Installer ($code -ne 0) 'Update asks for normal app closure when the installed EXE is in use'
        $code = Run-Setup (Join-Path $installRoot 'unins000.exe') @() 'locked-uninstall.log'
        Assert-Installer ($code -ne 0 -and (Test-Path $registration)) 'Uninstall refuses an in-use app without deleting files or stopping processes'
    }
    finally { $lockedApp.Dispose() }
    $code = Run-Setup $Installer $arguments 'update.log'
    Assert-Installer ($code -eq 0 -and (Get-Content -LiteralPath (Join-Path $testRoot 'update.log') -Raw).Contains('Reusing verified installed Bluetooth library.')) 'Update reuses an unchanged official Bluetooth library without downloading it again'

    $sentinel = Join-Path $installRoot 'user-added.txt'
    'Fixture-owned extra file; the uninstaller must preserve untracked files.' | Set-Content -LiteralPath $sentinel
    $code = Run-Setup (Join-Path $installRoot 'unins000.exe') @() 'uninstall.log'
    Assert-Installer ($code -eq 0) 'Actual generated uninstaller completes successfully'
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    while ((Test-Path (Join-Path $installRoot 'unins000.exe')) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 100 }
    foreach ($file in $manifest.files) {
        if (Test-Path -LiteralPath (Join-Path $installRoot $file.path)) { throw "Uninstall left an installer-owned file: $($file.path)" }
    }
    Assert-Installer (-not (Test-Path $library) -and -not (Test-Path (Join-Path $installRoot 'unins000.exe'))) 'Uninstall removes every owned payload file, Bluetooth library and uninstaller'
    Assert-Installer (-not (Test-Path $registration) -and -not (Test-Path $groupRoot)) 'Uninstall removes Windows Apps registration and Start menu shortcuts'
    Assert-Installer (Test-Path -LiteralPath $sentinel) 'Uninstall preserves files added by a user'
    $installed = $false
    if ($InvalidHashInstaller) {
        $installRoot = Join-Path $testRoot 'App without Bluetooth'
        $appOnlyArguments = @(('/DIR="' + $installRoot + '"'),'/COMPONENTS=app','/TASKS=')
        # Even the fixture's deliberately wrong Bluetooth hash cannot prevent
        # an app-only installation; that component must never be downloaded.
        $code = Run-Setup $InvalidHashInstaller $appOnlyArguments 'app-only-install.log'
        $installed = Test-Path -LiteralPath (Join-Path $installRoot 'unins000.exe')
        Assert-Installer ($code -eq 0 -and (Test-Path (Join-Path $installRoot 'node.exe'))) 'App-only setup succeeds without downloading Bluetooth support'
        Assert-Installer (-not (Test-Path (Join-Path $installRoot 'native')) -and -not (Test-Path (Join-Path $installRoot 'Avatar-tools'))) 'Deselected optional components are omitted'
        $code = Run-Setup (Join-Path $installRoot 'unins000.exe') @() 'app-only-uninstall.log'
        Assert-Installer ($code -eq 0 -and -not (Test-Path $registration) -and -not (Test-Path $groupRoot)) 'App-only installation also uninstalls cleanly'
        $installed = $false
    }
    Assert-Installer ((Settings-Snapshot) -eq $beforeSettings -and (Test-Path -LiteralPath $userSettings) -eq $settingsExisted) 'Installation, update and removal leave existing personal settings and tokens unchanged'
    foreach ($beforeProcess in $beforeProcesses) {
        $stillRunning = Get-Process -Id $beforeProcess.Id -ErrorAction SilentlyContinue
        Assert-Installer ($null -ne $stillRunning -and $stillRunning.Path -eq $beforeProcess.Path) 'Existing app process was not stopped or restarted'
    }
    $installed = $false
    Write-Host "$checkCount installer checks passed. Logs: $testRoot"
}
finally {
    if ($installed -and (Test-Path -LiteralPath (Join-Path $installRoot 'unins000.exe'))) {
        # A failed assertion still cleans up only this fixture's installation.
        [void](Run-Setup (Join-Path $installRoot 'unins000.exe') @() 'cleanup-uninstall.log')
    }
}
