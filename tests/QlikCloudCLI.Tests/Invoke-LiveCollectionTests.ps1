#requires -Version 7.4
<#
.SYNOPSIS
Validates collection recipes against an explicitly authorized live tenant.
.DESCRIPTION
Creates one uniquely named private collection and operates only on the ID returned
by that creation. Finally deletes it after checking its name, type and item count.
Never finds deletion targets by listing or by name. Existing collections and their
items are not changed. Credentials come from process environment or parameters;
reports contain only status metadata, except the new ID if cleanup needs attention.
Run in a fresh process. A failed/ambiguous create is not retried automatically.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ModulePath,
    [string]$Tenant = $env:QLIK_TENANT,
    [string]$APIKey = $env:QLIK_API_KEY,
    [ValidateRange(5, 60)][int]$RequestTimeoutSeconds = 30,
    [string]$OutputPath = (Join-Path $env:LOCALAPPDATA 'QlikCloudBuild/live-collection/results.json')
)
$ErrorActionPreference = 'Stop'
$VerbosePreference = $DebugPreference = $InformationPreference = $WarningPreference = $ProgressPreference = 'SilentlyContinue'
Set-StrictMode -Version Latest
$connection = $null
$priorLevel = $null
$createdId = $null
$marker = 'QlikCloudCLI-validation-' + [guid]::NewGuid().ToString('N')
$stage = 'setup'
$records = [Collections.Generic.List[object]]::new()
$report = [ordered]@{
    formatVersion = 1; testedAtUtc = [datetime]::UtcNow.ToString('o'); status = 'running'
    sdkSha256 = $null; moduleSha256 = $null; catalogSha256 = $null
    cleanup = 'not-needed'; createdResourceIdNeedingCleanup = $null; operations = @()
}
function Save-Report {
    $report.operations = $records.ToArray()
    $fullPath = [IO.Path]::GetFullPath($OutputPath)
    $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($fullPath))
    [IO.File]::WriteAllText($fullPath, ($report | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
}
function Assert-Result([bool]$Condition) {
    if (-not $Condition) { throw [InvalidOperationException]::new('Live test assertion failed.') }
}
function Get-Status($ErrorRecord) {
    $exception = $ErrorRecord.Exception
    while ($null -ne $exception) {
        $property = $exception.GetType().GetProperty('StatusCode')
        if ($null -ne $property) { return [int]$property.GetValue($exception) }
        $exception = $exception.InnerException
    }
    return $null
}
function Record-Pass([string]$Probe, $StatusCode = $null) {
    $records.Add([pscustomobject]@{ probe = $Probe; status = 'passed'; httpStatus = $StatusCode })
    Save-Report
}
function Assert-OwnedCollection($Collection) {
    Assert-Result ($null -ne $Collection -and $Collection.Id -ceq $createdId)
    Assert-Result ($Collection.Name -ceq $marker -and [string]$Collection.Type -ieq 'Private' -and $Collection.ItemCount -eq 0)
}
try {
    Save-Report
    Assert-Result (-not [string]::IsNullOrWhiteSpace($Tenant) -and -not [string]::IsNullOrWhiteSpace($APIKey))
    $module = Import-Module $ModulePath -PassThru -Force
    $priorLevel = [QlikCloud.Global]::LogLevel
    [QlikCloud.Global]::LogLevel = [Serilog.Events.LogEventLevel]::Fatal
    $report.sdkSha256 = (Get-FileHash ([QlikCloud.RestClientConfig].Assembly.Location)).Hash.ToLowerInvariant()
    $report.moduleSha256 = (Get-FileHash $module.Path).Hash.ToLowerInvariant()
    $report.catalogSha256 = (Get-FileHash (Join-Path (Split-Path $ModulePath -Parent) 'commands.json')).Hash.ToLowerInvariant()
    $connection = Connect-QlikCloud -Tenant $Tenant -APIKey $APIKey -PassThru
    $connection.QCSClient.Config.httpClient.Timeout = [TimeSpan]::FromSeconds($RequestTimeoutSeconds)
    Record-Pass 'connect'

    $stage = 'create-private-collection'
    $body = New-QCCollectionsCollectionsCreateCollectionRequestBodyObject -Name $marker -Type Private -Description $marker
    # Preview must not issue the POST. Only the next invocation creates a resource.
    $null = New-QCCollections -Connection $connection -Body $body -WhatIf
    $report.cleanup = 'create-outcome-unknown'
    Save-Report
    $created = New-QCCollections -Connection $connection -Body $body -Confirm:$false -RawResponse
    $createdId = [string]$created.Object.Id
    Assert-Result (-not [string]::IsNullOrWhiteSpace($createdId))
    $report.cleanup = 'pending'
    $report.createdResourceIdNeedingCleanup = $createdId
    Save-Report
    Assert-OwnedCollection $created.Object
    Record-Pass $stage ([int]$created.StatusCode)

    $stage = 'read-created-collection'
    $read = Get-QCCollections -Connection $connection -CollectionId $createdId -RawResponse
    Assert-OwnedCollection $read.Object
    Record-Pass $stage ([int]$read.StatusCode)

    $stage = 'list-private-collection-by-name'
    $page = Get-QCCollections -Connection $connection -Type Private -Name $marker -Limit 2
    Assert-Result (@($page.Data | Where-Object Id -CEQ $createdId).Count -eq 1)
    Record-Pass $stage

    $stage = 'put-created-collection'
    $put = New-QCCollectionsCollectionsUpdateCollectionRequestBodyObject -Name $marker -Description ($marker + '-put')
    $null = Set-QCCollections -Connection $connection -CollectionId $createdId -Body $put -Confirm:$false
    $read = Get-QCCollections -Connection $connection -CollectionId $createdId
    Assert-OwnedCollection $read
    Assert-Result ($read.Description -ceq ($marker + '-put'))
    Record-Pass $stage

    $stage = 'patch-created-collection'
    $item = New-QCCollectionsCollectionByIdPatchItemObject -Op Replace -Path Description -Value ($marker + '-patch')
    $patch = New-QCCollectionsCollectionByIdPatchObject -Items @($item)
    $null = Update-QCCollections -Connection $connection -CollectionId $createdId -Body $patch -Confirm:$false
    $read = Get-QCCollections -Connection $connection -CollectionId $createdId
    Assert-OwnedCollection $read
    Assert-Result ($read.Description -ceq ($marker + '-patch'))
    Record-Pass $stage
    $report.status = 'passed'
} catch {
    $report.status = 'failed'
    $records.Add([pscustomobject]@{ probe = $stage; status = 'failed'; httpStatus = Get-Status $_; errorType = $_.Exception.GetType().FullName })
} finally {
    if (-not [string]::IsNullOrWhiteSpace($createdId) -and $null -ne $connection) {
        try {
            $read = Get-QCCollections -Connection $connection -CollectionId $createdId
            Assert-OwnedCollection $read
            $deleted = Remove-QCCollections -Connection $connection -CollectionId $createdId -Confirm:$false -RawResponse
            Record-Pass 'delete-created-collection' ([int]$deleted.StatusCode)
            $notFound = $false
            try { $null = Get-QCCollections -Connection $connection -CollectionId $createdId }
            catch { if ((Get-Status $_) -eq 404) { $notFound = $true } else { throw } }
            Assert-Result $notFound
            Record-Pass 'deleted-collection-returns-404' 404
            $report.cleanup = 'deleted-and-verified'
            $report.createdResourceIdNeedingCleanup = $null
        } catch {
            $report.cleanup = 'needs-attention'
            $report.status = 'failed'
            $records.Add([pscustomobject]@{ probe = 'cleanup'; status = 'failed'; httpStatus = Get-Status $_; errorType = $_.Exception.GetType().FullName })
        }
    }
    if ($null -ne $connection) { $connection.Dispose() }
    if ($null -ne $priorLevel) { [QlikCloud.Global]::LogLevel = $priorLevel }
    $APIKey = $null
    Save-Report
}
Write-Output "Live collection test: $($report.status); cleanup=$($report.cleanup). Sanitized report written."
if ($report.status -ne 'passed' -or $report.cleanup -ne 'deleted-and-verified') { exit 1 }
