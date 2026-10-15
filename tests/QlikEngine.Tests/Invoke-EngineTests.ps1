#requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ModulePath,
    [string]$PeerModulePath,
    [string]$PeerRepositoryRoot,
    [string]$OutputDirectory = (Join-Path ([IO.Path]::GetTempPath()) ('QlikEngineTests-' + [guid]::NewGuid().ToString('N'))),
    [string[]]$HostPath = @('powershell.exe', 'pwsh.exe'),
    [switch]$SkipBuild
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
if ($PeerRepositoryRoot) {
    $sourceRoot = Join-Path $PSScriptRoot '../../shared/Qlik.Engine.PowerShell'
    $peerRoot = Join-Path $PeerRepositoryRoot 'shared/Qlik.Engine.PowerShell'
    $sourceFiles = @(Get-ChildItem -LiteralPath $sourceRoot -File | Sort-Object Name)
    $peerFiles = @(Get-ChildItem -LiteralPath $peerRoot -File | Sort-Object Name)
    if (($sourceFiles.Name -join ',') -cne ($peerFiles.Name -join ',')) { throw 'Shared Engine source file sets differ.' }
    foreach ($file in $sourceFiles) {
        $peerFile = Join-Path $peerRoot $file.Name
        if ((Get-FileHash -LiteralPath $file.FullName).Hash -cne (Get-FileHash -LiteralPath $peerFile).Hash) {
            throw "Shared Engine source differs: $($file.Name)"
        }
    }
    Write-Host "PASS Shared source parity ($($sourceFiles.Count) files)"
}
$project = Join-Path $PSScriptRoot 'FixtureServer/FixtureServer.csproj'
if (-not $SkipBuild) {
    & dotnet build $project --configuration Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Engine fixture server build failed.' }
}
$serverDll = Join-Path $PSScriptRoot 'FixtureServer/bin/Release/net8.0/FixtureServer.dll'
$start = [Diagnostics.ProcessStartInfo]::new()
$start.FileName = (Get-Command dotnet -CommandType Application).Source
$start.Arguments = '"' + $serverDll + '"'
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
$server = [Diagnostics.Process]::Start($start)
$reports = [Collections.Generic.List[object]]::new()
$coImportReports = [Collections.Generic.List[object]]::new()
$failures = [Collections.Generic.List[string]]::new()
try {
    $ready = $server.StandardOutput.ReadLineAsync()
    if (-not $ready.Wait(15000)) { throw 'The loopback Engine fixture did not start within 15 seconds.' }
    $line = $ready.Result
    if ($line -notmatch '^FIXTURE_URL=(http://127\.0\.0\.1:\d+)$') { throw "Unexpected fixture startup output: $line" }
    $fixtureUrl = $Matches[1]
    foreach ($module in @($ModulePath, $PeerModulePath) | Where-Object { $_ }) {
        $resolved = (Resolve-Path -LiteralPath $module).Path
        $moduleName = [IO.Path]::GetFileNameWithoutExtension($resolved)
        $sourceDirectory = Split-Path -Parent $resolved
        $withoutSdk = Join-Path $OutputDirectory ('without-sdk/' + $moduleName + '-' + [guid]::NewGuid().ToString('N'))
        $sdkNames = @('Qlik.Engine.dll', 'Qlik.Sense.JsonRpc.dll', 'Qlik.Sense.Client.dll', 'Qlik.Engine.Extensions.dll')
        foreach ($file in Get-ChildItem -LiteralPath $sourceDirectory -File -Recurse | Where-Object Name -NotIn $sdkNames) {
            $relative = $file.FullName.Substring($sourceDirectory.TrimEnd('\', '/').Length + 1)
            $destination = Join-Path $withoutSdk $relative
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
            Copy-Item -LiteralPath $file.FullName -Destination $destination
        }
        foreach ($hostName in $HostPath) {
            $hostExecutable = (Get-Command $hostName -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
            $hostLabel = [IO.Path]::GetFileNameWithoutExtension($hostExecutable)
            $report = Join-Path $OutputDirectory ($moduleName + '-' + $hostLabel + '.json')
            & $hostExecutable -NoLogo -NoProfile -NonInteractive -File (Join-Path $PSScriptRoot 'Invoke-EngineWorker.ps1') -ModulePath $resolved -FixtureUrl $fixtureUrl -OutputPath $report
            $workerExit = $LASTEXITCODE
            if (Test-Path -LiteralPath $report) { $reports.Add((Get-Content -LiteralPath $report -Raw | ConvertFrom-Json)) }
            if ($workerExit -ne 0) { $failures.Add("$moduleName under $hostLabel") }
            $missingReport = Join-Path $OutputDirectory ($moduleName + '-' + $hostLabel + '-missing-sdk.json')
            & $hostExecutable -NoLogo -NoProfile -NonInteractive -File (Join-Path $PSScriptRoot 'Invoke-EngineWorker.ps1') -ModulePath (Join-Path $withoutSdk ($moduleName + '.psd1')) -FixtureUrl $fixtureUrl -OutputPath $missingReport -MissingSdk
            if ($LASTEXITCODE -ne 0) { $failures.Add("$moduleName missing SDK under $hostLabel") }
            if (Test-Path -LiteralPath $missingReport) { $reports.Add((Get-Content -LiteralPath $missingReport -Raw | ConvertFrom-Json)) }
        }
    }
    if ($reports.Count -gt 1) {
        $expected = $reports[0].metadata | ConvertTo-Json -Depth 20 -Compress
        foreach ($report in $reports | Select-Object -Skip 1) {
            if (($report.metadata | ConvertTo-Json -Depth 20 -Compress) -cne $expected) {
                $failures.Add("Command metadata parity: $($report.module) under $($report.edition)")
            }
        }
    }
    if ($PeerModulePath) {
        foreach ($hostName in $HostPath) {
            $hostExecutable = (Get-Command $hostName -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
            $hostLabel = [IO.Path]::GetFileNameWithoutExtension($hostExecutable)
            foreach ($order in @(@($ModulePath, $PeerModulePath), @($PeerModulePath, $ModulePath))) {
                $first = [IO.Path]::GetFileNameWithoutExtension($order[0])
                $coImportReport = Join-Path $OutputDirectory ('coimport-' + $first + '-' + $hostLabel + '.json')
                & $hostExecutable -NoLogo -NoProfile -NonInteractive -File (Join-Path $PSScriptRoot 'Invoke-CoImportWorker.ps1') -ModulePath $order[0] -PeerModulePath $order[1] -FixtureUrl $fixtureUrl -OutputPath $coImportReport
                if ($LASTEXITCODE -ne 0) { $failures.Add("Co-import $first first under $hostLabel") }
                if (Test-Path -LiteralPath $coImportReport) { $coImportReports.Add((Get-Content -LiteralPath $coImportReport -Raw | ConvertFrom-Json)) }
            }
        }
    }
    $summary = [pscustomobject]@{ passed = $failures.Count -eq 0; reports = @($reports); coImport = @($coImportReports); failures = @($failures) }
    $summary | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'summary.json') -Encoding UTF8
    Write-Host "Engine test reports: $OutputDirectory"
    if ($failures.Count) { throw ('Engine acceptance failed: ' + ($failures -join '; ')) }
}
finally {
    if ($null -ne (Get-Variable fixtureUrl -ErrorAction SilentlyContinue)) {
        try { Invoke-RestMethod -Uri ($fixtureUrl + '/__fixture/state') | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'fixture-traffic.json') -Encoding UTF8 } catch { }
    }
    if (-not $server.HasExited) { $server.Kill(); $server.WaitForExit(10000) | Out-Null }
    $server.Dispose()
}
