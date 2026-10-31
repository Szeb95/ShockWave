param([Parameter(Mandatory)][string]$PackageZip)
$ErrorActionPreference='Stop'
$compatRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$compatPackage=(Resolve-Path -LiteralPath $PackageZip).Path
$compatVersion=[IO.Path]::GetFileName($compatPackage) -replace '^ShockWave-','' -replace '-win-x64\.zip$',''
foreach($legacy in @('0.7.0','0.7.11','0.8.0')) {
    dotnet build (Join-Path $PSScriptRoot 'UpdaterCompatibility.Checks.csproj') -c Release "-p:LegacyVersion=$legacy" -p:UseAppHost=false --nologo -v:q
    if($LASTEXITCODE -ne 0) {throw 'Legacy compatibility checker build failed'}
    $checker=Join-Path $PSScriptRoot "bin/$legacy/Release/net8.0-windows/UpdaterCompatibility.Checks.dll"
    dotnet $checker --package $compatPackage --output (Join-Path $compatRoot "work/package-compat-checks/$compatVersion/$legacy")
    if($LASTEXITCODE -ne 0) {throw ('Legacy package compatibility failed: '+$legacy)}
}
