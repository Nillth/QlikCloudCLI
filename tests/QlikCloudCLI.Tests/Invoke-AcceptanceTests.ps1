#requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ModulePath,
    [string]$ClientManifestPath,
    [string]$OutputPath,
    [string]$HostPath,
    [switch]$Worker
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$ModulePath = (Resolve-Path -LiteralPath $ModulePath).Path
if ([string]::IsNullOrWhiteSpace($OutputPath)) { $OutputPath = Join-Path $env:LOCALAPPDATA 'QlikCloudBuild/cli-acceptance/results.json' }
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
if (-not $Worker) {
    if ([string]::IsNullOrWhiteSpace($HostPath)) {
        $hostName = if ($PSVersionTable.PSEdition -eq 'Desktop') { 'powershell.exe' } else { 'pwsh.exe' }
        $HostPath = Join-Path $PSHOME $hostName
    }
    $hostCommand = (Get-Command -Name $HostPath -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
    $childArguments = @('-NoLogo', '-NoProfile', '-NonInteractive', '-File', $PSCommandPath, '-ModulePath', $ModulePath, '-OutputPath', $OutputPath, '-Worker')
    if (-not [string]::IsNullOrWhiteSpace($ClientManifestPath)) { $childArguments += @('-ClientManifestPath', $ClientManifestPath) }
    & $hostCommand @childArguments
    exit $LASTEXITCODE
}

$module = Import-Module -Name $ModulePath -Force -PassThru
$moduleDirectory = Split-Path -Parent $ModulePath
if ([string]::IsNullOrWhiteSpace($ClientManifestPath)) { $ClientManifestPath = Join-Path $moduleDirectory 'QlikCloud.operations.json' }
$inventoryPath = Join-Path $moduleDirectory 'commands.json'
if (-not (Test-Path -LiteralPath $inventoryPath)) { throw "The packaged module must include its commands.json inventory: $inventoryPath" }
$inventory = Get-Content -LiteralPath $inventoryPath -Raw | ConvertFrom-Json
$operations = @($inventory.operations)
$constructors = @($inventory.constructors)
$sdkAssembly = [QlikCloud.RestClientConfig].Assembly
$referenceAssemblies = @([QlikCloud.RestClientConfig].Assembly.Location, [Microsoft.Extensions.Logging.ILogger].Assembly.Location)
if ($PSVersionTable.PSEdition -eq 'Desktop') {
    # Windows PowerShell uses the .NET Framework C# compiler, not the Core ref directory.
    # MockTransport.cs intentionally stays compatible with that compiler's C# 5 syntax.
    $referenceAssemblies += @(
        [Net.Http.HttpClient].Assembly.Location,
        [Uri].Assembly.Location,
        [Linq.Enumerable].Assembly.Location,
        [Collections.Concurrent.ConcurrentQueue[object]].Assembly.Location
    )
    $frameworkDirectory = [Runtime.InteropServices.RuntimeEnvironment]::GetRuntimeDirectory()
    $netstandardCandidates = @(
        (Join-Path $frameworkDirectory 'netstandard.dll'),
        (Join-Path $frameworkDirectory 'Facades/netstandard.dll')
    )
    $netstandardReference = $netstandardCandidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
    if (-not $netstandardReference) { throw 'Windows PowerShell acceptance tests require the .NET Framework netstandard.dll facade (install .NET Framework 4.8).' }
    $referenceAssemblies += $netstandardReference
}
else {
    $referenceAssemblies += @(Get-ChildItem -LiteralPath (Join-Path $PSHOME 'ref') -Filter '*.dll' | ForEach-Object FullName)
}
$referenceAssemblies = @($referenceAssemblies | Sort-Object -Unique)
Add-Type -Path (Join-Path $PSScriptRoot 'MockTransport.cs') -ReferencedAssemblies $referenceAssemblies

$results = [Collections.Generic.List[object]]::new()
$fixtures = [Collections.Generic.List[object]]::new()
function Assert-True([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Assert-Equal($Expected, $Actual, [string]$Message) {
    if ($Expected -cne $Actual) { throw "$Message Expected: <$Expected>; actual: <$Actual>." }
}
function Format-CatalogType([type]$Type) {
    if ($Type.IsArray) { return (Format-CatalogType $Type.GetElementType()) + '[]' }
    if ($Type.IsGenericType) {
        $definition = $Type.GetGenericTypeDefinition().FullName
        $arguments = @($Type.GetGenericArguments() | ForEach-Object { Format-CatalogType $_ })
        if ($definition -eq 'System.Nullable`1') { return $arguments[0] + '?' }
        return 'global::' + $definition.Substring(0, $definition.IndexOf('`')).Replace('+', '.') + '<' + ($arguments -join ', ') + '>'
    }
    return 'global::' + $Type.FullName.Replace('+', '.')
}
function Test-Case([string]$Name, [scriptblock]$Body) {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    try {
        & $Body | Out-Null
        $results.Add([pscustomobject]@{ name = $Name; passed = $true; milliseconds = $timer.ElapsedMilliseconds; error = $null })
        Write-Host "PASS $Name"
    } catch {
        $results.Add([pscustomobject]@{ name = $Name; passed = $false; milliseconds = $timer.ElapsedMilliseconds; error = $_.ToString(); position = $_.InvocationInfo.PositionMessage; stack = $_.ScriptStackTrace })
        Write-Host "FAIL $Name`: $($_.Exception.Message)"
    }
}
function Find-Operation([string]$Api, [string]$Method, [string]$Path) {
    $matches = @($operations | Where-Object { $_.api -eq $Api -and $_.httpMethod -eq $Method -and $_.path -eq $Path })
    if ($matches.Count -ne 1) { throw "Expected one operation for $Api $Method $Path; found $($matches.Count)." }
    return $matches[0]
}
function Invoke-Endpoint($Operation, [hashtable]$Parameters) {
    $command = Get-Command -Name $Operation.commandName -Module $module.Name
    if ($command.Parameters.ContainsKey('Confirm')) { $Parameters['Confirm'] = $false }
    & $command @Parameters
}
function New-Fixture {
    $handler = [QlikCloud.AcceptanceFixtures.MockTransport]::new()
    $http = [Net.Http.HttpClient]::new($handler)
    $http.BaseAddress = [uri]'https://qlikcloud-acceptance.invalid/api/'
    $http.Timeout = [TimeSpan]::FromSeconds(15)
    $client = [QlikCloud.AcceptanceFixtures.FixtureClient]::new($http)
    $connection = [QlikCloud.Common.Connection]::new($client)
    $fixture = [pscustomobject]@{ Handler = $handler; Http = $http; Client = $client; Connection = $connection }
    $fixtures.Add($fixture)
    return $fixture
}
function New-UserJson([string]$Id) { return @{ id = $Id; name = "User $Id"; subject = "subject-$Id"; tenantId = 'fixture-tenant' } | ConvertTo-Json -Compress }
function New-CollectionJson([string]$Id) {
    return @{
        id = $Id; name = "Collection $Id"; type = 'private'; links = @{}; tenantId = 'fixture-tenant'
        createdAt = '2026-01-01T00:00:00Z'; updatedAt = '2026-01-01T00:00:00Z'; itemCount = 0
    } | ConvertTo-Json -Depth 5 -Compress
}
function New-CollectionItemJson([string]$Id) {
    return @{
        id = $Id; name = "Item $Id"; links = @{}; tenantId = 'fixture-tenant'; actions = @()
        meta = @{ tags = @(); actions = @(); collections = @(); isFavorited = $false }
        createdAt = '2026-01-01T00:00:00Z'; updatedAt = '2026-01-01T00:00:00Z'
        isFavorited = $false; resourceType = 'app'; collectionIds = @('parent')
        resourceCreatedAt = '2026-01-01T00:00:00Z'; resourceUpdatedAt = '2026-01-01T00:00:00Z'; resourceAttributes = @{}
    } | ConvertTo-Json -Depth 5 -Compress
}
function Clear-DefaultConnection {
    $command = Get-Command Disconnect-QlikCloud -Module $module.Name
    $parameters = @{}
    if ($command.Parameters.ContainsKey('Confirm')) { $parameters.Confirm = $false }
    & $command @parameters | Out-Null
}

$listUsers = Find-Operation users get '/api/v1/users'
$getUser = Find-Operation users get '/api/v1/users/{userId}'
$createUser = Find-Operation users post '/api/v1/users'
$deleteUser = Find-Operation users delete '/api/v1/users/{userId}'
$upload = Find-Operation apps put '/api/v1/apps/{appId}/media/files/{path}'
$download = Find-Operation apps get '/api/v1/apps/{appId}/media/files/{path}'
$listCollections = Find-Operation collections get '/api/v1/collections'
$getCollection = Find-Operation collections get '/api/v1/collections/{collectionId}'
$listCollectionItems = Find-Operation collections get '/api/v1/collections/{collectionId}/items'
$getCollectionItem = Find-Operation collections get '/api/v1/collections/{collectionId}/items/{itemId}'
$listAudits = Find-Operation audits get '/api/v1/audits'
$getAudit = Find-Operation audits get '/api/v1/audits/{id}'
$userConstructor = 'New-QCUsersUserPostSchemaObject'

Test-Case 'Package targets .NET Standard 2.0 and both Windows PowerShell 5.1 and Core' {
    $manifest = Import-PowerShellDataFile -LiteralPath $ModulePath
    Assert-Equal '5.1' ([string]$manifest.PowerShellVersion) 'Module manifest has an unexpected minimum PowerShell version.'
    Assert-Equal 'Core,Desktop' (@($manifest.CompatiblePSEditions | Sort-Object) -join ',') 'Module must declare both Desktop and Core compatibility.'
    $cliAssembly = (Get-Command Connect-QlikCloud -Module $module.Name).ImplementingType.Assembly
    foreach ($assembly in @($sdkAssembly, $cliAssembly)) {
        $attributes = @($assembly.GetCustomAttributes([Runtime.Versioning.TargetFrameworkAttribute], $false))
        Assert-Equal 1 $attributes.Count "$($assembly.GetName().Name) must declare exactly one target framework."
        Assert-Equal '.NETStandard,Version=v2.0' $attributes[0].FrameworkName "$($assembly.GetName().Name) targets an incompatible framework."
    }
}

Test-Case 'Packaged inventory covers every exported endpoint and constructor parameter' {
    Assert-Equal $inventory.assemblySha256 ((Get-FileHash -LiteralPath $sdkAssembly.Location -Algorithm SHA256).Hash.ToLowerInvariant()) 'Packaged SDK differs from the command inventory input.'
    Assert-Equal $inventory.clientManifestSha256 ((Get-FileHash -LiteralPath $ClientManifestPath -Algorithm SHA256).Hash.ToLowerInvariant()) 'Packaged API catalog differs from the command inventory input.'
    Assert-True ($operations.Count -gt 0) 'No API operations were generated.'
    Assert-True ($constructors.Count -gt 0) 'No model constructors were generated.'
    Assert-Equal 2 $inventory.formatVersion 'Grouped routes must use catalog format version 2.'
    $endpointNames = @($operations.commandName | Sort-Object -Unique)
    $names = $endpointNames + @($constructors.commandName)
    Assert-Equal $names.Count @($names | Sort-Object -Unique).Count 'Generated command names collide.'
    Assert-Equal $endpointNames.Count $inventory.commandCount 'Catalog command count is not the distinct REST command count.'
    $operationSets = @($operations | ForEach-Object { "$($_.commandName)/$($_.parameterSetName)" })
    Assert-Equal $operationSets.Count @($operationSets | Sort-Object -Unique).Count 'Multiple operations claim the same command and parameter set.'
    $commandGroups = @($inventory.commands)
    Assert-Equal ($endpointNames -join "`n") (@($commandGroups.commandName | Sort-Object) -join "`n") 'Catalog command groups differ from its operations.'
    foreach ($name in $names) {
        Assert-True ($name -cmatch '^[A-Za-z]+-QC[A-Za-z0-9_]+$') "Generated command $name does not use the QC noun prefix."
    }
    foreach ($constructor in $constructors) {
        Assert-True ($constructor.commandName -cmatch '^New-QC[A-Za-z0-9_]+Object$') "Constructor $($constructor.commandName) does not preserve the New-QC...Object naming contract."
    }
    $manualNames = @('Connect-QlikCloud', 'Disconnect-QlikCloud', 'Get-QCConnection', 'Receive-QCToken', 'Get-QlikUserAgent', 'Invoke-OpenApp')
    $expectedExports = @($names + $manualNames | Sort-Object)
    $actualExports = @(Get-Command -Module $module.Name | ForEach-Object Name | Sort-Object)
    Assert-Equal ($expectedExports -join "`n") ($actualExports -join "`n") 'Package exports differ from its generated inventory and six supported connection/engine commands.'
    $legacyExports = @($actualExports | Where-Object { $_ -match '^[A-Za-z]+-QlikCloud' })
    Assert-Equal "Connect-QlikCloud`nDisconnect-QlikCloud" ($legacyExports -join "`n") 'Only Connect-QlikCloud and Disconnect-QlikCloud may retain the old prefix; legacy aliases must not be exported.'
    foreach ($group in $commandGroups) {
        $command = Get-Command -Name $group.commandName -Module $module.Name -ErrorAction Stop
        $routes = @($operations | Where-Object commandName -EQ $group.commandName)
        $expectedSets = @($routes.parameterSetName | Sort-Object)
        $actualSets = @($command.ParameterSets)
        Assert-True ($expectedSets.Count -ge 1 -and $expectedSets.Count -le 32) "$($command.Name) exceeds PowerShell's 32-set limit."
        Assert-True ($actualSets.Count -ge 1 -and $actualSets.Count -le 32) "$($command.Name) has an invalid runtime parameter-set count."
        Assert-Equal $routes.Count $group.operationCount "$($command.Name) has an incorrect aggregate operation count."
        Assert-Equal ($expectedSets -join ',') (@($group.parameterSetNames | Sort-Object) -join ',') "$($command.Name) has an incorrect aggregate set list."
        Assert-Equal ($expectedSets -join ',') (@($actualSets.Name | Sort-Object) -join ',') "$($command.Name) runtime parameter sets differ from its routes."
        $defaultSet = if ($routes.Count -eq 1) { 'Default' } else { 'List' }
        Assert-Equal $defaultSet $group.defaultParameterSetName "$($command.Name) has an unexpected default in the catalog."
        Assert-Equal $defaultSet (@($actualSets | Where-Object IsDefault).Name -join ',') "$($command.Name) runtime default differs from its catalog."
    }
    foreach ($operation in $operations) {
        $command = Get-Command -Name $operation.commandName -Module $module.Name -ErrorAction Stop
        Assert-True $command.Parameters.ContainsKey('Connection') "$($command.Name) has no explicit connection parameter."
        Assert-True $command.Parameters.ContainsKey('RawResponse') "$($command.Name) has no raw response parameter."
        $sets = @($command.ParameterSets | Where-Object Name -EQ $operation.parameterSetName)
        Assert-Equal 1 $sets.Count "$($command.Name) omits or duplicates set $($operation.parameterSetName)."
        $set = $sets[0]
        foreach ($commonName in @('Connection', 'RawResponse')) {
            Assert-True (@($set.Parameters.Name) -contains $commonName) "$($command.Name)/$($set.Name) lost common parameter $commonName."
        }
        $generatedParameters = @($set.Parameters | Where-Object {
            $property = $command.ImplementingType.GetProperty($_.Name)
            $null -ne $property -and $property.DeclaringType -eq $command.ImplementingType
        })
        Assert-Equal (@($operation.parameters | ForEach-Object name | Sort-Object) -join ',') (@($generatedParameters | ForEach-Object Name | Sort-Object) -join ',') "$($command.Name)/$($set.Name) exposes parameters belonging to another route."
        foreach ($parameter in @($operation.parameters)) {
            Assert-True $command.Parameters.ContainsKey($parameter.name) "$($command.Name) omits parameter $($parameter.name)."
            $bound = @($set.Parameters | Where-Object Name -EQ $parameter.name)
            Assert-Equal 1 $bound.Count "$($command.Name)/$($set.Name) omits parameter $($parameter.name)."
            $context = "$($command.Name)/$($set.Name).$($parameter.name)"
            Assert-Equal $parameter.type (Format-CatalogType $bound[0].ParameterType) "$context CLR type differs from the catalog."
            Assert-Equal ([bool]$parameter.mandatory) $bound[0].IsMandatory "$context requiredness differs from the catalog."
            Assert-Equal ([bool]$parameter.pipelineByValue) $bound[0].ValueFromPipeline "$context value pipeline binding differs from the catalog."
            Assert-Equal ([bool]$parameter.pipelineByPropertyName) $bound[0].ValueFromPipelineByPropertyName "$context property pipeline binding differs from the catalog."
            Assert-Equal (@($parameter.aliases | Sort-Object) -join ',') (@($bound[0].Aliases | Sort-Object) -join ',') "$context aliases differ from the catalog."
        }
        $outputAttributes = @($command.ImplementingType.GetCustomAttributes([Management.Automation.OutputTypeAttribute], $true) | Where-Object { $_.ParameterSetName -contains $operation.parameterSetName })
        $outputTypes = @($outputAttributes | ForEach-Object { $_.Type | ForEach-Object { Format-CatalogType $_.Type } })
        Assert-True ($outputTypes -contains $operation.outputType) "$($command.Name)/$($set.Name) lost its typed output metadata."
        if ($operation.httpMethod -notin @('get', 'head', 'options')) {
            Assert-True $command.Parameters.ContainsKey('WhatIf') "$($command.Name) has no ShouldProcess protection."
        }
    }
    foreach ($constructor in $constructors) {
        $command = Get-Command -Name $constructor.commandName -Module $module.Name -ErrorAction Stop
        if ($constructor.supportsJson) { Assert-True $command.Parameters.ContainsKey('Json') "$($command.Name) has no JSON input." }
        Assert-True $command.Parameters.ContainsKey('InputObject') "$($command.Name) has no model-copy input."
    }
    $dllNames = @(Get-ChildItem -LiteralPath $moduleDirectory -Recurse -Filter '*.dll' | ForEach-Object Name)
    Assert-True ($dllNames -notcontains 'System.Management.Automation.dll') 'Package includes the PowerShell reference/runtime assembly instead of using the host.'
    Assert-True ($dllNames -notcontains 'PowerShellDI.dll') 'Package still includes the retired external PowerShellDI dependency.'
}

if (-not [string]::IsNullOrWhiteSpace($ClientManifestPath)) {
    Test-Case 'Every client manifest operation has one PowerShell command' {
        $clientManifest = Get-Content -LiteralPath $ClientManifestPath -Raw | ConvertFrom-Json
        $expected = @($clientManifest.apis | Where-Object { $_.PSObject.Properties.Name -contains 'operations' } | ForEach-Object {
            $apiName = $_.api
            foreach ($operation in $_.operations) { "$apiName $($operation.method.ToUpperInvariant()) $($operation.path)" }
        } | Sort-Object)
        $actual = @($operations | ForEach-Object { "$($_.api) $($_.httpMethod.ToUpperInvariant()) $($_.path)" } | Sort-Object)
        Assert-Equal ($expected -join "`n") ($actual -join "`n") 'PowerShell endpoint coverage differs from the client manifest.'
    }
}

Test-Case 'Collections list and item share one command without repurposing the Id filter' {
    Assert-Equal 'Get-QCCollections' $listCollections.commandName 'Collection list did not retain its natural command name.'
    Assert-Equal $listCollections.commandName $getCollection.commandName 'Collection item was not grouped with its list.'
    Assert-Equal 'List' $listCollections.parameterSetName 'Collection list has the wrong parameter set.'
    Assert-Equal 'ByCollectionId' $getCollection.parameterSetName 'Collection item has the wrong parameter set.'
    $fixture = New-Fixture
    $fixture.Handler.QueueJson(200, '{"data":[],"links":{}}')
    $list = Get-QCCollections -Connection $fixture.Connection
    Assert-Equal 'QlikCloud.Collections.CollectionsListCollectionsResponseBody' $list.GetType().FullName 'Default invocation did not return the collection-list model.'
    Assert-Equal '/api/v1/collections' ([uri]$fixture.Handler.Requests[0].Uri).AbsolutePath 'Default set called the wrong route.'
    Assert-Equal '' ([uri]$fixture.Handler.Requests[0].Uri).Query 'Unbound query values were sent.'
    $fixture.Handler.QueueJson(200, '{"data":[],"links":{}}')
    $rawList = Get-QCCollections -Connection $fixture.Connection -Id 'filter-id' -Limit 0 -RawResponse
    Assert-Equal '/api/v1/collections' ([uri]$fixture.Handler.Requests[1].Uri).AbsolutePath 'List Id filter was mistaken for a path identifier.'
    Assert-True ($fixture.Handler.Requests[1].Uri -match '(?:\?|&)id=filter-id(?:&|$)') 'List Id filter was not serialized.'
    Assert-True ($fixture.Handler.Requests[1].Uri -match '(?:\?|&)limit=0(?:&|$)') 'List set lost explicit zero.'
    Assert-True ($rawList -is [QlikCloud.APIResponse]) 'Raw list did not retain its wrapper.'
    Assert-Equal 'QlikCloud.Collections.CollectionsListCollectionsResponseBody' $rawList.Object.GetType().FullName 'Raw list has the wrong payload type.'
    $fixture.Handler.QueueJson(200, (New-CollectionJson 'detail-id'))
    $item = Get-QCCollections -Connection $fixture.Connection -CollectionId 'detail-id'
    Assert-Equal 'QlikCloud.Collections.CollectionResultResponseBody' $item.GetType().FullName 'Item set did not return the collection model.'
    Assert-Equal 'detail-id' $item.Id 'Item set returned the wrong collection.'
    Assert-Equal '/api/v1/collections/detail-id' ([uri]$fixture.Handler.Requests[2].Uri).AbsolutePath 'Item set called the wrong route.'
    $fixture.Handler.QueueJson(200, (New-CollectionJson 'raw-detail'))
    $rawItem = Get-QCCollections -Connection $fixture.Connection -CollectionId 'raw-detail' -RawResponse
    Assert-True ($rawItem -is [QlikCloud.APIResponse]) 'Raw item did not retain its wrapper.'
    Assert-Equal 200 $rawItem.StatusCode 'Raw item lost its HTTP status.'
    Assert-Equal 'QlikCloud.Collections.CollectionResultResponseBody' $rawItem.Object.GetType().FullName 'Raw item has the wrong payload type.'
    Assert-Equal 'raw-detail' $rawItem.Object.Id 'Raw item lost its typed payload.'
    Assert-Equal 4 $fixture.Handler.Count 'Collection invocations did not dispatch exactly once each.'
}

Test-Case 'Grouped collection ID pipelines dispatch each scalar and property record' {
    $fixture = New-Fixture
    foreach ($id in @('scalar-one', 'scalar-two', 'object-one', 'object-two')) { $fixture.Handler.QueueJson(200, (New-CollectionJson $id)) }
    $scalarResults = @(@('scalar-one', 'scalar-two') | Get-QCCollections -Connection $fixture.Connection)
    $objects = @([pscustomobject]@{ CollectionId = 'object-one' }, [pscustomobject]@{ CollectionId = 'object-two' })
    $objectResults = @($objects | Get-QCCollections -Connection $fixture.Connection)
    Assert-Equal 'scalar-one,scalar-two' ($scalarResults.Id -join ',') 'Scalar identifier pipeline lost or reused a record.'
    Assert-Equal 'object-one,object-two' ($objectResults.Id -join ',') 'Property identifier pipeline lost or reused a record.'
    $paths = @($fixture.Handler.Requests | ForEach-Object { ([uri]$_.Uri).AbsolutePath })
    Assert-Equal '/api/v1/collections/scalar-one,/api/v1/collections/scalar-two,/api/v1/collections/object-one,/api/v1/collections/object-two' ($paths -join ',') 'ID pipeline selected a list route or reused an identifier.'
}

Test-Case 'Nested collection items keep the parent required and select item detail independently' {
    Assert-Equal 'Get-QCCollectionsItems' $listCollectionItems.commandName 'Nested item command includes a route placeholder.'
    Assert-Equal $listCollectionItems.commandName $getCollectionItem.commandName 'Nested item list and detail are not grouped.'
    Assert-True ($listCollectionItems.commandName -ne $listCollections.commandName) 'Nested items were collapsed into the parent resource command.'
    Assert-Equal 'List' $listCollectionItems.parameterSetName 'Nested list set changed.'
    Assert-Equal 'ByItemId' $getCollectionItem.parameterSetName 'Nested detail set changed.'
    $fixture = New-Fixture
    $fixture.Handler.QueueJson(200, '{"data":[]}')
    $list = Get-QCCollectionsItems -Connection $fixture.Connection -CollectionId 'parent' -Limit 0 -NoActions $false
    Assert-Equal 'QlikCloud.Collections.CollectionsListCollectionItemsResponseBody' $list.GetType().FullName 'Nested list has the wrong response type.'
    $uri = $fixture.Handler.Requests[0].Uri
    Assert-Equal '/api/v1/collections/parent/items' ([uri]$uri).AbsolutePath 'Nested list called the wrong route.'
    Assert-True ($uri -match '(?:\?|&)limit=0(?:&|$)') 'Nested list lost zero.'
    Assert-True ($uri -match '(?:\?|&)noActions=false(?:&|$)') 'Nested list lost false.'
    $fixture.Handler.QueueJson(200, (New-CollectionItemJson 'child'))
    $item = Get-QCCollectionsItems -Connection $fixture.Connection -CollectionId 'parent' -ItemId 'child' -RawResponse
    Assert-Equal '/api/v1/collections/parent/items/child' ([uri]$fixture.Handler.Requests[1].Uri).AbsolutePath 'Nested item called the wrong route.'
    Assert-Equal 'QlikCloud.Collections.ItemResultResponseBody' $item.Object.GetType().FullName 'Nested detail has the wrong response type.'
    Assert-Equal 'child' $item.Object.Id 'Nested item lost its payload.'
    Assert-Equal $false $item.Object.IsFavorited 'Nested item lost its false property.'
    Assert-Equal 2 $fixture.Handler.Count 'Nested commands dispatched more than once.'
}

Test-Case 'List-only filters and missing nested parents fail binding before HTTP' {
    $fixture = New-Fixture
    $invalidCalls = @(
        @{ Command = 'Get-QCCollections'; Parameters = @{ CollectionId = 'detail'; Limit = 0 } },
        @{ Command = 'Get-QCCollections'; Parameters = @{ CollectionId = 'detail'; Id = 'list-filter' } },
        @{ Command = 'Get-QCCollectionsItems'; Parameters = @{ CollectionId = 'parent'; ItemId = 'child'; NoActions = $false } },
        @{ Command = 'Get-QCCollectionsItems'; Parameters = @{ CollectionId = $null; ItemId = 'child' } },
        @{ Command = 'Get-QCAudits'; Parameters = @{ AuditId = 'detail'; Id = 'list-filter' } }
    )
    foreach ($call in $invalidCalls) {
        $arguments = $call.Parameters
        $arguments.Connection = $fixture.Connection
        $arguments.ErrorAction = 'Stop'
        $failure = $null
        try { & $call.Command @arguments | Out-Null } catch { $failure = $_ }
        Assert-True ($null -ne $failure) "$($call.Command) accepted an invalid parameter-set combination."
        Assert-True ($failure.Exception -is [Management.Automation.ParameterBindingException]) "$($call.Command) failed after binding instead of rejecting the arguments."
        Assert-Equal 0 $fixture.Handler.Count "$($call.Command) issued HTTP for invalid arguments."
    }
}

Test-Case 'Audits retain the Id list filter while AuditId selects the detail route' {
    Assert-Equal 'Get-QCAudits' $listAudits.commandName 'Audit list command changed.'
    Assert-Equal $listAudits.commandName $getAudit.commandName 'Audit detail was not grouped.'
    Assert-Equal 'ByAuditId' $getAudit.parameterSetName 'Audit detail set does not describe its public identifier.'
    Assert-Equal 'AuditId' $getAudit.parameters[0].name 'Audit detail did not rename its public parameter.'
    Assert-Equal 'id' $getAudit.parameters[0].clrName 'Audit public rename changed the SDK parameter mapping.'
    $fixture = New-Fixture
    $fixture.Handler.QueueJson(200, '{"data":[]}')
    $list = Get-QCAudits -Connection $fixture.Connection -Id 'filter-event' -Limit 0
    Assert-Equal 'QlikCloud.Audits.GetResult' $list.GetType().FullName 'Audit list has the wrong response type.'
    Assert-Equal '/api/v1/audits' ([uri]$fixture.Handler.Requests[0].Uri).AbsolutePath 'Audit list filter selected the detail route.'
    Assert-True ($fixture.Handler.Requests[0].Uri -match '(?:\?|&)id=filter-event(?:&|$)') 'Audit Id filter was lost.'
    Assert-True ($fixture.Handler.Requests[0].Uri -match '(?:\?|&)limit=0(?:&|$)') 'Audit list lost zero.'
    $fixture.Handler.QueueJson(200, '{"id":"detail-event"}')
    $detail = Get-QCAudits -Connection $fixture.Connection -AuditId 'detail-event' -RawResponse
    Assert-Equal '/api/v1/audits/detail-event' ([uri]$fixture.Handler.Requests[1].Uri).AbsolutePath 'AuditId did not reach the SDK path argument.'
    Assert-Equal 'QlikCloud.Audits.GetByIDResult' $detail.Object.GetType().FullName 'Audit detail has the wrong response type.'
    Assert-Equal 'detail-event' $detail.Object.Id 'Audit detail lost its typed payload.'
    Assert-Equal 2 $fixture.Handler.Count 'Audit commands dispatched more than once.'
}

if (@($operations | Where-Object api -EQ 'core/ip-policies').Count -gt 0) {
    Test-Case 'Namespaced commands preserve the API route and typed models in both PowerShell hosts' {
        $listPolicies = Find-Operation 'core/ip-policies' 'GET' '/api/core/ip-policies'
        $getPolicy = Find-Operation 'core/ip-policies' 'GET' '/api/core/ip-policies/{id}'
        $removePolicy = Find-Operation 'core/ip-policies' 'DELETE' '/api/core/ip-policies/{id}'
        Assert-Equal 'Get-QCCoreIpPolicies' $listPolicies.commandName 'Namespaced command noun changed.'
        Assert-Equal $listPolicies.commandName $getPolicy.commandName 'Namespaced list/detail routes are not grouped.'
        $fixture = New-Fixture
        $fixture.Handler.QueueJson(200, '{"data":[],"links":{"self":{"href":"https://qlikcloud-acceptance.invalid/api/core/ip-policies"}},"totalResults":0}')
        $result = Invoke-Endpoint $listPolicies @{ Connection = $fixture.Connection; Limit = 1; TotalResults = $false }
        Assert-Equal 'QlikCloud.CoreIpPolicies' $result.GetType().Namespace 'Namespaced response used another API model.'
        Assert-Equal '/api/core/ip-policies' ([uri]$fixture.Handler.Requests[0].Uri).AbsolutePath 'Namespace was lost or a v1 prefix was added.'
        Assert-True ($fixture.Handler.Requests[0].Uri -match '(?:\?|&)totalResults=false(?:&|$)') 'Namespaced request lost explicit false.'
        Invoke-Endpoint $removePolicy @{ Connection = $fixture.Connection; Id = 'fixture-policy'; WhatIf = $true } | Out-Null
        Assert-Equal 1 $fixture.Handler.Count 'Namespaced WhatIf issued an HTTP request.'
    }
}

Test-Case 'Optional query values preserve false and zero and default results are typed' {
    $fixture = New-Fixture
    $fixture.Handler.QueueJson(200, '{"data":[],"totalResults":0}')
    $result = Invoke-Endpoint $listUsers @{ Connection = $fixture.Connection; Limit = 0; TotalResults = $false }
    Assert-Equal 'QlikCloud.Users.Users' $result.GetType().FullName 'Default result was not the SDK model.'
    Assert-Equal 0 $result.TotalResults 'Zero in the response was lost.'
    $uri = $fixture.Handler.Requests[0].Uri
    Assert-True ($uri -match '(?:\?|&)limit=0(?:&|$)') "Zero query argument missing: $uri"
    Assert-True ($uri -match '(?:\?|&)totalResults=false(?:&|$)') "False query argument missing: $uri"
}

Test-Case 'Pipeline path binding uses each input record' {
    $fixture = New-Fixture
    $fixture.Handler.QueueJson(200, (New-UserJson 'first'))
    $fixture.Handler.QueueJson(200, (New-UserJson 'second'))
    $records = @([pscustomobject]@{ UserId = 'first' }, [pscustomobject]@{ UserId = 'second' })
    $output = @($records | & $getUser.commandName -Connection $fixture.Connection)
    Assert-Equal 2 $output.Count 'Pipeline did not return one result per input record.'
    Assert-Equal 'first' $output[0].Id 'First result does not correspond to the first input.'
    Assert-Equal 'second' $output[1].Id 'Second result does not correspond to the second input.'
    Assert-True ($fixture.Handler.Requests[0].Uri -match '/users/first$') 'First path identifier was not bound.'
    Assert-True ($fixture.Handler.Requests[1].Uri -match '/users/second$') 'Second path identifier reused the previous pipeline record.'
}

Test-Case 'Mandatory path parameters reject null before HTTP' {
    $fixture = New-Fixture
    $failure = $null
    try { Invoke-Endpoint $getUser @{ Connection = $fixture.Connection; UserId = $null } | Out-Null } catch { $failure = $_ }
    Assert-True ($null -ne $failure) 'Missing mandatory userId was accepted.'
    Assert-Equal 0 $fixture.Handler.Count 'Invalid arguments issued an HTTP request.'
}

Test-Case 'RawResponse retains status headers raw text and typed object' {
    $fixture = New-Fixture
    $json = New-UserJson 'raw-result'
    $fixture.Handler.QueueJson(200, $json)
    $result = Invoke-Endpoint $getUser @{ Connection = $fixture.Connection; UserId = 'raw-result'; RawResponse = $true }
    Assert-True ($result -is [QlikCloud.APIResponse]) 'RawResponse did not return an APIResponse wrapper.'
    Assert-Equal 200 $result.StatusCode 'Raw response status changed.'
    Assert-Equal $json $result.RawContent 'Raw response body changed.'
    Assert-Equal 'raw-result' $result.Object.Id 'Raw response lost its typed payload.'
    Assert-Equal 'local-mock' (@($result.Headers['X-Qlik-Acceptance'])[0]) 'Raw response lost headers.'
}

Test-Case 'Model Json and InputObject overrides preserve nested bodies without mutating source' {
    $source = & $userConstructor -Json '{"subject":"source-subject","name":"Original","assignedRoles":[{"name":"Fixture role"}]}'
    $copy = & $userConstructor -InputObject $source -Name 'Override'
    Assert-Equal 'Original' $source.Name 'InputObject overrides mutated the source model.'
    Assert-Equal 'Override' $copy.Name 'Explicit model property override was ignored.'
    Assert-Equal 'source-subject' $copy.Subject 'Unchanged source property was lost.'
    $fixture = New-Fixture
    $fixture.Handler.QueueJson(201, (New-UserJson 'created'))
    $result = Invoke-Endpoint $createUser @{ Connection = $fixture.Connection; Body = $copy }
    $body = $fixture.Handler.Requests[0].Text | ConvertFrom-Json
    Assert-Equal 'Override' $body.name 'Request did not serialize the overridden model.'
    Assert-Equal 'Fixture role' $body.assignedRoles[0].name 'Nested model/union data was lost.'
    Assert-Equal 'created' $result.Id 'Create did not return the typed response.'
}

Test-Case 'Model overrides retain explicit false and zero instead of the source values' {
    $invite = New-QCUsersInviteItemObject -Json '{"email":"fixture@example.invalid","resend":true}'
    $override = New-QCUsersInviteItemObject -InputObject $invite -Resend $false
    Assert-Equal $false $override.Resend 'Explicit false did not override true.'
    Assert-Equal $true $invite.Resend 'False override mutated its input object.'
    Assert-True ($override.ToJson() -match '"resend":false') 'False was omitted from JSON serialization.'
    $count = New-QCAppsFilterFieldValueObject -ValueAsNumber 7
    $zero = New-QCAppsFilterFieldValueObject -InputObject $count -ValueAsNumber 0
    Assert-Equal 0 $zero.ValueAsNumber 'Explicit zero did not override the source count.'
    Assert-Equal 7 $count.ValueAsNumber 'Zero override mutated its input object.'
}

Test-Case 'Malformed model JSON produces an actionable local error' {
    $failure = $null
    try { & $userConstructor -Json '{"subject":' -ErrorAction Stop | Out-Null } catch { $failure = $_ }
    Assert-True ($null -ne $failure) 'Malformed constructor JSON was accepted.'
    Assert-True ($failure.FullyQualifiedErrorId -like 'QlikCloudInvalidModelJson*') 'Malformed constructor JSON has no stable validation error.'
}

Test-Case 'Model constructors accept typed pipeline input once per record' {
    $first = & $userConstructor -Subject 'one' -Name 'First'
    $second = & $userConstructor -Subject 'two' -Name 'Second'
    $models = @(@($first, $second) | & $userConstructor)
    Assert-Equal 2 $models.Count 'Constructor pipeline dropped a record.'
    Assert-Equal 'one' $models[0].Subject 'First constructor record changed.'
    Assert-Equal 'two' $models[1].Subject 'Second constructor record reused the first.'
}

Test-Case 'Mutation body pipeline sends each typed model once' {
    $fixture = New-Fixture
    $fixture.Handler.QueueJson(201, (New-UserJson 'first'))
    $fixture.Handler.QueueJson(201, (New-UserJson 'second'))
    $first = & $userConstructor -Subject 'first-subject' -Name 'First'
    $second = & $userConstructor -Subject 'second-subject'
    $output = @(@($first, $second) | & $createUser.commandName -Connection $fixture.Connection -Confirm:$false)
    Assert-Equal 2 $output.Count 'Mutation pipeline did not return one response per model.'
    $firstBody = $fixture.Handler.Requests[0].Text | ConvertFrom-Json
    $secondBody = $fixture.Handler.Requests[1].Text | ConvertFrom-Json
    Assert-Equal 'first-subject' $firstBody.subject 'First request body changed.'
    Assert-Equal 'second-subject' $secondBody.subject 'Second request body reused the first.'
    Assert-True ($secondBody.PSObject.Properties.Name -notcontains 'name') 'An unset body property leaked from the preceding record.'
}

Test-Case 'Mutating WhatIf requires no connection and issues no request' {
    Clear-DefaultConnection
    $fixture = New-Fixture
    Invoke-Endpoint $deleteUser @{ Connection = $fixture.Connection; UserId = 'do-not-delete'; WhatIf = $true } | Out-Null
    Invoke-Endpoint $deleteUser @{ UserId = 'no-connection-needed'; WhatIf = $true } | Out-Null
    Assert-Equal 0 $fixture.Handler.Count 'WhatIf issued HTTP.'
}

Test-Case 'HTTP errors expose structured status without leaking response payloads' {
    $fixture = New-Fixture
    $fixture.Handler.QueueJson(500, '{"message":"fixture-server-error"}')
    $failure = $null
    try { Invoke-Endpoint $getUser @{ Connection = $fixture.Connection; UserId = 'broken'; ErrorAction = 'Stop' } | Out-Null } catch { $failure = $_ }
    Assert-True ($null -ne $failure) 'An HTTP failure was silently accepted.'
    Assert-Equal 500 $failure.Exception.StatusCode 'HTTP status is missing from the PowerShell error.'
    Assert-True ($failure.FullyQualifiedErrorId -like 'QlikCloudHttpError*') 'HTTP failure has no stable PowerShell error ID.'
    Assert-True ($failure.ToString() -notmatch 'fixture-server-error') 'The server response payload leaked through the default error record.'
}

Test-Case 'Binary uploads and downloads preserve all bytes' {
    $fixture = New-Fixture
    [byte[]]$bytes = @(0, 1, 127, 128, 255, 10, 13, 0)
    $fixture.Handler.QueueJson(200, '')
    $bodyType = (Get-Command $upload.commandName -Module $module.Name).Parameters['Body'].ParameterType
    $stream = [IO.MemoryStream]::new($bytes, $false)
    try {
        $file = [Activator]::CreateInstance($bodyType, @($stream, 'fixture.bin', 'application/octet-stream'))
        Invoke-Endpoint $upload @{ Connection = $fixture.Connection; AppId = 'fixture-app'; Path = 'fixture.bin'; Body = $file } | Out-Null
        Assert-Equal ([Convert]::ToBase64String($bytes)) ([Convert]::ToBase64String($fixture.Handler.Requests[0].Body)) 'Upload modified binary bytes.'
        Assert-True ($fixture.Handler.Requests[0].ContentType -like 'application/octet-stream*') 'Upload content type was not binary.'
    } finally { $stream.Dispose() }
    $fixture.Handler.QueueBytes(200, $bytes, 'application/octet-stream')
    $fileResponse = Invoke-Endpoint $download @{ Connection = $fixture.Connection; AppId = 'fixture-app'; Path = 'fixture.bin' }
    $received = [IO.MemoryStream]::new()
    try {
        $fileResponse.Stream.CopyTo($received)
        Assert-Equal ([Convert]::ToBase64String($bytes)) ([Convert]::ToBase64String($received.ToArray())) 'Download modified binary bytes.'
    } finally { $received.Dispose(); $fileResponse.Dispose() }
}

Test-Case 'Missing connection fails locally and explicit connection leaves default unchanged' {
    Clear-DefaultConnection
    $failure = $null
    try { Invoke-Endpoint $listUsers @{ ErrorAction = 'Stop' } | Out-Null } catch { $failure = $_ }
    Assert-True ($null -ne $failure) 'Missing connection did not fail.'
    Assert-True ($failure.ToString() -match '(?i)connect') 'Missing connection error is not actionable.'
    $fixture = New-Fixture
    $fixture.Handler.QueueJson(200, '{"data":[]}')
    Invoke-Endpoint $listUsers @{ Connection = $fixture.Connection } | Out-Null
    Assert-True ($null -eq [QlikCloud.Common.ConnectionManager]::GetConnection()) 'Explicit connection unexpectedly became the runspace default.'
}

Test-Case 'Default connection is resolved separately for each pipeline record' {
    Clear-DefaultConnection
    $first = New-Fixture
    $second = New-Fixture
    $first.Handler.QueueJson(200, (New-UserJson 'first'))
    $second.Handler.QueueJson(200, (New-UserJson 'second'))
    $output = @(& {
        [QlikCloud.Common.ConnectionManager]::SetConnection($first.Connection)
        [pscustomobject]@{ UserId = 'first' }
        [QlikCloud.Common.ConnectionManager]::SetConnection($second.Connection)
        [pscustomobject]@{ UserId = 'second' }
    } | & $getUser.commandName)
    Assert-Equal 2 $output.Count 'Default-switching pipeline dropped a result.'
    Assert-Equal 1 $first.Handler.Count 'The first default handled a later pipeline record.'
    Assert-Equal 1 $second.Handler.Count 'The second default was not selected for its record.'
    Clear-DefaultConnection
}

Test-Case 'Connect default is runspace-local and Disconnect removes it' {
    Clear-DefaultConnection
    $parameters = @{ Tenant = 'https://qlikcloud-acceptance.invalid/'; APIKey = 'local-fixture-only' }
    Connect-QlikCloud @parameters | Out-Null
    $default = Get-QCConnection
    Assert-True ($null -ne $default) 'Connect did not establish a runspace default.'
    $explicit = Connect-QlikCloud @parameters -PassThru
    Assert-True ([object]::ReferenceEquals($default, (Get-QCConnection))) 'PassThru replaced the default connection.'
    Disconnect-QlikCloud -Connection $explicit -Confirm:$false
    Assert-True $explicit.IsDisposed 'Disconnect did not dispose the explicit connection.'
    Assert-True ([object]::ReferenceEquals($default, (Get-QCConnection))) 'Disconnecting an explicit connection removed a different default.'
    $other = [Management.Automation.PowerShell]::Create()
    try {
        $null = $other.AddScript('param($manifest) Import-Module -Name $manifest -Force; try { Get-QCConnection -ErrorAction Stop; "unexpected-default" } catch { $_.FullyQualifiedErrorId }').AddArgument($ModulePath)
        $result = $other.Invoke()
        Assert-Equal 0 $other.Streams.Error.Count 'Independent runspace inspection failed.'
        Assert-True ($result[0].PSObject.BaseObject -like 'QlikCloudConnectionRequired*') 'Connection default leaked into another runspace.'
    } finally { $other.Dispose() }
    Clear-DefaultConnection
    Assert-True $default.IsDisposed 'Disconnect did not dispose the default connection.'
    Assert-True ($null -eq [QlikCloud.Common.ConnectionManager]::GetConnection()) 'Disconnect retained the default connection.'
}

Test-Case 'Unsupported JWT and non-OAuth token exchange fail without authentication fallback' {
    $failure = $null
    try { Connect-QlikCloud -Tenant 'https://qlikcloud-acceptance.invalid/' -JWTToken 'local-fixture-only' -ErrorAction Stop | Out-Null } catch { $failure = $_ }
    Assert-True ($null -ne $failure -and $failure.FullyQualifiedErrorId -like 'QlikCloudJwtNotSupported*') 'JWT did not fail with the explicit unsupported-authentication error.'
    $fixture = New-Fixture
    $failure = $null
    try { Receive-QCToken -Connection $fixture.Connection -ErrorAction Stop | Out-Null } catch { $failure = $_ }
    Assert-True ($null -ne $failure -and $failure.FullyQualifiedErrorId -like 'QlikCloudOAuthConnectionRequired*') 'Token exchange did not enforce an OAuth connection.'
    Assert-Equal 0 $fixture.Handler.Count 'Unsupported token exchange made an HTTP request.'
}

Test-Case 'SDK OAuth requires an explicit client registration' {
    foreach ($clientId in @($null, '', '   ')) {
        $failure = $null
        try {
            [QlikCloud.Clients.OAuthClient]::new('https://qlikcloud-acceptance.invalid/', $clientId,
                'local-fixture-only', [Microsoft.Extensions.Logging.Abstractions.NullLogger]::Instance) | Out-Null
        } catch { $failure = $_.Exception.GetBaseException() }
        Assert-True ($failure -is [ArgumentException]) 'A missing or blank OAuth client ID did not fail locally with ArgumentException.'
        Assert-Equal 'clientId' $failure.ParamName 'The missing OAuth registration error did not identify the clientId argument.'
    }
}

Test-Case 'OAuth client credentials parse a mocked token and authenticate generated REST calls' {
    $handler = [QlikCloud.AcceptanceFixtures.MockTransport]::new()
    $http = [Net.Http.HttpClient]::new($handler)
    $http.BaseAddress = [uri]'https://qlikcloud-acceptance.invalid/api/'
    $http.Timeout = [TimeSpan]::FromSeconds(15)
    $oauth = $null
    $connection = $null
    try {
        $oauth = [QlikCloud.Clients.OAuthClient]::new(
            'https://qlikcloud-acceptance.invalid/', 'fixture-client', 'fixture-secret',
            [Microsoft.Extensions.Logging.Abstractions.NullLogger]::Instance)
        $oauth.Config.httpClient.Dispose()
        $oauth.Config.httpClient = $http
        $handler.QueueJson(200, '{"access_token":"fixture-access-token","token_type":"Bearer","expires_in":3600}')
        $oauth.ClientCredentials('fixture.scope')
        Assert-Equal 1 $handler.Count 'Client credentials did not perform exactly one mock request.'
        $request = $handler.Requests[0]
        Assert-Equal 'POST' $request.Method 'Client credentials did not use POST.'
        Assert-Equal 'https://qlikcloud-acceptance.invalid/oauth/token' $request.Uri 'Client credentials used the wrong token endpoint.'
        Assert-True ($request.ContentType -like 'application/x-www-form-urlencoded*') 'Token request was not form encoded.'
        Assert-True ($request.Text -match '(?:^|&)grant_type=client_credentials(?:&|$)') 'Client credentials grant was missing.'
        Assert-True ($request.Text -match '(?:^|&)scope=fixture.scope(?:&|$)') 'Requested scope was missing.'
        Assert-Equal 'Bearer' $http.DefaultRequestHeaders.Authorization.Scheme 'Token was not installed as bearer authentication.'
        Assert-Equal 'fixture-access-token' $http.DefaultRequestHeaders.Authorization.Parameter 'IdentityModel did not parse the mock access token.'
        $connection = [QlikCloud.Common.Connection]::new($oauth)
        $handler.QueueJson(200, (New-UserJson 'oauth-user'))
        $result = Invoke-Endpoint $getUser @{ Connection = $connection; UserId = 'oauth-user' }
        Assert-Equal 'oauth-user' $result.Id 'Authenticated OAuth connection did not invoke the generated REST client.'
        Assert-Equal 2 $handler.Count 'Authenticated request did not use the injected mock transport.'
    }
    finally {
        if ($null -ne $connection) { $connection.Dispose() }
        $http.Dispose()
        if ($null -ne $oauth) { $oauth.Config.httpClient.Dispose() }
    }
}

Test-Case 'Engine API-key location initializes without opening a session' {
    # Resolve the command's actual Engine return type before using that optional
    # SDK; this exercises normal lazy dependency loading, not an assembly preload.
    $openMethod = (Get-Command Invoke-OpenApp -Module $module.Name).ImplementingType.GetMethod('OpenQlikApp')
    Assert-Equal 'Qlik.Engine.IApp' $openMethod.ReturnType.GetGenericArguments()[0].FullName 'Engine command return contract changed.'
    $location = [Qlik.Engine.QcsLocation]::FromUri('https://qlikcloud-acceptance.invalid/')
    $location.AsApiKey('fixture-engine-api-key')
    Assert-True ($location -is [Qlik.Engine.IQcsLocation]) 'Engine did not create the expected Qlik Cloud location.'
    Assert-Equal 'https' $location.ServerUri.Scheme 'Engine location scheme changed.'
    Assert-Equal 'qlikcloud-acceptance.invalid' $location.ServerUri.Host 'Engine location points outside the offline fixture.'
    Assert-True ($null -ne $location.SessionInfo) 'Engine API-key configuration did not initialize session metadata.'
}

Test-Case 'Closing a runspace disposes its owned default transport' {
    $fixture = New-Fixture
    $runspace = [Management.Automation.Runspaces.RunspaceFactory]::CreateRunspace()
    $runspace.Open()
    $pipeline = [Management.Automation.PowerShell]::Create()
    $pipeline.Runspace = $runspace
    try {
        $null = $pipeline.AddScript('param($manifest, $connection) Import-Module -Name $manifest -Force; [QlikCloud.Common.ConnectionManager]::SetConnection($connection)').AddArgument($ModulePath).AddArgument($fixture.Connection)
        $null = $pipeline.Invoke()
        Assert-Equal 0 $pipeline.Streams.Error.Count 'Runspace default setup failed.'
        $runspace.Close()
        Assert-True $fixture.Connection.IsDisposed 'Closed runspace retained its default connection.'
        Assert-True ($fixture.Handler.DisposedCount -gt 0) 'Closed runspace did not dispose its HTTP transport.'
    } finally { $pipeline.Dispose(); $runspace.Dispose() }
}

Test-Case 'Stopping an in-flight command cancels the HTTP request' {
    $fixture = New-Fixture
    $fixture.Handler.QueuePending()
    $pipeline = [Management.Automation.PowerShell]::Create()
    try {
        $null = $pipeline.AddScript('param($manifest) Import-Module -Name $manifest -Force').AddArgument($ModulePath)
        $null = $pipeline.Invoke()
        Assert-Equal 0 $pipeline.Streams.Error.Count 'Cancellation test runspace could not import module.'
        $pipeline.Commands.Clear()
        $null = $pipeline.AddCommand($listUsers.commandName).AddParameter('Connection', $fixture.Connection)
        $pending = $pipeline.BeginInvoke()
        $deadline = [DateTime]::UtcNow.AddSeconds(10)
        while ($fixture.Handler.Count -eq 0 -and [DateTime]::UtcNow -lt $deadline -and -not $pending.IsCompleted) { Start-Sleep -Milliseconds 20 }
        Assert-Equal 1 $fixture.Handler.Count 'The cancellation fixture never received a request.'
        $stop = $pipeline.BeginStop($null, $null)
        Assert-True $stop.AsyncWaitHandle.WaitOne(5000) 'Stopping the command exceeded five seconds.'
        $pipeline.EndStop($stop)
        Assert-True ($fixture.Handler.CancelledCount -gt 0) 'PowerShell Stop did not cancel the HTTP cancellation token.'
    } finally { $fixture.Http.Dispose(); $pipeline.Dispose() }
}

foreach ($fixture in $fixtures) { $fixture.Http.Dispose() }
$null = New-Item -ItemType Directory -Path (Split-Path -Parent $OutputPath) -Force
$failed = @($results | Where-Object { -not $_.passed })
$report = [ordered]@{
    modulePath = $ModulePath
    moduleName = $module.Name
    moduleVersion = $module.Version.ToString()
    powershellVersion = $PSVersionTable.PSVersion.ToString()
    powershellEdition = $PSVersionTable.PSEdition
    hostPath = [Diagnostics.Process]::GetCurrentProcess().MainModule.FileName
    clrVersion = [Environment]::Version.ToString()
    sdkPath = $sdkAssembly.Location
    sdkSha256 = (Get-FileHash -LiteralPath $sdkAssembly.Location -Algorithm SHA256).Hash.ToLowerInvariant()
    inventoryPath = $inventoryPath
    operationCount = $operations.Count
    endpointCommandCount = @($operations.commandName | Sort-Object -Unique).Count
    maximumEndpointParameterSetCount = ($inventory.commands | ForEach-Object { @($_.parameterSetNames).Count } | Measure-Object -Maximum).Maximum
    constructorCount = $constructors.Count
    exportedCommandCount = @(Get-Command -Module $module.Name).Count
    passed = $results.Count - $failed.Count
    failed = $failed.Count
    tenantCallsPerformed = $false
    results = $results.ToArray()
}
$report | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $OutputPath -Encoding utf8
Write-Host "$($report.passed) passed; $($report.failed) failed. Evidence: $OutputPath"
if ($failed.Count -gt 0) { exit 1 }
