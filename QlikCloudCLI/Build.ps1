#requires -Version 7.4
[CmdletBinding()]
param(
    [ValidatePattern('^[A-Za-z0-9_-]+$')][string] $Configuration = 'Release',
    [ValidatePattern('^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$')][string] $Version,
    [string] $ArtifactsPath,
    [string] $ResultPath,
    [switch] $Sign,
    [switch] $Test,
    [switch] $Package,
    [string] $PackageDirectory = (Join-Path $PSScriptRoot 'dist')
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repositoryRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = (Get-Content -LiteralPath (Join-Path $repositoryRoot 'version.json') -Raw | ConvertFrom-Json).version
}
if ($Version -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$' -or
    @($Version.Split('.') | Where-Object { [long]$_ -gt 65534 }).Count) {
    throw 'Version must be major.minor.patch, with each component between 0 and 65534.'
}
$project = Join-Path $PSScriptRoot 'src/QlikCloudCLI.csproj'
$buildArguments = @('build', $project, '--configuration', $Configuration, '--nologo', "-p:Version=$Version")
$propertyArguments = @('msbuild', $project, '-nologo', "-p:Configuration=$Configuration", "-p:Version=$Version", '-getProperty:TargetDir')
if ($Sign) { $buildArguments += '-p:QlikCloudSignModule=true' }
if (-not [string]::IsNullOrWhiteSpace($ArtifactsPath)) {
    $ArtifactsPath = [IO.Path]::GetFullPath($ArtifactsPath)
    $buildArguments += @('--artifacts-path', $ArtifactsPath)
    $propertyArguments += @('-p:UseArtifactsOutput=true', "-p:ArtifactsPath=$ArtifactsPath")
}
& dotnet @buildArguments
if ($LASTEXITCODE -ne 0) { throw "QlikCloudCLI build failed (exit $LASTEXITCODE)." }
$targetDirectory = & dotnet @propertyArguments
if ($LASTEXITCODE -ne 0) { throw 'Could not locate the built module.' }
$manifest = Join-Path (($targetDirectory -join "`n").Trim()) 'module/QlikCloudCLI/QlikCloudCLI.psd1'
if (-not (Test-Path -LiteralPath $manifest -PathType Leaf)) { throw "The built module manifest was not found: $manifest" }
if ($Test) {
    $testHosts = [Collections.Generic.List[string]]::new()
    if ($IsWindows) {
        $desktopHost = Join-Path $env:WINDIR 'System32/WindowsPowerShell/v1.0/powershell.exe'
        if (-not (Test-Path -LiteralPath $desktopHost -PathType Leaf)) { throw 'Windows PowerShell 5.1 is required for the Windows compatibility checks.' }
        $testHosts.Add($desktopHost)
    }
    $testHosts.Add((Get-Process -Id $PID).Path)
    foreach ($testHost in $testHosts) {
        $hostLabel = [IO.Path]::GetFileNameWithoutExtension($testHost)
        $evidence = Join-Path (Split-Path (Split-Path -Parent $manifest) -Parent) "test-results/$hostLabel.json"
        & $testHost -NoProfile -NonInteractive -File (Join-Path $PSScriptRoot '../tests/QlikCloudCLI.Tests/Invoke-AcceptanceTests.ps1') -ModulePath $manifest -HostPath $testHost -OutputPath $evidence
        if ($LASTEXITCODE -ne 0) { throw "QlikCloudCLI acceptance checks failed in $hostLabel (exit $LASTEXITCODE)." }
    }
}
Write-Output $manifest
$moduleDirectory = Split-Path -Parent $manifest
$result = [ordered]@{ moduleDirectory = $moduleDirectory; manifestPath = $manifest; version = $Version; packagePath = $null; packageSha256 = $null }
if ($Package) {
    $manifestVersion = (Import-PowerShellDataFile -LiteralPath $manifest).ModuleVersion
    if ([string]$manifestVersion -cne $Version) { throw 'Staged module version differs from the requested release version.' }
    $packageRoot = [IO.Path]::GetFullPath($PackageDirectory)
    $null = [IO.Directory]::CreateDirectory($packageRoot)
    $archiveName = 'QlikCloudCLI-{0}-{1}.zip' -f $Version, [datetime]::UtcNow.ToString('yyyyMMdd-HHmmssfff')
    $archive = Join-Path $packageRoot $archiveName
    $temporaryArchive = Join-Path $packageRoot ('.' + [guid]::NewGuid().ToString('N') + '.tmp')
    try {
        # A single top-level QlikCloudCLI folder makes ZIP installation predictable.
        [IO.Compression.ZipFile]::CreateFromDirectory($moduleDirectory, $temporaryArchive, [IO.Compression.CompressionLevel]::Optimal, $true)
        Move-Item -LiteralPath $temporaryArchive -Destination $archive
        $hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
        [IO.File]::WriteAllText($archive + '.sha256', "$hash  $archiveName`n", [Text.UTF8Encoding]::new($false))
        $result.packagePath = $archive
        $result.packageSha256 = $hash
        # Keep the exact archive contents available beside the ZIP for inspection.
        $expandedPackage = Join-Path $packageRoot ([IO.Path]::GetFileNameWithoutExtension($archiveName))
        if (Test-Path -LiteralPath $expandedPackage) { throw "Package extraction directory already exists: $expandedPackage" }
        [IO.Compression.ZipFile]::ExtractToDirectory($archive, $expandedPackage)
        Write-Output (Join-Path $expandedPackage 'QlikCloudCLI/QlikCloudCLI.psd1')
        Write-Output $archive
    }
    finally {
        if (Test-Path -LiteralPath $temporaryArchive) { Remove-Item -LiteralPath $temporaryArchive -Force }
    }
}
if (-not [string]::IsNullOrWhiteSpace($ResultPath)) {
    $resultFile = [IO.Path]::GetFullPath($ResultPath)
    $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($resultFile))
    $result | ConvertTo-Json | Set-Content -LiteralPath $resultFile -Encoding utf8
}
