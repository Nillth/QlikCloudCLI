#requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ModulePath,
    [Parameter(Mandatory)][string]$PeerModulePath,
    [Parameter(Mandatory)][string]$FixtureUrl,
    [Parameter(Mandatory)][string]$OutputPath
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$connections = [Collections.Generic.List[object]]::new()
$opened = [Collections.Generic.List[object]]::new()
$passed = $false
$failure = $null
try {
    $modules = @(Import-Module $ModulePath -PassThru; Import-Module $PeerModulePath -PassThru)
    $sdkNames = @('Qlik.Engine', 'Qlik.Sense.JsonRpc', 'Qlik.Sense.Client', 'Qlik.Engine.Extensions')
    if (@([AppDomain]::CurrentDomain.GetAssemblies() | Where-Object { $_.GetName().Name -in $sdkNames }).Count) {
        throw 'Co-import eagerly loaded Engine assemblies.'
    }
    foreach ($module in $modules) {
        $open = Get-Command ($module.Name + '\Open-QSEApp')
        if ($module.Name -eq 'QlikCloudCLI') {
            $connect = Get-Command ($module.Name + '\Connect-QlikCloud')
            $connection = & $connect -Tenant $FixtureUrl -APIKey 'fixture-not-a-real-key' -PassThru
        } else {
            $connection = [Activator]::CreateInstance($open.ImplementingType.Assembly.GetType('QlikSenseCLI.QlikClient'))
            $connection.GetType().GetField('uRLQES', [Reflection.BindingFlags]'Instance,NonPublic').SetValue($connection, $FixtureUrl)
        }
        $connections.Add($connection)
        $app = & $open -AppId ('coimport-' + $module.Name) -Connection $connection
        $opened.Add([pscustomobject]@{ module = $module.Name; app = $app })
    }
    foreach ($entry in $opened) {
        $read = Get-Command ($entry.module + '\Get-QSEScript')
        if ((& $read -App $entry.app) -notlike ('*coimport-' + $entry.module + '*')) { throw 'Module-qualified read used the wrong app.' }
        $foreign = @($modules | Where-Object Name -NE $entry.module)[0]
        $foreignRead = Get-Command ($foreign.Name + '\Get-QSEScript')
        $rejected = $false
        try { $null = & $foreignRead -App $entry.app }
        catch {
            $rejected = $true
            if ($_.Exception.Message -notmatch 'same module|not opened by this module') { throw 'Foreign app rejection did not explain module ownership.' }
        }
        if (-not $rejected) { throw 'A foreign module accepted another module''s registered app.' }
    }
    $sdkAssemblies = @([AppDomain]::CurrentDomain.GetAssemblies() | Where-Object { $_.GetName().Name -in $sdkNames })
    foreach ($name in $sdkNames) {
        if (@($sdkAssemblies | Where-Object { $_.GetName().Name -eq $name }).Count -gt 1) { throw "Co-import loaded conflicting $name assemblies." }
    }
    $passed = $true
    Write-Host ('PASS Module-qualified co-import in order ' + ($modules.Name -join ', '))
} catch { $failure = $_.ToString(); Write-Host "FAIL Co-import: $failure" }
finally {
    foreach ($entry in $opened) {
        try { $close = Get-Command ($entry.module + '\Close-QSEApp'); & $close -App $entry.app } catch { $passed = $false; $failure = $_.ToString() }
    }
    foreach ($connection in $connections) { try { $connection.Dispose() } catch { } }
    [pscustomobject]@{
        passed = $passed; error = $failure; edition = $PSVersionTable.PSEdition; version = [string]$PSVersionTable.PSVersion
        moduleOrder = @([IO.Path]::GetFileNameWithoutExtension($ModulePath), [IO.Path]::GetFileNameWithoutExtension($PeerModulePath))
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $OutputPath -Encoding UTF8
}
if (-not $passed) { exit 1 }
