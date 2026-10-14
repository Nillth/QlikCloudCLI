#requires -Version 7.4
[CmdletBinding()]
param(
    [string] $RepositoryRoot = (Split-Path -Parent $PSScriptRoot),
    [Parameter(Mandatory)][string] $ArtifactsPath,
    [string] $Version,
    [switch] $Sign,
    [string] $ResultPath
)
$ErrorActionPreference = 'Stop'
$RepositoryRoot = [IO.Path]::GetFullPath($RepositoryRoot)
$ArtifactsPath = [IO.Path]::GetFullPath($ArtifactsPath)
$null = [IO.Directory]::CreateDirectory($ArtifactsPath)
& (Join-Path $RepositoryRoot 'tests/Automation.Tests/Invoke-Tests.ps1')
& (Join-Path $RepositoryRoot 'tests/Automation.Tests/Signing.Tests.ps1')
foreach ($name in @('QlikCloud.Generator.Tests','QlikCloudCLI.Generator.Tests')) {
    & dotnet test (Join-Path $RepositoryRoot "tests/$name/$name.csproj") --configuration Release --artifacts-path (Join-Path $ArtifactsPath $name) --logger trx --results-directory (Join-Path $ArtifactsPath "test-results/$name") --nologo
    if ($LASTEXITCODE -ne 0) { throw "$name failed (exit $LASTEXITCODE)." }
}
$arguments = @{ Configuration = 'Release'; ArtifactsPath = (Join-Path $ArtifactsPath 'build'); Test = $true; Package = $true; PackageDirectory = (Join-Path $ArtifactsPath 'packages'); Sign = $Sign.IsPresent }
if ($Version) { $arguments.Version = $Version }
if ($ResultPath) { $arguments.ResultPath = $ResultPath }
& (Join-Path $RepositoryRoot 'QlikCloudCLI/Build.ps1') @arguments
