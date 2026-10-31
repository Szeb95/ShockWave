param([Parameter(Mandatory)][string]$Executable)
$ErrorActionPreference='Stop'
$panelCheckExe=(Resolve-Path -LiteralPath $Executable).Path
$panelCheckVersion=([version](Get-Item -LiteralPath $panelCheckExe).VersionInfo.FileVersion).ToString(3)
$panelCheckRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$panelCheckProfile=[Environment]::GetFolderPath('UserProfile')
if(-not $panelCheckProfile) {$panelCheckProfile=$env:USERPROFILE}
if(-not $panelCheckProfile -or -not [IO.Path]::IsPathFullyQualified($panelCheckProfile)) {throw 'Test user profile unavailable'}
$panelCheckParent=Join-Path $panelCheckProfile '.shockwave-package-checks'
$panelCheckScratch=Join-Path $panelCheckParent ([guid]::NewGuid().ToString('N'))
$panelCheckArtifacts=Join-Path $panelCheckRoot ('work/package-checks/'+$panelCheckVersion)
New-Item -ItemType Directory -Path $panelCheckScratch,$panelCheckArtifacts -Force | Out-Null
try {
    foreach($case in @(@{Flag='--panel-self-test';Folder='panel'},@{Flag='--steamvr-library-check';Folder='native'})) {
        $output=Join-Path $panelCheckScratch $case.Folder
        $process=Start-Process -FilePath $panelCheckExe -ArgumentList @($case.Flag,('"'+$output+'"')) -WindowStyle Hidden -PassThru
        if(-not $process.WaitForExit(45000)) {Stop-Process -Id $process.Id;throw 'Owned package diagnostic timed out'}
        if($process.ExitCode -ne 0) {if(Test-Path -LiteralPath (Join-Path $output 'failure.txt')) {Get-Content -LiteralPath (Join-Path $output 'failure.txt') | Write-Host};throw 'Published package diagnostic failed'}
        $process.Dispose()
    }
    $panelCheckReport=Get-Content -LiteralPath (Join-Path $panelCheckScratch 'panel/panel-integration.json') -Raw | ConvertFrom-Json
    $panelCheckNative=Get-Content -LiteralPath (Join-Path $panelCheckScratch 'native/native-library.json') -Raw | ConvertFrom-Json
    if(-not $panelCheckReport.passed -or $panelCheckReport.total -lt 171 -or -not $panelCheckNative.passed -or $panelCheckNative.steamVrInitialized) {throw 'Incomplete panel/native package verification'}
    [ordered]@{passed=$true;version=$panelCheckVersion;selfChecks=$panelCheckReport.total;nativeLibraryVerified=$true;physicalOutput=$false;artifacts=$panelCheckArtifacts}
} finally {
    Get-ChildItem -LiteralPath $panelCheckScratch -Force | ForEach-Object {Copy-Item -LiteralPath $_.FullName -Destination $panelCheckArtifacts -Recurse -Force}
    $resolved=(Resolve-Path -LiteralPath $panelCheckScratch).Path
    if(-not $resolved.StartsWith([IO.Path]::GetFullPath($panelCheckParent)+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) {throw 'Invalid diagnostic cleanup path'}
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
