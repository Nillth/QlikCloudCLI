#requires -Version 7.2
<#
.SYNOPSIS
Runs optional, bounded read-only smoke tests against an explicitly selected Qlik tenant.
.DESCRIPTION
Credentials may be supplied through QLIK_TENANT, QLIK_API_KEY, QLIK_CLIENT_ID,
QLIK_CLIENT_SECRET and QLIK_OAUTH_SCOPE. They are never written to disk or passed
on the worker command line. Run this only when live tenant access is authorized.

The worker calls only catalogued GET operations, apart from Connect-QlikCloud's
OAuth client-credentials token issuance when client credentials were supplied.
It does not open a browser, exchange tokens, call Engine, or modify tenant data.
Private response objects and any discovered IDs remain in worker memory.

The worker process deadline bounds the entire run, including OAuth setup and
server-directed retry delays. The per-request timeout is also applied when the
HTTP client has not yet sent a request. OAuth's client has already sent its token
request, so its immutable timeout remains unchanged and the worker deadline is
the applicable bound; transportTimeouts records this explicitly.

Report values are limited to static probe labels, status/category/type metadata,
counts, durations, versions and hashes of local module/catalog binaries. HTTP
401/403 means denied, never passed. Skipped probes are not evidence of success.

An empty or omitted OAuthScope uses the connection cmdlet's default unless the
scope was explicitly supplied, including an explicit empty environment value.
For reproducibility, supply QLIK_OAUTH_SCOPE with the grant's intended scopes.
.EXAMPLE
$env:QLIK_TENANT = 'https://tenant.example/'
# Supply credentials in process memory using your normal secret retrieval method.
& .\Invoke-LiveReadOnlyTests.ps1 -ModulePath 'C:\build\module\QlikCloudCLI.psd1'
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ModulePath,
    [string]$Tenant = $env:QLIK_TENANT,
    [string]$APIKey = $env:QLIK_API_KEY,
    [string]$ClientID = $env:QLIK_CLIENT_ID,
    [string]$ClientSecret = $env:QLIK_CLIENT_SECRET,
    [AllowEmptyString()][string]$OAuthScope = $env:QLIK_OAUTH_SCOPE,
    [ValidateRange(1, 5)][int]$Limit = 1,
    [ValidateRange(5, 60)][int]$RequestTimeoutSeconds = 30,
    [ValidateRange(30, 600)][int]$OverallTimeoutSeconds = 180,
    [string]$OutputPath = (Join-Path $env:LOCALAPPDATA 'QlikCloudBuild/live-read-only/results.json'),
    [switch]$SkipById,
    [switch]$Worker
)

$ErrorActionPreference = 'Stop'
$VerbosePreference = 'SilentlyContinue'
$DebugPreference = 'SilentlyContinue'
$InformationPreference = 'SilentlyContinue'
$WarningPreference = 'SilentlyContinue'
$ProgressPreference = 'SilentlyContinue'
Set-StrictMode -Version Latest

function Save-SafeReport($Report) {
    $directory = Split-Path -Parent ([IO.Path]::GetFullPath($OutputPath))
    $null = [IO.Directory]::CreateDirectory($directory)
    $text = $Report | ConvertTo-Json -Depth 12 -Compress
    [IO.File]::WriteAllText([IO.Path]::GetFullPath($OutputPath), $text, [Text.UTF8Encoding]::new($false))
}

function New-FailureReport([string]$Status) {
    return [ordered]@{
        formatVersion = 1
        status = $Status
        passed = 0
        failed = 1
        denied = 0
        skipped = 0
        tenantMutationsAttempted = 0
        operations = @()
    }
}

if (-not $Worker) {
    # A separate process also bounds OAuth setup and server-directed retry waits,
    # which are outside the SDK's per-request HttpClient timeout.
    $process = $null
    try {
        $resolvedModule = (Resolve-Path -LiteralPath $ModulePath).Path
        $resolvedOutput = [IO.Path]::GetFullPath($OutputPath)
        if ([string]::IsNullOrWhiteSpace($Tenant)) { throw [ArgumentException]::new() }
        if ([string]::IsNullOrWhiteSpace($APIKey) -and [string]::IsNullOrWhiteSpace($ClientID) -and [string]::IsNullOrWhiteSpace($ClientSecret)) { throw [ArgumentException]::new() }
        $start = [Diagnostics.ProcessStartInfo]::new()
        $start.FileName = Join-Path $PSHOME 'pwsh.exe'
        $start.UseShellExecute = $false
        $start.CreateNoWindow = $true
        $start.RedirectStandardOutput = $true
        $start.RedirectStandardError = $true
        foreach ($argument in @('-NoLogo', '-NoProfile', '-NonInteractive', '-File', $PSCommandPath, '-Worker', '-ModulePath', $resolvedModule,
            '-OutputPath', $resolvedOutput, '-Limit', [string]$Limit, '-RequestTimeoutSeconds', [string]$RequestTimeoutSeconds, '-OverallTimeoutSeconds', [string]$OverallTimeoutSeconds)) {
            $start.ArgumentList.Add($argument)
        }
        if ($SkipById) { $start.ArgumentList.Add('-SkipById') }
        foreach ($entry in @{
            QLIK_TENANT = $Tenant; QLIK_API_KEY = $APIKey; QLIK_CLIENT_ID = $ClientID; QLIK_CLIENT_SECRET = $ClientSecret
        }.GetEnumerator()) {
            if ([string]::IsNullOrWhiteSpace([string]$entry.Value)) { $null = $start.Environment.Remove($entry.Key) }
            else { $start.Environment[$entry.Key] = [string]$entry.Value }
        }
        if ($PSBoundParameters.ContainsKey('OAuthScope') -or $null -ne [Environment]::GetEnvironmentVariable('QLIK_OAUTH_SCOPE')) {
            $start.Environment['QLIK_OAUTH_SCOPE'] = $OAuthScope
            $start.Environment['QLIK_LIVE_EXPLICIT_SCOPE'] = '1'
        } else {
            $null = $start.Environment.Remove('QLIK_OAUTH_SCOPE')
            $null = $start.Environment.Remove('QLIK_LIVE_EXPLICIT_SCOPE')
        }
        $process = [Diagnostics.Process]::new()
        $process.StartInfo = $start
        Save-SafeReport (New-FailureReport 'worker-starting')
        $null = $process.Start()
        # Never print or persist worker streams, including unexpected CLR/library diagnostics.
        $standardOutput = $process.StandardOutput.ReadToEndAsync()
        $standardError = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($OverallTimeoutSeconds * 1000)) {
            $process.Kill($true)
            $null = $process.WaitForExit(5000)
            Save-SafeReport (New-FailureReport 'worker-timeout')
            Write-Output 'Live read-only smoke: worker-timeout. Sanitized report written.'
            exit 2
        }
        $null = $standardOutput.GetAwaiter().GetResult()
        $null = $standardError.GetAwaiter().GetResult()
        if (-not (Test-Path -LiteralPath $resolvedOutput -PathType Leaf)) {
            Save-SafeReport (New-FailureReport 'worker-failed')
            Write-Output 'Live read-only smoke: worker-failed. Sanitized report written.'
            exit 2
        }
        $summary = [IO.File]::ReadAllText($resolvedOutput) | ConvertFrom-Json -Depth 12
        Write-Output "Live read-only smoke: passed=$($summary.passed), denied=$($summary.denied), failed=$($summary.failed), skipped=$($summary.skipped). Sanitized report written."
        exit $process.ExitCode
    } catch {
        try { Save-SafeReport (New-FailureReport 'configuration-or-worker-failure') } catch { }
        Write-Output 'Live read-only smoke: configuration-or-worker-failure. No diagnostic payload was emitted.'
        exit 2
    } finally {
        if ($null -ne $process) { $process.Dispose() }
        $APIKey = $null
        $ClientSecret = $null
        $ClientID = $null
    }
}

$records = [Collections.Generic.List[object]]::new()
$connections = [Collections.Generic.List[object]]::new()
$priorLevel = $null
$overall = [Diagnostics.Stopwatch]::StartNew()
$report = [ordered]@{
    formatVersion = 1
    status = 'running'
    passed = 0
    failed = 0
    denied = 0
    skipped = 0
    tenantMutationsAttempted = 0
    oauthTokenRequestsAttempted = 0
    powershellVersion = $PSVersionTable.PSVersion.ToString()
    moduleVersion = $null
    sdkSha256 = $null
    moduleSha256 = $null
    catalogSha256 = $null
    transportTimeouts = @()
    operations = @()
}

function Update-Report {
    $report.passed = @($records | Where-Object status -eq 'passed').Count
    $report.failed = @($records | Where-Object status -eq 'failed').Count
    $report.denied = @($records | Where-Object status -eq 'denied').Count
    $report.skipped = @($records | Where-Object status -eq 'skipped').Count
    $report.operations = $records.ToArray()
    Save-SafeReport $report
}

function Add-Skipped([string]$Profile, [string]$Probe, [string]$Reason) {
    $records.Add([pscustomobject]@{ profile = $Profile; probe = $Probe; status = 'skipped'; reason = $Reason; httpStatus = $null; milliseconds = 0 })
    Update-Report
}

function Get-EntityCount($Value) {
    if ($null -eq $Value) { return 0 }
    if ($Value -is [QlikCloud.APIResponse] -and $null -ne $Value.PSObject.Properties['Object']) { $Value = $Value.Object }
    if ($null -eq $Value) { return 0 }
    foreach ($name in @('Data', 'Items')) {
        if ($null -ne $Value.PSObject.Properties[$name] -and $Value.$name -is [Collections.IEnumerable] -and $Value.$name -isnot [string]) {
            return @($Value.$name).Count
        }
    }
    if ($Value -is [Collections.IEnumerable] -and $Value -isnot [string]) { return @($Value).Count }
    return 1
}

function Get-SafeHttpStatus($ErrorRecord) {
    $exception = $ErrorRecord.Exception
    for ($i = 0; $i -lt 8 -and $null -ne $exception; $i++) {
        $property = $exception.GetType().GetProperty('StatusCode')
        if ($null -ne $property) {
            $value = $property.GetValue($exception)
            if ($null -ne $value) {
                $number = [int]$value
                if ($number -ge 100 -and $number -le 599) { return $number }
            }
        }
        $exception = $exception.InnerException
    }
    return $null
}

function Invoke-Probe([string]$Profile, [string]$Name, [scriptblock]$Action, [switch]$ExpectRaw) {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    try {
        $values = @(& $Action 2>$null 3>$null 4>$null 5>$null 6>$null)
        $value = if ($values.Count -eq 1) { $values[0] } else { $values }
        if ($ExpectRaw -and $value -isnot [QlikCloud.APIResponse]) { throw [InvalidOperationException]::new() }
        $statusCode = if ($value -is [QlikCloud.APIResponse]) { [int]$value.StatusCode } else { $null }
        if ($null -ne $statusCode -and ($statusCode -lt 200 -or $statusCode -ge 300)) { throw [InvalidOperationException]::new() }
        $records.Add([pscustomobject]@{
            profile = $Profile; probe = $Name; status = 'passed'; httpStatus = $statusCode
            outputType = if ($null -eq $value) { $null } else { $value.GetType().FullName }
            entityCount = Get-EntityCount $value
            outputCount = $values.Count
            rawCharacterCount = if ($value -is [QlikCloud.APIResponse] -and $null -ne $value.RawContent) { $value.RawContent.Length } else { $null }
            headerCount = if ($value -is [QlikCloud.APIResponse] -and $null -ne $value.Headers) { $value.Headers.Count } else { $null }
            milliseconds = $timer.ElapsedMilliseconds
        })
        Update-Report
        return [pscustomobject]@{ Succeeded = $true; Value = $value }
    } catch {
        $httpStatus = Get-SafeHttpStatus $_
        $records.Add([pscustomobject]@{
            profile = $Profile; probe = $Name
            status = if ($httpStatus -in @(401, 403)) { 'denied' } else { 'failed' }
            httpStatus = $httpStatus
            errorType = $_.Exception.GetType().FullName
            milliseconds = $timer.ElapsedMilliseconds
        })
        Update-Report
        return [pscustomobject]@{ Succeeded = $false; Value = $null }
    }
}

function Resolve-GetOperation([string]$Api, [string]$Path) {
    $matches = @($catalog.operations | Where-Object { $_.api -eq $Api -and $_.httpMethod -eq 'GET' -and $_.path -eq $Path })
    if ($matches.Count -ne 1) { return $null }
    $command = Get-Command -Name $matches[0].commandName -Module $module.Name -ErrorAction Stop
    if ($command.CommandType -ne [Management.Automation.CommandTypes]::Cmdlet -or -not $command.Name.StartsWith('Get-', [StringComparison]::Ordinal) -or -not $command.ImplementingType.FullName.StartsWith('QlikCloudCLI.Generated.', [StringComparison]::Ordinal)) {
        throw [InvalidOperationException]::new()
    }
    $sets = @($command.ParameterSets | Where-Object Name -EQ $matches[0].parameterSetName)
    if ($sets.Count -ne 1) { throw [InvalidOperationException]::new('The GET catalog parameter set is missing from the installed command.') }
    return [pscustomobject]@{ Operation = $matches[0]; Command = $command }
}

function Invoke-ReadProbe([string]$Profile, [string]$Name, $Binding, $Connection, [hashtable]$Parameters, [switch]$Raw) {
    if ($null -eq $Binding) {
        Add-Skipped $Profile $Name 'get-operation-absent-from-catalog'
        return [pscustomobject]@{ Succeeded = $false; Value = $null }
    }
    foreach ($argumentName in @($Parameters.Keys)) {
        if (@($Binding.Operation.parameters | ForEach-Object name) -notcontains $argumentName) {
            Add-Skipped $Profile $Name 'parameter-absent-from-catalog'
            return [pscustomobject]@{ Succeeded = $false; Value = $null }
        }
    }
    $Parameters.Connection = $Connection
    $Parameters.ErrorAction = 'Stop'
    if ($Raw) { $Parameters.RawResponse = $true }
    return Invoke-Probe $Profile $Name { & $Binding.Command @Parameters } -ExpectRaw:$Raw
}

function Get-FirstEntity($Result) {
    if (-not $Result.Succeeded -or $null -eq $Result.Value) { return $null }
    $value = $Result.Value
    if ($value -is [QlikCloud.APIResponse] -and $null -ne $value.PSObject.Properties['Object']) { $value = $value.Object }
    foreach ($name in @('Data', 'Items')) {
        if ($null -ne $value -and $null -ne $value.PSObject.Properties[$name]) { return @($value.$name) | Select-Object -First 1 }
    }
    return $null
}

function Run-ReadProbes([string]$Profile, $Connection) {
    $requestTimeoutApplied = $true
    try { $Connection.QCSClient.Config.httpClient.Timeout = [TimeSpan]::FromSeconds($RequestTimeoutSeconds) }
    catch {
        # PowerShell wraps HttpClient's InvalidOperationException in a property-
        # assignment exception after OAuth has already used the transport.
        $cause = $_.Exception
        for ($i = 0; $i -lt 8 -and $null -ne $cause.InnerException; $i++) { $cause = $cause.InnerException }
        if ($cause -isnot [InvalidOperationException]) { throw }
        $requestTimeoutApplied = $false
    }
    $report.transportTimeouts += [pscustomobject]@{
        profile = $Profile
        status = if ($requestTimeoutApplied) { 'per-request-and-worker-bound' } else { 'worker-bound-client-already-used' }
        requestTimeoutSeconds = if ($requestTimeoutApplied) { $RequestTimeoutSeconds } else { $null }
        workerTimeoutSeconds = $OverallTimeoutSeconds
    }
    $null = Invoke-ReadProbe $Profile 'users-me' (Resolve-GetOperation users '/api/v1/users/me') $Connection @{} -Raw
    $users = Invoke-ReadProbe $Profile 'users-list-typed' (Resolve-GetOperation users '/api/v1/users') $Connection @{ Limit = $Limit }
    $null = Invoke-ReadProbe $Profile 'users-list-raw' (Resolve-GetOperation users '/api/v1/users') $Connection @{ Limit = $Limit } -Raw
    $null = Invoke-ReadProbe $Profile 'spaces-list' (Resolve-GetOperation spaces '/api/v1/spaces') $Connection @{ Limit = $Limit } -Raw
    $null = Invoke-ReadProbe $Profile 'items-list' (Resolve-GetOperation items '/api/v1/items') $Connection @{ Limit = $Limit } -Raw
    $apps = Invoke-ReadProbe $Profile 'apps-list-via-items' (Resolve-GetOperation items '/api/v1/items') $Connection @{ Limit = $Limit; ResourceType = 'app' } -Raw
    if (-not $SkipById) {
        $user = Get-FirstEntity $users
        if ($null -ne $user -and $null -ne $user.PSObject.Properties['Id'] -and -not [string]::IsNullOrWhiteSpace([string]$user.Id)) {
            $null = Invoke-ReadProbe $Profile 'user-by-discovered-id' (Resolve-GetOperation users '/api/v1/users/{userId}') $Connection @{ UserId = $user.Id } -Raw
        } else { Add-Skipped $Profile 'user-by-discovered-id' 'no-readable-entity-id' }
        $app = Get-FirstEntity $apps
        if ($null -ne $app -and $null -ne $app.PSObject.Properties['ResourceId'] -and -not [string]::IsNullOrWhiteSpace([string]$app.ResourceId)) {
            $null = Invoke-ReadProbe $Profile 'app-by-discovered-id' (Resolve-GetOperation apps '/api/v1/apps/{appId}') $Connection @{ AppId = $app.ResourceId } -Raw
        } else { Add-Skipped $Profile 'app-by-discovered-id' 'no-readable-entity-id' }
        $user = $null
        $app = $null
    }
}

try {
    # Do not allow a failed run to leave an earlier success report in place.
    Update-Report
    $module = Import-Module -Name $ModulePath -Force -PassThru 2>$null 3>$null 4>$null 5>$null 6>$null
    $priorLevel = [QlikCloud.Global]::LogLevel
    [QlikCloud.Global]::LogLevel = [Serilog.Events.LogEventLevel]::Fatal
    $catalogPath = Join-Path (Split-Path -Parent $ModulePath) 'commands.json'
    $catalog = [IO.File]::ReadAllText($catalogPath) | ConvertFrom-Json -Depth 100
    $report.moduleVersion = $module.Version.ToString()
    $report.sdkSha256 = (Get-FileHash -LiteralPath ([QlikCloud.RestClientConfig].Assembly.Location)).Hash.ToLowerInvariant()
    $report.moduleSha256 = (Get-FileHash -LiteralPath $module.Path).Hash.ToLowerInvariant()
    $report.catalogSha256 = (Get-FileHash -LiteralPath $catalogPath).Hash.ToLowerInvariant()
    if ($catalog.assemblySha256 -ne $report.sdkSha256) { throw [InvalidOperationException]::new() }

    if (-not [string]::IsNullOrWhiteSpace($APIKey)) {
        $keyConnection = Invoke-Probe 'api-key' 'connect' {
            Connect-QlikCloud -Tenant $Tenant -APIKey $APIKey -PassThru -ErrorAction Stop
        }
        if ($keyConnection.Succeeded) {
            $connections.Add($keyConnection.Value)
            Run-ReadProbes 'api-key' $keyConnection.Value
        }
    } else { Add-Skipped 'api-key' 'connect' 'credentials-not-supplied' }

    if (-not [string]::IsNullOrWhiteSpace($ClientID) -and -not [string]::IsNullOrWhiteSpace($ClientSecret)) {
        $report.oauthTokenRequestsAttempted++
        $oauthParameters = @{ Tenant = $Tenant; ClientID = $ClientID; ClientSecret = $ClientSecret; ClientCredentials = $true; PassThru = $true; ErrorAction = 'Stop' }
        if ($env:QLIK_LIVE_EXPLICIT_SCOPE -eq '1' -or $PSBoundParameters.ContainsKey('OAuthScope')) { $oauthParameters.Scope = $OAuthScope }
        $oauthConnection = Invoke-Probe 'oauth-client-credentials' 'connect' { Connect-QlikCloud @oauthParameters }
        $oauthParameters.Clear()
        if ($oauthConnection.Succeeded) {
            $connections.Add($oauthConnection.Value)
            Run-ReadProbes 'oauth-client-credentials' $oauthConnection.Value
        }
    } elseif (-not [string]::IsNullOrWhiteSpace($ClientID) -or -not [string]::IsNullOrWhiteSpace($ClientSecret)) {
        $records.Add([pscustomobject]@{ profile = 'oauth-client-credentials'; probe = 'connect'; status = 'failed'; reason = 'incomplete-credentials'; httpStatus = $null; milliseconds = 0 })
    } else { Add-Skipped 'oauth-client-credentials' 'connect' 'credentials-not-supplied' }
    $report.status = 'completed'
} catch {
    $records.Add([pscustomobject]@{ profile = 'runner'; probe = 'setup-or-execution'; status = 'failed'; errorType = $_.Exception.GetType().FullName; httpStatus = $null; milliseconds = $overall.ElapsedMilliseconds })
    $report.status = 'failed'
} finally {
    foreach ($connection in $connections) { try { $connection.Dispose() } catch { } }
    if ($null -ne $priorLevel) { [QlikCloud.Global]::LogLevel = $priorLevel }
    $APIKey = $null
    $ClientSecret = $null
    $ClientID = $null
    Update-Report
}
if ($report.failed -gt 0 -or $report.denied -gt 0) { exit 1 }
if ($report.passed -eq 0) { exit 2 }
exit 0
