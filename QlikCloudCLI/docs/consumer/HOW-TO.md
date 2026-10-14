# QlikCloudCLI operator guide

QlikCloudCLI provides PowerShell commands for Qlik Cloud REST APIs, connection management, and app Engine access. Use the compiled module from a Gallery package or ZIP with **Windows PowerShell 5.1**, included with Windows. Start `powershell.exe`; no additional PowerShell installation is required.

Use your own tenant hostname, secret provider, resource identifiers, and local file paths. The examples never need a token embedded in a script. A connection's permissions still determine which APIs and resources it can access.

## Install from the PowerShell Gallery

To install a published release from PSGallery, run this in Windows PowerShell:

```powershell
Install-Module -Name QlikCloudCLI -Repository PSGallery -Scope CurrentUser
Import-Module QlikCloudCLI -ErrorAction Stop
Get-Module QlikCloudCLI | Select-Object Name, Version, ModuleBase
```

PowerShellGet may prompt to install its NuGet provider or confirm the PSGallery repository. Follow your organization's repository policy. The module's compiled dependencies are included; a development SDK is not required. If Gallery access is unavailable, use the ZIP instructions below.

To update a version previously installed through PowerShellGet:

```powershell
Update-Module -Name QlikCloudCLI -ErrorAction Stop
```

Then close any PowerShell processes using the old module and start a fresh session. Loaded .NET assemblies remain in their original process even after `Remove-Module`. `Update-Module` does not manage a manually extracted ZIP installation.

## Extract the ZIP and import the module

Save the QlikCloudCLI ZIP to your computer and verify that it came from a source you trust. The example below assumes its name is `QlikCloudCLI.zip` in your Downloads directory; change `$zipPath` to match the package you received. Unblock the ZIP before extraction, then extract into a new directory so all files belong to the same package version:

```powershell
$zipPath = Join-Path $HOME 'Downloads\QlikCloudCLI.zip'
$extractRoot = Join-Path $HOME 'Tools\QlikCloudCLI-release'
if (Test-Path -LiteralPath $extractRoot) {
    throw "Choose a new extraction directory; $extractRoot already exists."
}
Unblock-File -LiteralPath $zipPath -ErrorAction Stop
Expand-Archive -LiteralPath $zipPath -DestinationPath $extractRoot -ErrorAction Stop

$moduleFolder = Join-Path $extractRoot 'QlikCloudCLI'
$manifestPath = Join-Path $moduleFolder 'QlikCloudCLI.psd1'
Import-Module $manifestPath
Get-Module QlikCloudCLI | Select-Object Name, Version, ModuleBase
```

The ZIP contains a top-level `QlikCloudCLI` directory. Keep that entire directory together: `QlikCloudCLI.psd1` loads the binaries in `bin`. The included `README.md`, this guide, `LLM.txt`, `COMMANDS.md`, and command inventories describe the package you installed. Importing the manifest directly is sufficient to use it.

If you already extracted a trusted package and Windows reports blocked files, unblock only that package's extracted `QlikCloudCLI` folder. In the example above, `$moduleFolder` is exactly `$extractRoot\QlikCloudCLI`:

```powershell
Get-ChildItem -LiteralPath $moduleFolder -Recurse -File -ErrorAction Stop |
    Unblock-File -ErrorAction Stop
```

Then retry the manifest import in a fresh Windows PowerShell session. Keep this recovery command scoped to the module folder, and follow your organization's PowerShell execution policy.

### Optional signature check

For a signed release, inspect the manifest and the two module DLLs:

```powershell
$signaturePaths = @(
    $manifestPath
    (Join-Path $moduleFolder 'bin\QlikCloud.dll')
    (Join-Path $moduleFolder 'bin\QlikCloudCLI.dll')
)
Get-AuthenticodeSignature -LiteralPath $signaturePaths -ErrorAction Stop |
    Select-Object Path, Status, @{ Name = 'Signer'; Expression = { $_.SignerCertificate.Subject } }
```

Expect `Valid` and verify the publisher's identity for these three files. Signature trust is evaluated using your machine's certificate configuration; follow your organization's certificate policy if validation fails. This check makes no assumption about the signing of bundled third-party DLLs.

### Optional installation for your Windows user

Installing in your user's Windows PowerShell module directory lets you import by name. Run this in `powershell.exe`: it derives the `WindowsPowerShell\Modules` directory from your profile location, including redirected Documents folders, and copies the **whole QlikCloudCLI directory**, including `bin`, under its package version:

```powershell
$userModuleRoot = Join-Path (Split-Path $PROFILE.CurrentUserAllHosts -Parent) 'Modules'
$moduleInstallRoot = Join-Path $userModuleRoot 'QlikCloudCLI'
$version = [string](Import-PowerShellDataFile -LiteralPath $manifestPath).ModuleVersion
$installPath = Join-Path $moduleInstallRoot $version
if (Test-Path -LiteralPath $installPath) {
    throw "Version $version is already installed at $installPath."
}
New-Item -ItemType Directory -Path $moduleInstallRoot -Force | Out-Null
Copy-Item -LiteralPath $moduleFolder -Destination $installPath -Recurse
Get-Module -ListAvailable QlikCloudCLI | Select-Object Name, Version, ModuleBase
```

In a new `powershell.exe` session, use `Import-Module QlikCloudCLI`. To select a particular installed version, use `Import-Module QlikCloudCLI -RequiredVersion <version>` with the version shown above.

### Upgrade to another package

Extract the new ZIP into a new directory. Close Windows PowerShell sessions that loaded the previous module, start a fresh `powershell.exe` process, and import the new manifest by its full path. You can then install the new version using the same procedure. If a replacement package has the same version, keep the old directory as a backup outside your Windows PowerShell module directory before replacing the complete installed version folder. Keep each package's binaries together. `Remove-Module` alone does not unload the DLLs from a running PowerShell process.

## Connect using a secret

These examples expect a tenant base URL or hostname such as `tenant.region.qlikcloud.com`. Do not append `/api/v1`. Populate the environment variables through your normal secret provider or automation environment. `-APIKey` and `-ClientSecret` currently accept strings, so do not print them or include their literal values in shell history or shared scripts.

For an interactive session, prompt without echoing the API key. Convert it to the string accepted by `Connect-QlikCloud`, then clear the temporary variables:

```powershell
$tenant = Read-Host 'Tenant hostname or URL'
$secureApiKey = Read-Host 'API key' -AsSecureString
try {
    $apiKey = ([pscredential]::new('QlikCloudAPIKey', $secureApiKey)).GetNetworkCredential().Password
    $connection = Connect-QlikCloud -Tenant $tenant -APIKey $apiKey -PassThru
}
finally {
    $apiKey = $null
    $secureApiKey.Dispose()
    $secureApiKey = $null
}
```

This leaves `$connection` open for the later recipes; disconnect it when finished. The connection retains its authentication credential while it is in use.

For an unattended script, this independent example reads an API key from the automation environment and closes its connection after one request:

```powershell
if ([string]::IsNullOrWhiteSpace($env:QLIK_TENANT) -or
    [string]::IsNullOrWhiteSpace($env:QLIK_API_KEY)) {
    throw 'Set QLIK_TENANT and QLIK_API_KEY through your automation environment.'
}
$connection = Connect-QlikCloud -Tenant $env:QLIK_TENANT `
    -APIKey $env:QLIK_API_KEY -PassThru

try {
    $page = Get-QCCollections -Connection $connection -Type Private -Limit 10
    $page.Data | Select-Object Id, Name, Type
}
finally {
    Disconnect-QlikCloud -Connection $connection
}
```

An API-key connection stores the bearer credential locally. A successful `Connect-QlikCloud` alone does not prove the key is valid or authorized: the first API request does that. The later examples assume you have opened `$connection` again and will disconnect it after the work.

For OAuth client credentials, use a client configured for that grant and supply its approved scopes explicitly:

```powershell
$oauthParameters = @{
    Tenant            = $env:QLIK_TENANT
    ClientID          = $env:QLIK_CLIENT_ID
    ClientSecret      = $env:QLIK_CLIENT_SECRET
    Scope             = $env:QLIK_OAUTH_SCOPE
    ClientCredentials = $true
    PassThru          = $true
}
foreach ($name in 'Tenant', 'ClientID', 'ClientSecret', 'Scope') {
    if ([string]::IsNullOrWhiteSpace($oauthParameters[$name])) {
        throw "Configure the OAuth value for $name before connecting."
    }
}
$connection = Connect-QlikCloud @oauthParameters
```

The module's default OAuth scopes are user-oriented. Do not assume those defaults are suitable for a machine client. Client credentials, API keys, and browser sign-in may have different API and resource access. User-specific routes such as `Get-QCUsersMe` are not a universal machine-client health check.

Interactive OAuth opens a browser for sign-in:

```powershell
$connection = Connect-QlikCloud -Tenant $env:QLIK_TENANT `
    -ClientID $env:QLIK_CLIENT_ID -Scope $env:QLIK_OAUTH_SCOPE -PassThru
```

That flow listens on `http://127.0.0.1:<an available port>/callback/`. Configure the OAuth client to allow this loopback redirect behavior. A confidential browser client may also need `-ClientSecret`. Do not assume automatic renewal for a long-running job; reconnect when the credential expires. `-JWTToken` exists in the command metadata but reports that JWT authentication is not implemented.

### Default and explicit connections

Without `-PassThru`, `Connect-QlikCloud` sets a default for the current PowerShell runspace and produces no connection object. With `-PassThru`, it returns an explicit connection and leaves that default unchanged.

```powershell
Connect-QlikCloud -Tenant $env:QLIK_TENANT -APIKey $env:QLIK_API_KEY
Get-QCCollections -Type Private -Limit 5
$defaultConnection = Get-QCConnection

$otherConnection = Connect-QlikCloud -Tenant $env:QLIK_OTHER_TENANT `
    -APIKey $env:QLIK_OTHER_API_KEY -PassThru
try {
    Get-QCCollections -Connection $otherConnection -Type Private -Limit 5
}
finally {
    Disconnect-QlikCloud -Connection $otherConnection
    Disconnect-QlikCloud   # Closes this runspace's default connection.
}
```

Use explicit connections in reusable functions and multi-tenant scripts. Dispose connections you created; disconnect an old default before replacing it if you no longer need it. Parallel runspaces have their own defaults.

## Find a command and its request model

Public REST and request-model command nouns start with `QC`: for example, `Get-QCApiKeys` and `New-QCCollectionsCollectionsCreateCollectionRequestBodyObject`. REST nouns follow the resource path without its identifier placeholders. Related list/detail GET routes share one command with separate parameter sets. Nested resources retain their own commands. Constructor names still include the complete model name and end in `Object`. Use `Get-QCConnection` for connection lookup and `Receive-QCToken` for token exchange. `Connect-QlikCloud`, `Disconnect-QlikCloud`, `Get-QlikUserAgent`, and `Invoke-OpenApp` retain their names. The module is still `QlikCloudCLI`, and .NET types still use namespaces such as `QlikCloud.Collections`.

Use the exact current command names; previous long REST and constructor names are not exported as compatibility aliases.

```powershell
Get-Command -Module QlikCloudCLI
Get-Command Get-QCCollections -Syntax
Get-Command '*QCCollections*' -Module QlikCloudCLI
Get-Command 'New-QCCollections*Object' -Module QlikCloudCLI

$module = Get-Module QlikCloudCLI
$inventory = Get-Content (Join-Path $module.ModuleBase 'commands.json') -Raw |
    ConvertFrom-Json
$inventory.operations |
    Where-Object api -EQ 'collections' |
    Select-Object commandName, parameterSetName, httpMethod, path
$inventory.commands |
    Where-Object commandName -EQ 'Get-QCCollections' |
    Select-Object commandName, defaultParameterSetName, parameterSetNames, operationCount
```

The included [COMMANDS.md](COMMANDS.md) lists commands, routes, parameter types, and constructors. In version 2 of `commands.json`, `commands` describes distinct REST commands and their parameter sets; `operations` records each HTTP route, its `commandName`, `parameterSetName`, and parameters; `constructors` describes request models. Two operations can therefore have the same command name. Match an operation by HTTP method and path, then inspect its parameter set. Use the [Qlik REST API reference](https://qlik.dev/apis/rest/) for request semantics, required fields, permissions, and endpoint limits. Inspect PowerShell parameter metadata instead of guessing a spelling:

```powershell
(Get-Command New-QCCollections).Parameters['Body'].ParameterType.FullName
(Get-Command Get-QCCollections).Parameters['Type'].ParameterType
[enum]::GetNames([QlikCloud.Collections.CollectionTypes])
(Get-Command Get-QCCollections).ParameterSets |
    Select-Object Name, IsDefault, @{ Name = 'Parameters'; Expression = { $_.Parameters.Name -join ', ' } }
```

The usual route-to-command mapping is:

| API operation | Command example |
| --- | --- |
| GET `/collections` | `Get-QCCollections` (`List`, the default set) |
| GET `/collections/{collectionId}` | `Get-QCCollections -CollectionId $collectionId` (`ByCollectionId`) |
| GET `/collections/{collectionId}/items` | `Get-QCCollectionsItems -CollectionId $collectionId` (`List`) |
| GET `/collections/{collectionId}/items/{itemId}` | `Get-QCCollectionsItems -CollectionId $collectionId -ItemId $itemId` (`ByItemId`) |
| POST collection | `New-QCCollections` |
| PUT resource | `Set-QCCollections` |
| PATCH resource | `Update-QCCollections` |
| DELETE resource | `Remove-QCCollections` |
| POST action | `Invoke-QCAppsExport` |
| Construct a request body locally | `New-QCCollectionsCollectionsCreateCollectionRequestBodyObject` |

Constructors end in `Object`; they do not contact the tenant. Names include the API and model because similar model names occur in different APIs. Use `Get-Command` or the included inventory to find the exact command for a route. Some parameter names have a `Parameter` suffix to distinguish them from PowerShell's own parameters.

Supplying the detail identifier selects the detail parameter set; without it, the grouped command uses `List`. List-only filters cannot be combined with detail-only parameters. PowerShell rejects that combination before making a request. Parameters for a parent resource remain required in both nested-resource sets. The same rule does not imply a list route exists for every command: `Get-QCApps -AppId $appId` reads one app; use `Get-QCItems -ResourceType App` for the app catalog.

A few routes retain explicit suffixes because combining their parameters would be ambiguous: `Get-QCAppsReportFiltersById`, `Get-QCDirectAccessAgentsConnectorsFilesByFileType`, `Get-QCSharingTasksExecutionsByExecutionId`, and `Invoke-QCDataFilesActionsChangeSpaceById`. Look them up rather than adding a guessed `ById` suffix. For audits, `Get-QCAudits -AuditId $auditId` selects one audit; the list route's `-Id` remains a filter.

## List, filter, and export data

The default result is the typed API body. Many list endpoints return an envelope with `Data` and `Links`; they do not directly return every resource in the tenant.

```powershell
$users = Get-QCUsers -Connection $connection -Limit 20 `
    -Filter 'status eq "active"' -TotalResults $false
$users.Data | Select-Object Id, Name, Email, Status

# This exports the first page only; see the pagination function below for all pages.
$users.Data | Select-Object Id, Name, Email, Status |
    Export-Csv -LiteralPath .\users-first-page.csv -NoTypeInformation -Encoding utf8

$spaces = Get-QCSpaces -Connection $connection -Name 'Finance' -Limit 25
$spaces.Data | Select-Object Id, Name, Type, Description

$apps = Get-QCItems -Connection $connection -ResourceType App -Limit 20
$apps.Data | Select-Object Id, ResourceId, Name, SpaceId
```

Users use the users API's filter expression syntax. Spaces `-Name` is a case-insensitive contains search, so `Finance` can match a longer name. Collection `-Name` is case-sensitive, while collection `-Query` searches name or description case-insensitively. These filters are endpoint-specific.

An item's `Id` is its catalog item ID; an app item's `ResourceId` is the app ID to pass to app or Engine commands. Keep those identifiers distinct. Use `Format-Table` only at the display step, never before `Export-Csv` or a command that needs objects.

Optional parameters are omitted when unbound. Supplying `-TotalResults $false` deliberately sends false. Do not replace a boolean parameter with a switch-like `-TotalResults` without its value.

Single path identifiers support pipeline values. For example, this makes one request for each ID from the already fetched page:

```powershell
$users.Data.Id | Get-QCUsers -Connection $connection |
    Select-Object Id, Name, Email
```

The single-user command returns `QlikCloud.Users.Response`, whose `Id`, `Name`, and `Email` are direct properties; there is no `Data` envelope for that response. Path parameters also bind by property name, and typed bodies can bind from the pipeline. A pipeline does not combine multiple API operations into a transaction.

## Follow users pagination explicitly

`Get-QCUsers` accepts an opaque cursor in `-Next`; its response exposes the next link at `Links.Next.Href`. The users API allows `-Limit` from 1 to 100. Its next link may contain a `startingAfter` query value; pass that decoded value to `-Next` to retrieve the next page. The helper accepts either `next` or `startingAfter` from the link. Pass the decoded **cursor value**, not the entire URL, and keep the original filters on every request. Never combine `-Next` and `-Prev`.

This function follows the users endpoint's contract, stops when its next link is absent, and rejects a repeated cursor. `MaxPages` bounds the number of requests and throws rather than silently truncating the result. Parsing a returned link does not make a request to that URL.

```powershell
function Get-AllQlikCloudUsers {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [QlikCloud.Common.Connection] $Connection,
        [string] $Filter = 'status eq "active"',
        [ValidateRange(1, 100000)]
        [int] $MaxPages = 1000
    )

    Add-Type -AssemblyName System.Web
    $request = @{ Connection = $Connection; Limit = 100; TotalResults = $false }
    if (-not [string]::IsNullOrWhiteSpace($Filter)) { $request.Filter = $Filter }
    $seen = [Collections.Generic.HashSet[string]]::new()
    $pageNumber = 0
    do {
        if ($pageNumber -ge $MaxPages) {
            throw "Users pagination exceeded MaxPages ($MaxPages); no complete export was produced."
        }
        $page = Get-QCUsers @request
        $pageNumber++
        $page.Data

        $nextHref = if ($null -ne $page.Links -and $null -ne $page.Links.Next) {
            $page.Links.Next.Href
        }
        if ([string]::IsNullOrWhiteSpace($nextHref)) { break }

        $link = [uri]::new([uri]'https://pagination.invalid/', $nextHref)
        $query = [System.Web.HttpUtility]::ParseQueryString($link.Query)
        $cursor = $query['next']
        if ([string]::IsNullOrWhiteSpace($cursor)) {
            $cursor = $query['startingAfter']
        }
        if ([string]::IsNullOrWhiteSpace($cursor)) {
            throw 'The users next link did not contain next or startingAfter.'
        }
        if (-not $seen.Add($cursor)) { throw 'The users API repeated a next cursor.' }
        $request.Next = $cursor
    } while ($true)
}

# Complete every request before opening the CSV output file.
$allUsers = @(Get-AllQlikCloudUsers -Connection $connection `
    -Filter 'status eq "active"' -MaxPages 1000)
$allUsers |
    Select-Object Id, Name, Email, Status |
    Export-Csv -LiteralPath .\active-users.csv -NoTypeInformation -Encoding utf8
```

There is no automatic paging or universal `-All` parameter. A short page alone is not the stop condition: use the endpoint's next link. Other APIs can have different pagination shapes and limits; check their response type and Qlik's API reference before adapting this function. Run the fetch and export together in a script: this example gathers the complete result in memory before writing CSV, so a paging failure does not leave a partial final CSV. Increase `MaxPages` deliberately if the expected inventory requires it.

## Construct request objects, preview changes, and use PATCH

This tutorial creates one uniquely named private collection, reads it, changes its description with PUT and PATCH, and deletes only that created collection. It adds no items and does not publish the collection. Use an identity that can create its own private collections.

First construct a typed request and preview it. This step makes no API request and works without a connection:

```powershell
$tutorialName = 'QlikCloudCLI-tutorial-' + [guid]::NewGuid().ToString('N')
$createBody = New-QCCollectionsCollectionsCreateCollectionRequestBodyObject `
    -Name $tutorialName -Type Private -Description 'Disposable QlikCloudCLI tutorial'
New-QCCollections -Body $createBody -WhatIf
```

The API requires `name` and `type` for creation even though the constructor's property parameters are optional. Enum values in PowerShell use names such as `Private`; JSON uses the API's values such as `private`.

You can start with JSON or copy a typed model and override selected properties:

```powershell
$jsonBody = New-QCCollectionsCollectionsCreateCollectionRequestBodyObject `
    -Json '{"name":"Local model example","type":"private","description":"Not sent"}'
$copiedBody = New-QCCollectionsCollectionsCreateCollectionRequestBodyObject `
    -InputObject $jsonBody -Description 'Only the copy changes'
$jsonBody.Description       # Not sent
$copiedBody.Description     # Only the copy changes
```

`-InputObject` copies normal models, including nested models, before applying explicitly bound overrides. Collection constructors accept `-Items`; use the item's typed constructor to create nested request bodies. Only supplied overrides replace values, including explicit false and zero. Constructors convert JSON into the command's request type; the server still validates the request's business rules.

Run the following block when ready to exercise the complete collection lifecycle. It uses the body created above and the open `$connection`. Cleanup captures the ID returned by this creation and checks it again before deletion. It never searches for old tutorial collections to delete.

```powershell
$createdId = $null
try {
    $created = New-QCCollections -Connection $connection -Body $createBody
    if ([string]::IsNullOrWhiteSpace($created.Id)) {
        throw 'Creation returned no collection ID; automatic cleanup cannot identify the resource.'
    }
    $createdId = $created.Id

    $current = Get-QCCollections `
        -Connection $connection -CollectionId $createdId
    $current | Select-Object Id, Name, Type, Description, ItemCount

    $replacement = New-QCCollectionsCollectionsUpdateCollectionRequestBodyObject `
        -Name $tutorialName -Description 'Description changed with PUT'
    Set-QCCollections -Connection $connection `
        -CollectionId $createdId -Body $replacement -WhatIf
    $null = Set-QCCollections -Connection $connection `
        -CollectionId $createdId -Body $replacement

    $change = New-QCCollectionsCollectionByIdPatchItemObject `
        -Op Replace -Path Description -Value 'Description changed with PATCH'
    $patch = New-QCCollectionsCollectionByIdPatchObject -Items @($change)
    Update-QCCollections -Connection $connection `
        -CollectionId $createdId -Body $patch -WhatIf
    $null = Update-QCCollections -Connection $connection `
        -CollectionId $createdId -Body $patch

    Get-QCCollections -Connection $connection `
        -CollectionId $createdId | Select-Object Id, Name, Type, Description
}
finally {
    if ($createdId) {
        $check = Get-QCCollections `
            -Connection $connection -CollectionId $createdId
        if ($check.Id -ne $createdId -or $check.Name -ne $tutorialName -or
            $check.Type.ToString() -ne 'Private' -or $check.ItemCount -ne 0) {
            throw "Collection $createdId no longer matches this empty private tutorial collection; it was not deleted."
        }
        Remove-QCCollections -Connection $connection `
            -CollectionId $createdId -WhatIf
        $null = Remove-QCCollections -Connection $connection `
            -CollectionId $createdId
    }
}
```

The PATCH body serializes to an array such as `[{"op":"replace","path":"/description","value":"..."}]`. For this route, `-Body` is a `CollectionByIdPatch`, not a single patch item. The equivalent local constructor is:

```powershell
$patch = New-QCCollectionsCollectionByIdPatchObject `
    -Json '[{"op":"replace","path":"/description","value":"Description changed with PATCH"}]'
```

All REST mutations support `-WhatIf` and `-Confirm`. `-WhatIf` shows the target and action without sending the request; it does not validate tenant permissions or ask the server to validate the body. Add `-Confirm` to a real mutation when you want an interactive prompt. A failure during cleanup leaves the captured `$createdId` available for investigation; do not replace that check with deletion by name.

## Upload and download binary content

File uploads use a typed `FileParameter` containing an open stream. This example uploads a local file to a unique media path in an app you are allowed to modify. Set `QLIK_APP_ID` to the app's **resource ID** and `QLIK_UPLOAD_PATH` to an existing local file. The media path is within the app, not a local filesystem path.

```powershell
$appId = $env:QLIK_APP_ID
$localPath = [IO.Path]::GetFullPath($env:QLIK_UPLOAD_PATH)
$mediaPath = 'QlikCloudCLI-tutorial-' + [guid]::NewGuid().ToString('N') + '.bin'
$uploadStream = [IO.File]::OpenRead($localPath)
try {
    $uploadBody = New-QCAppsFileParameterObject -Data $uploadStream `
        -FileName ([IO.Path]::GetFileName($localPath)) -ContentType 'application/octet-stream'
    Set-QCAppsMediaFiles -Connection $connection `
        -AppId $appId -Path $mediaPath -Body $uploadBody -WhatIf
    $null = Set-QCAppsMediaFiles -Connection $connection `
        -AppId $appId -Path $mediaPath -Body $uploadBody
}
finally {
    $uploadStream.Dispose()
}
```

The upload request consumes the stream. Open a new stream and construct a new body for another request or a retry. FileParameter's `-InputObject` copies the wrapper but shares its stream; it cannot duplicate file data. There is no `-InFile` convenience parameter.

The corresponding download returns `QlikCloud.Apps.FileResponse`. Copy its `Stream` as bytes and always dispose the response. This example uses `CreateNew` so it will not overwrite an existing file:

```powershell
$downloadPath = Join-Path (Get-Location).Path ('download-' + [guid]::NewGuid().ToString('N') + '.bin')
$download = Get-QCAppsMediaFiles -Connection $connection `
    -AppId $appId -Path $mediaPath
try {
    $destination = [IO.File]::Open($downloadPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write)
    try { $download.Stream.CopyTo($destination) }
    finally { $destination.Dispose() }
}
finally {
    $download.Dispose()
}
Get-Item -LiteralPath $downloadPath | Select-Object FullName, Length
```

There is no `-OutFile` parameter. Do not pipe binary responses through `Set-Content`. The upload example leaves its unique media file in the selected app; retain `$mediaPath` if you want to remove that specific tutorial upload later. Do not assume all download workflows return bytes immediately: `Invoke-QCAppsExport`, for example, initiates app export and returns response metadata, including the export location header, rather than a QVF stream.

## Inspect status and handle failures

Use `-RawResponse` when you need successful response status or headers:

```powershell
$response = Get-QCCollections -Connection $connection -Type Private -Limit 5 -RawResponse
$response.StatusCode
$response.Headers
$response.Object.Data | Select-Object Id, Name
```

For a JSON API, the raw wrapper has `StatusCode`, `Headers`, `RawContent`, and `Object`. Default output unwraps `Object`, but keeps any collection envelope the API itself uses. A no-content response has status/headers without a body. A binary response has `Stream`, `StatusCode`, and `Headers` and must still be disposed.

HTTP failures are terminating PowerShell errors; `-RawResponse` does not turn a failed HTTP call into a successful wrapper. Handle the typed exception without printing credentials or entire records:

```powershell
try {
    $page = Get-QCCollections -Connection $connection -Type Private -Limit 5
}
catch [QlikCloudCLI.Runtime.QlikCloudApiException] {
    $status = $_.Exception.StatusCode
    Write-Warning ("Qlik Cloud returned HTTP {0}; category: {1}; error: {2}" -f
        $status, $_.CategoryInfo.Category, $_.FullyQualifiedErrorId)
    throw
}
```

Common categories are 401 `AuthenticationError`, 403 `PermissionDenied`, 404 `ObjectNotFound`, 429 `LimitsExceeded`, and 400 `InvalidArgument`. Check credential expiry for 401, scopes/roles and resource access for 403, and the endpoint's body and filter contract for 400. A 404 may mean the supplied identifier is wrong for that API. Decide retries per operation; retrying a create after an ambiguous network failure can create another resource. The module does not make a sequence of changes atomic.

Stopping a REST command's pipeline requests cancellation of its HTTP operation. Browser authentication, token exchange, and Engine operations may not stop immediately. Do not include `RawContent`, token responses, or full tenant records in routine logs.

## Open an app in the Engine and close its session

`Invoke-OpenApp` returns a `Qlik.Engine.IApp`. It is a separate Engine session, not the REST app response. Use an actual app ID, such as `ResourceId` from an app item. `-NoData` and `-LogWS` are boolean parameters, so pass an explicit value.

```powershell
$engineApp = $null
$engineSession = $null
try {
    $engineApp = Invoke-OpenApp -Connection $connection -AppId $env:QLIK_APP_ID -NoData $true
    $engineSession = $engineApp.Session
    $layout = $engineApp.GetAppLayout()
    $layout | Select-Object Title, LastReloadTime, HasData, IsOpenedWithoutData
}
finally {
    try {
        if ($null -ne $engineApp) { $engineApp.Dispose() }
    }
    finally {
        if ($null -ne $engineSession) { $engineSession.Dispose() }
    }
}
```

Dispose app handles and the Engine session, then disconnect the REST connection after all its work is finished. Disconnecting REST does not replace the Engine session's lifecycle. OAuth Engine access performs token exchange internally. `Receive-QCToken -TokenPurpose Websocket` exposes that exchange for advanced callers; its returned access token is a secret and should not be printed.

Engine access depends on your identity's permissions for the selected app and its authentication flow. A successful REST read does not prove that the same connection can open an Engine session; check this separately with an app you can access.

## Finish a script

```powershell
Disconnect-QlikCloud -Connection $connection
```

For unattended work, wrap the whole connected part of the script in `try`/`finally`, put disconnection in `finally`, and let an unhandled terminating error fail the job. Keep the exact resource IDs returned by creations, distinguish first-page exports from complete exports, and check the included command inventory when adapting examples to another API.
