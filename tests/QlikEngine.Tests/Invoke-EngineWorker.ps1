#requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ModulePath,
    [Parameter(Mandatory)][string]$FixtureUrl,
    [Parameter(Mandatory)][string]$OutputPath,
    [switch]$MissingSdk
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$results = [Collections.Generic.List[object]]::new()
$connections = [Collections.Generic.List[object]]::new()
$metadata = @()
function Assert-True([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Assert-Equal($Expected, $Actual, [string]$Message) {
    if ($Expected -cne $Actual) { throw "$Message Expected <$Expected>; got <$Actual>." }
}
function Assert-Throws([scriptblock]$Body, [string]$Message) {
    $thrown = $false
    try { & $Body | Out-Null } catch { $thrown = $true }
    Assert-True $thrown $Message
}
function Test-Case([string]$Name, [scriptblock]$Body) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    try {
        & $Body | Out-Null
        $results.Add([pscustomobject]@{ name = $Name; passed = $true; milliseconds = $watch.ElapsedMilliseconds; error = $null })
        Write-Host "PASS $Name"
    } catch {
        $results.Add([pscustomobject]@{ name = $Name; passed = $false; milliseconds = $watch.ElapsedMilliseconds; error = $_.ToString(); position = $_.InvocationInfo.PositionMessage; stack = $_.ScriptStackTrace })
        Write-Host "FAIL $Name`: $($_.Exception.Message)"
    }
}
function Get-LoadedSdk { @([AppDomain]::CurrentDomain.GetAssemblies() | Where-Object { $_.GetName().Name -in @('Qlik.Engine', 'Qlik.Sense.JsonRpc', 'Qlik.Sense.Client', 'Qlik.Engine.Extensions') }) }
function Get-FixtureState { Invoke-RestMethod -Uri ($FixtureUrl + '/__fixture/state') -Method Get }
function Wait-Until([scriptblock]$Condition, [string]$Message) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while ($watch.ElapsedMilliseconds -lt 8000) {
        if (& $Condition) { return }
        Start-Sleep -Milliseconds 40
    }
    throw $Message
}
function New-FixtureConnection {
    if ($module.Name -eq 'QlikCloudCLI') {
        $connection = Connect-QlikCloud -Tenant $FixtureUrl -APIKey 'fixture-not-a-real-key' -PassThru
    } else {
        # Bypass only the remote QRS authentication handshake. The actual Sense
        # Engine adapter still creates/authenticates/opens its normal SDK Location.
        $connection = [Activator]::CreateInstance($moduleAssembly.GetType('QlikSenseCLI.QlikClient', $true))
        $flags = [Reflection.BindingFlags]'Instance,NonPublic'
        $connection.GetType().GetField('uRLQES', $flags).SetValue($connection, $FixtureUrl)
        $connection.GetType().GetField('hostName', $flags).SetValue($connection, '127.0.0.1')
    }
    $connections.Add($connection)
    return $connection
}
function Set-FixtureDefault($Connection) {
    if ($module.Name -eq 'QlikCloudCLI') { [QlikCloud.Common.ConnectionManager]::SetConnection($Connection) }
    else {
        $store = $moduleAssembly.GetType('QlikSenseCLI.Globals+VariableStore', $true)
        $store.GetField('QlikClient', [Reflection.BindingFlags]'Static,Public').SetValue($null, $Connection)
    }
}
function Disconnect-Fixture($Connection) {
    $name = if ($module.Name -eq 'QlikCloudCLI') { 'Disconnect-QlikCloud' } else { 'Disconnect-QlikSense' }
    $command = Get-Command $name -Module $module.Name
    $parameters = @{ Connection = $Connection }
    if (-not $command.Parameters.ContainsKey('Connection')) { $parameters = @{ QlikClient = $Connection } }
    if ($command.Parameters.ContainsKey('Confirm')) { $parameters.Confirm = $false }
    & $command @parameters
}
try {
    Assert-Equal 0 @(Get-LoadedSdk).Count 'A fresh process unexpectedly already contains the Qlik SDK.'
    $module = Import-Module -Name $ModulePath -Force -PassThru
    $moduleAssembly = (Get-Command Open-QSEApp -Module $module.Name).ImplementingType.Assembly
    Test-Case 'Import and command discovery leave all Engine SDK assemblies unloaded' {
        Assert-Equal 0 @(Get-LoadedSdk).Count 'Import eagerly loaded Engine SDK assemblies.'
    }
    $commandNames = @(
        'Open-QSEApp', 'Close-QSEApp', 'Get-QSEAppLayout', 'Get-QSEScript', 'Test-QSEScript',
        'Get-QSEField', 'Get-QSETable', 'Test-QSEExpression', 'Invoke-QSEExpression',
        'Get-QSESheetList', 'Get-QSEBookmarkList', 'Get-QSEDimensionList', 'Get-QSEMeasureList',
        'Get-QSEMasterObjectList', 'Get-QSEFieldList', 'Get-QSEVariableList'
    )
    $commonParameters = @('Verbose','Debug','ErrorAction','WarningAction','InformationAction','ProgressAction','ErrorVariable','WarningVariable','InformationVariable','OutVariable','OutBuffer','PipelineVariable')
    $metadata = @(foreach ($name in $commandNames) {
        $command = Get-Command $name -Module $module.Name -CommandType Cmdlet
        $instance = [Activator]::CreateInstance($command.ImplementingType)
        [pscustomobject]@{
            name = $name
            defaultParameterSet = $command.DefaultParameterSet
            outputs = @($command.OutputType | ForEach-Object Name | Sort-Object)
            parameters = @(foreach ($parameter in $command.Parameters.Values | Where-Object Name -NotIn $commonParameters | Sort-Object Name) {
                $property = $command.ImplementingType.GetProperty($parameter.Name)
                $default = if ($property) { $property.GetValue($instance, $null) } else { $null }
                [pscustomobject]@{
                    name = $parameter.Name; type = $parameter.ParameterType.FullName
                    aliases = @($parameter.Aliases | Sort-Object)
                    default = if ($null -eq $default) { $null } else { [string]$default }
                    sets = @(foreach ($set in $parameter.ParameterSets.GetEnumerator() | Sort-Object Key) {
                        [pscustomobject]@{ name = $set.Key; mandatory = $set.Value.IsMandatory; position = $set.Value.Position; pipeline = $set.Value.ValueFromPipeline; propertyPipeline = $set.Value.ValueFromPipelineByPropertyName }
                    })
                }
            })
        }
    })
    Test-Case 'All 16 commands expose the shared parameter and pipeline contract' {
        Assert-Equal 16 $metadata.Count 'Unexpected command count.'
        $open = Get-Command Open-QSEApp -Module $module.Name
        Assert-True ($open.Parameters.Connection.Aliases -contains 'QlikClient') 'Open must retain the QlikClient alias.'
        Assert-Equal 'System.Management.Automation.SwitchParameter' $open.Parameters.NoData.ParameterType.FullName 'NoData must be a switch.'
        foreach ($name in $commandNames | Where-Object { $_ -ne 'Open-QSEApp' }) {
            $command = Get-Command $name -Module $module.Name
            $appParameter = $command.Parameters.App.ParameterSets['__AllParameterSets']
            Assert-True ($appParameter.IsMandatory -and $appParameter.ValueFromPipeline) "$name must bind a required pipeline App."
            Assert-True (-not $command.Parameters.ContainsKey('Connection')) "$name must use its app owner, not another default connection."
        }
        Assert-Equal 0 @(Get-LoadedSdk).Count 'Metadata discovery loaded the Engine SDK.'
    }
    if ($MissingSdk) {
        Test-Case 'Missing Engine payload fails only on first Engine use' {
            $connection = New-FixtureConnection
            Assert-Equal 0 @(Get-LoadedSdk).Count 'Creating a connection loaded the absent SDK.'
            Assert-Throws { Open-QSEApp -AppId 'missing-payload' -Connection $connection -TimeoutSeconds 5 } 'Missing SDK unexpectedly allowed an Engine open.'
            Assert-True ($null -ne (Get-Command Open-QSEApp -Module $module.Name)) 'Failure removed the module commands.'
        }
    } else {
        Test-Case 'The identical script executes every inspection command with native SDK objects' {
            $connection = New-FixtureConnection
            Assert-Equal 0 @(Get-LoadedSdk).Count 'Creating a REST connection loaded Engine assemblies.'
            $scriptResult = & (Join-Path $PSScriptRoot 'Common-EngineScript.ps1') -Connection $connection
            Assert-Equal $scriptResult.Script $scriptResult.DirectScript 'Wrapper and native reads differ.'
            Assert-True ($scriptResult.Script -is [string]) 'Get-QSEScript must emit one string.'
            Assert-Equal '42' $scriptResult.Expression.Text 'Expression text lost.'
            Assert-Equal 42 $scriptResult.Expression.Number 'Expression numeric value lost.'
            Assert-True $scriptResult.Expression.IsNumeric 'Expression numeric flag lost.'
            Assert-True (-not $scriptResult.Syntax[0].IsValid) 'Syntax diagnostics were lost.'
            Assert-Equal 3 $scriptResult.NamedField.Cardinality 'Named field cardinality differs.'
            Assert-Equal 1 $scriptResult.Tables.Count 'Table enumeration differs.'
            foreach ($kind in $scriptResult.Lists.Keys) {
                Assert-Equal 1 $scriptResult.Lists[$kind].Count "$kind list was not enumerated."
                Assert-True (-not ($scriptResult.Lists[$kind][0] -is [IDisposable])) "$kind entry is a live SDK object."
            }
            $sessions = @((Get-FixtureState).sessions | Where-Object appId -EQ 'portable-app')
            Wait-Until { @((Get-FixtureState).sessions | Where-Object { $_.appId -eq 'portable-app' -and $_.open }).Count -eq 0 } 'Script finally did not close its app socket.'
        }
        Test-Case 'Default changes preserve owners; repeated opens isolate sessions; NoData is opt-in' {
            $first = New-FixtureConnection
            $second = New-FixtureConnection
            Set-FixtureDefault $first
            $appA = Open-QSEApp -AppId 'isolation-app'
            $appB = $null
            $noData = $null
            try {
                Set-FixtureDefault $second
                $appB = Open-QSEApp -AppId 'isolation-app'
                $noData = Open-QSEApp -AppId 'nodata-app' -NoData -QlikClient $first
                Assert-True (-not [object]::ReferenceEquals($appA.Session, $appB.Session)) 'Two opens shared the same SDK session.'
                Assert-True ((Get-QSEScript -App $appA).Contains('isolation-app')) 'Changing default retargeted an existing app.'
                $state = Get-FixtureState
                $isolated = @($state.sessions | Where-Object { $_.appId -eq 'isolation-app' -and $_.open })
                Assert-Equal 2 $isolated.Count 'Two independent sessions did not reach the server.'
                Assert-Equal 2 @($isolated | ForEach-Object { $_.path + '|' + $_.sessionHeader } | Select-Object -Unique).Count 'Two opens reused the same server session selector.'
                Assert-Equal 0 @($isolated | Where-Object noData).Count 'NoData should default to false.'
                Assert-True (@($state.sessions | Where-Object { $_.appId -eq 'nodata-app' -and $_.noData -and $_.open }).Count -eq 1) 'NoData was not sent to the Engine.'
                Disconnect-Fixture $first
                Assert-Throws { Get-QSEScript -App $appA } 'Disconnect left the original app usable.'
                Assert-True ((Get-QSEScript -App $appB).Contains('isolation-app')) 'Disconnect closed another connection app.'
            } finally {
                foreach ($opened in @($appA, $appB, $noData) | Where-Object { $null -ne $_ }) { Close-QSEApp -App $opened }
            }
        }
        Test-Case 'Snapshots destroy temporary lists; native lists survive until disposal or close' {
            $connection = New-FixtureConnection
            $app = Open-QSEApp -AppId 'lists-app' -Connection $connection
            try {
                foreach ($kind in @('Sheet','Bookmark','Dimension','Measure','MasterObject','Field','Variable')) {
                    $command = 'Get-QSE' + $kind + 'List'
                    $entries = @(& $command -App $app)
                    Assert-Equal 1 $entries.Count "$kind snapshot count differs."
                    Assert-Equal 0 (@((Get-FixtureState).sessions | Where-Object { $_.appId -eq 'lists-app' -and $_.open })[0].objects) "$kind temporary object leaked."
                    $native = @(& $command -App $app -AsSdkObject)
                    Assert-Equal 1 $native.Count "$kind native list was enumerated."
                    Assert-True ($native[0] -is [IDisposable]) "$kind is not a native disposable SDK list."
                    $null = $native[0].GetLayout()
                    $native[0].Dispose()
                }
                $retained = Get-QSESheetList -App $app -AsSdkObject
                $snapshot = @(Get-QSEBookmarkList -App $app)
                Close-QSEApp -App $app
                Close-QSEApp -App $app
                Assert-True ($snapshot[0].Id -like 'fixture-*') 'Detached snapshot did not survive close.'
                Assert-Throws { $retained.GetLayout() } 'A retained child remained usable after app close.'
                Wait-Until { @((Get-FixtureState).sessions | Where-Object { $_.appId -eq 'lists-app' -and $_.open }).Count -eq 0 } 'App close leaked its socket.'
            } finally { Close-QSEApp -App $app }
        }
        Test-Case 'Direct native disposal reconciles with repeated Close and stale app reads fail' {
            $app = Open-QSEApp -AppId 'direct-dispose' -Connection (New-FixtureConnection)
            $app.Dispose()
            Assert-Throws { Get-QSEScript -App $app } 'Directly disposed app remained usable.'
            Close-QSEApp -App $app
            Close-QSEApp -App $app
        }
        Test-Case 'Engine errors terminate; a failed open does not poison the owning connection' {
            $connection = New-FixtureConnection
            Assert-Throws { Open-QSEApp -AppId 'missing-app' -Connection $connection } 'Missing app did not fail.'
            Wait-Until { @((Get-FixtureState).sessions | Where-Object { $_.appId -eq 'missing-app' -and $_.open }).Count -eq 0 } 'A failed Open leaked its partial transport.'
            $app = Open-QSEApp -AppId 'error-app' -Connection $connection
            try {
                $engineError = $null
                try { Invoke-QSEExpression -App $app -Expression 'FixtureError' } catch { $engineError = $_ }
                Assert-True ($null -ne $engineError) 'Engine JSON-RPC error was swallowed.'
                Assert-True ($engineError.Exception.Message -match '7001') 'Safe Engine error code was discarded.'
                Assert-True ($engineError.Exception.Message -notmatch 'Fixture evaluation failed|FixtureError') 'Raw server/payload text leaked into the error.'
                Assert-True (-not (Test-QSEExpression -App $app -Expression 'bad(').IsValid) 'Invalid expression was reported valid.'
                Assert-True ((Get-QSEScript -App $app).Contains('error-app')) 'A read error poisoned the session.'
            } finally { Close-QSEApp -App $app }
        }
        Test-Case 'Timeout closes only the affected session and rejects subsequent reads' {
            $connection = New-FixtureConnection
            $slow = Open-QSEApp -AppId 'slow-app' -Connection $connection
            $healthy = Open-QSEApp -AppId 'healthy-app' -Connection $connection
            try {
                $watch = [Diagnostics.Stopwatch]::StartNew()
                Assert-Throws { Get-QSEScript -App $slow -TimeoutSeconds 1 } 'Slow call did not time out.'
                Assert-True ($watch.Elapsed.TotalSeconds -lt 6) 'Timeout did not return promptly.'
                Assert-Throws { Get-QSEScript -App $slow } 'Timed-out session remained usable.'
                Assert-True ((Get-QSEScript -App $healthy).Contains('healthy-app')) 'Timeout closed another app session.'
            } finally { Close-QSEApp -App $slow; Close-QSEApp -App $healthy }
        }
        Test-Case 'StopProcessing cancels an in-flight SDK read without waiting for its response' {
            $shell = [Management.Automation.PowerShell]::Create()
            $runspace = [Management.Automation.Runspaces.RunspaceFactory]::CreateRunspace()
            $runspace.Open()
            $shell.Runspace = $runspace
            try {
                $null = $shell.AddScript(@'
param($modulePath, $url, $moduleName)
$ErrorActionPreference = 'Stop'
Import-Module $modulePath
if ($moduleName -eq 'QlikCloudCLI') {
    $connection = Connect-QlikCloud -Tenant $url -APIKey 'fixture-not-a-real-key' -PassThru
} else {
    $assembly = (Get-Command Open-QSEApp -Module $moduleName).ImplementingType.Assembly
    $connection = [Activator]::CreateInstance($assembly.GetType('QlikSenseCLI.QlikClient'))
    $connection.GetType().GetField('uRLQES', [Reflection.BindingFlags]'Instance,NonPublic').SetValue($connection, $url)
}
$app = Open-QSEApp -AppId 'cancel-app' -Connection $connection
try { Get-QSEScript -App $app -TimeoutSeconds 30 }
finally { $connection.Dispose() }
'@).AddArgument($ModulePath).AddArgument($FixtureUrl).AddArgument($module.Name)
                $invoke = $shell.BeginInvoke()
                Wait-Until {
                    $state = Get-FixtureState
                    $ids = @($state.sessions | Where-Object { $_.appId -eq 'cancel-app' -and $_.open } | ForEach-Object id)
                    @($state.requests | Where-Object { $_.method -eq 'GetScript' -and $_.fixtureSession -in $ids }).Count -gt 0
                } 'Cancellation fixture never started the slow read.'
                $stop = $shell.BeginStop($null, $null)
                Assert-True $stop.AsyncWaitHandle.WaitOne(5000) 'StopProcessing waited for the 15-second fixture response.'
                $shell.EndStop($stop)
                Assert-True $invoke.AsyncWaitHandle.WaitOne(1000) 'Stopped pipeline remained active.'
                try { $null = $shell.EndInvoke($invoke) } catch [Management.Automation.PipelineStoppedException] { }
            } finally { $shell.Dispose(); $runspace.Dispose() }
        }
        Test-Case 'Timed-out Open closes its pending transport and the connection can open another app' {
            $connection = New-FixtureConnection
            $watch = [Diagnostics.Stopwatch]::StartNew()
            Assert-Throws { Open-QSEApp -AppId 'slow-open' -Connection $connection -TimeoutSeconds 1 } 'Pending open did not time out.'
            Assert-True ($watch.Elapsed.TotalSeconds -lt 6) 'Pending open timeout was not prompt.'
            Wait-Until { @((Get-FixtureState).sessions | Where-Object { $_.appId -eq 'slow-open' -and $_.open }).Count -eq 0 } 'Pending open transport leaked after timeout.'
            $app = Open-QSEApp -AppId 'after-open-timeout' -Connection $connection
            try { Assert-True ((Get-QSEScript -App $app).Contains('after-open-timeout')) 'Open timeout poisoned the connection.' }
            finally { Close-QSEApp -App $app }
        }
        Test-Case 'Runspace removal closes apps owned by an explicit connection' {
            $shell = [Management.Automation.PowerShell]::Create()
            $runspace = [Management.Automation.Runspaces.RunspaceFactory]::CreateRunspace()
            $runspace.Open()
            $shell.Runspace = $runspace
            try {
                $null = $shell.AddScript(@'
param($modulePath, $url, $moduleName)
Import-Module $modulePath
if ($moduleName -eq 'QlikCloudCLI') {
    $connection = Connect-QlikCloud -Tenant $url -APIKey 'fixture-not-a-real-key' -PassThru
} else {
    $assembly = (Get-Command Open-QSEApp -Module $moduleName).ImplementingType.Assembly
    $connection = [Activator]::CreateInstance($assembly.GetType('QlikSenseCLI.QlikClient'))
    $connection.GetType().GetField('uRLQES', [Reflection.BindingFlags]'Instance,NonPublic').SetValue($connection, $url)
}
Open-QSEApp -AppId 'runspace-cleanup' -Connection $connection
'@).AddArgument($ModulePath).AddArgument($FixtureUrl).AddArgument($module.Name)
                $opened = $shell.Invoke()
                Assert-Equal 1 $opened.Count 'Second runspace did not return its native app.'
                Assert-Throws { Get-QSEScript -App $opened[0] } 'A native app crossed runspace ownership.'
                $runspace.Close()
                Wait-Until { @((Get-FixtureState).sessions | Where-Object { $_.appId -eq 'runspace-cleanup' -and $_.open }).Count -eq 0 } 'Closing a runspace leaked an explicit connection app.'
            } finally { $shell.Dispose(); $runspace.Dispose() }
        }

        $references = @($moduleAssembly.Location, [System.Management.Automation.PSCmdlet].Assembly.Location)
        if ($PSVersionTable.PSEdition -eq 'Desktop') {
            $references += @([Linq.Enumerable].Assembly.Location, [Collections.Concurrent.ConcurrentQueue[object]].Assembly.Location)
            $runtime = [Runtime.InteropServices.RuntimeEnvironment]::GetRuntimeDirectory()
            $references += @((Join-Path $runtime 'netstandard.dll'), (Join-Path $runtime 'Facades/netstandard.dll')) | Where-Object { Test-Path -LiteralPath $_ }
        } else { $references += @(Get-ChildItem -LiteralPath (Join-Path $PSHOME 'ref') -Filter '*.dll' | ForEach-Object FullName) }
        Add-Type -Path (Join-Path $PSScriptRoot 'RegistryFixture.cs') -ReferencedAssemblies @($references | Sort-Object -Unique)
        $registry = $moduleAssembly.GetType('Qlik.Engine.PowerShell.EngineSessionRegistry', $true)
        $runspaceId = [Management.Automation.Runspaces.Runspace]::DefaultRunspace.InstanceId
        Test-Case 'Ownership rejects unknown objects, wrong runspaces and reference-equal impostors' {
            $owner = [object]::new()
            $provider = [Qlik.Engine.Tests.FixtureProvider]::new()
            $app = [Qlik.Engine.Tests.FixtureApp]::new()
            $impostor = [Qlik.Engine.Tests.FixtureApp]::new()
            $registry::Register($app, $owner, $runspaceId, $provider)
            try {
                Assert-Throws { Get-QSEScript -App $impostor } 'Registry accepted an Equals-equal impostor.'
                $request = [Qlik.Engine.PowerShell.EngineReadRequest]::new()
                Assert-Throws { $registry::ReadAsync($app, [guid]::NewGuid(), $request).GetAwaiter().GetResult() } 'Registry accepted a different runspace.'
                Assert-Equal 'fixture' (Get-QSEScript -App $app) 'Registered provider read failed.'
                $app.Session = [object]::new()
                Assert-Throws { Get-QSEScript -App $app } 'Changed session was silently adopted.'
            } finally { Close-QSEApp -App $app }
        }
        Test-Case 'Registry serializes one app but permits independent sessions concurrently' {
            $owner = [object]::new()
            $provider = [Qlik.Engine.Tests.FixtureProvider]::new()
            $provider.HoldReads = $true
            $app = [Qlik.Engine.Tests.FixtureApp]::new()
            $second = [Qlik.Engine.Tests.FixtureApp]::new()
            $registry::Register($app, $owner, $runspaceId, $provider)
            $registry::Register($second, $owner, $runspaceId, $provider)
            $request = [Qlik.Engine.PowerShell.EngineReadRequest]::new()
            try {
                $one = $registry::ReadAsync($app, $runspaceId, $request)
                $two = $registry::ReadAsync($app, $runspaceId, $request)
                Assert-Equal 1 $provider.Active 'Reads on one app ran concurrently.'
                $three = $registry::ReadAsync($second, $runspaceId, $request)
                Assert-Equal 2 $provider.Active 'An independent session was globally serialized.'
                1..3 | ForEach-Object { $provider.ReleaseRead() }
                foreach ($task in @($one, $two, $three)) { Assert-True $task.Wait(3000) 'Read queue failed to drain.' }
            } finally { $provider.ReleaseRead(); $provider.ReleaseRead(); $provider.ReleaseRead(); Close-QSEApp -App $app; Close-QSEApp -App $second }
        }
        Test-Case 'Close cleans tracked resources once and disconnect rejects a late open' {
            $owner = [object]::new()
            $provider = [Qlik.Engine.Tests.FixtureProvider]::new()
            $app = [Qlik.Engine.Tests.FixtureApp]::new()
            $resource = [Qlik.Engine.Tests.FixtureResource]::new()
            $registry::Register($app, $owner, $runspaceId, $provider)
            $registry::AttachResource($app, $resource)
            $native = Get-QSESheetList -App $app -AsSdkObject
            Close-QSEApp -App $app
            Close-QSEApp -App $app
            Assert-Equal 1 $provider.Disposals 'App disposal was not idempotent.'
            Assert-Equal 1 $resource.Disposals 'Attached location was not disposed exactly once.'
            Assert-Equal 1 $native.Disposals 'Retained child was not disposed exactly once.'
            $registry::CloseConnection($owner)
            Assert-Throws { $registry::Register([Qlik.Engine.Tests.FixtureApp]::new(), $owner, $runspaceId, $provider) } 'A completed late open attached to a disconnected owner.'
        }
        Test-Case 'A native child arriving after close is discarded and disposed' {
            $owner = [object]::new()
            $provider = [Qlik.Engine.Tests.FixtureProvider]::new()
            $provider.HoldReads = $true
            $app = [Qlik.Engine.Tests.FixtureApp]::new()
            $registry::Register($app, $owner, $runspaceId, $provider)
            $request = [Qlik.Engine.PowerShell.EngineReadRequest]::new()
            $request.AsSdkObject = $true
            $pending = $registry::ReadAsync($app, $runspaceId, $request)
            Close-QSEApp -App $app
            $provider.ReleaseRead()
            Assert-Throws { $pending.GetAwaiter().GetResult() } 'A late child escaped its closed owner.'
            Wait-Until { $provider.DestroyedChildren -eq 1 } 'Late SDK child was not disposed.'
        }
    }
} catch {
    $results.Add([pscustomobject]@{ name = 'Harness setup'; passed = $false; milliseconds = 0; error = $_.ToString(); position = $_.InvocationInfo.PositionMessage; stack = $_.ScriptStackTrace })
    Write-Host "FAIL Harness setup: $($_.Exception.Message)"
} finally {
    foreach ($connection in $connections) { try { $connection.Dispose() } catch { } }
    $report = [pscustomobject]@{
        module = [IO.Path]::GetFileNameWithoutExtension($ModulePath); edition = $PSVersionTable.PSEdition
        version = [string]$PSVersionTable.PSVersion; missingSdk = [bool]$MissingSdk
        metadata = $metadata; tests = @($results)
        sdkAssemblies = @(Get-LoadedSdk | ForEach-Object { [pscustomobject]@{ name = $_.GetName().Name; version = [string]$_.GetName().Version; path = $_.Location } })
    }
    $report | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $OutputPath -Encoding UTF8
}
if (@($results | Where-Object { -not $_.passed }).Count) { exit 1 }
