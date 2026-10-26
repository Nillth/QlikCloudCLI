# QlikCloudCLI

Use Qlik Cloud from PowerShell with typed requests and responses. The module includes REST commands, request-model constructors, connection management, and Engine commands. Related list/detail requests share one command with separate parameter sets. The included `COMMANDS.md` and `commands.json` describe the REST surface; `engine-commands.json` lists the shared Engine commands.

REST and request-model command nouns begin with `QC`, for example `Get-QCApiKeys` and `New-QCUsersUserPostSchemaObject`. Connection setup and cleanup remain `Connect-QlikCloud` and `Disconnect-QlikCloud`; use `Get-QCConnection` and `Receive-QCToken` for connection lookup and token exchange. `Get-QlikUserAgent` and `Invoke-OpenApp` retain their names. The module name remains `QlikCloudCLI`.

Use `Get-QCCollections` to list collections and `Get-QCCollections -CollectionId $collectionId` to read one. Nested resources keep their own commands: `Get-QCCollectionsItems -CollectionId $collectionId` lists that collection's items; adding `-ItemId $itemId` reads one item. `Get-Command <name> -Syntax` shows the available parameter sets.

Engine commands use the same `QSE` names as the Engine-enabled QlikSenseCLI module: `Open-QSEApp`, `Get-QSEScript`, `Get-QSESheetList`, and `Close-QSEApp`, for example. After connecting to either platform, the same inspection script can use these commands. See the [Engine guide](HOW-TO.md#open-an-app-in-the-engine-and-close-its-session) for the full surface, native SDK access, and cleanup. Both installed modules must include this shared Engine feature; older releases do not export these commands.

Namespaced APIs have distinct command nouns, such as `QCAnalyticsApps` and `QCWorkflowsAutomations`. Existing commands such as `QCApps` remain available. Check the exact route and request model in `commands.json` when choosing between them.

Deprecated commands remain available while Qlik publishes their definitions and write a warning when used. The catalog marks deprecated operations and request models. Review the [release notes](https://github.com/Nillth/QlikCloudCLI/releases) for added, deprecated, and removed APIs before upgrading; the [guide](HOW-TO.md#deprecated-and-removed-apis) explains warnings and migration.

## Install from the PowerShell Gallery

Install a published release from PSGallery for your Windows user:

```powershell
Install-Module -Name QlikCloudCLI -Repository PSGallery -Scope CurrentUser
Import-Module QlikCloudCLI -ErrorAction Stop
```

See [HOW-TO.md](HOW-TO.md#install-from-the-powershell-gallery) for Gallery upgrades. Gallery and ZIP installations contain the same compiled module and bundled dependencies.

## Extract and import a ZIP

Use **Windows PowerShell 5.1**, included with Windows. Start `powershell.exe`; no additional PowerShell installation is required. The module's dependencies are bundled in the ZIP.

For a package from a source you trust, run `Unblock-File -LiteralPath $zipPath -ErrorAction Stop` on the downloaded ZIP before extracting it. The [extraction instructions](HOW-TO.md#extract-the-zip-and-import-the-module) show the full sequence and recovery for an already extracted package. Keep the top-level `QlikCloudCLI` folder and all its contents, including `bin`, together. From the directory containing that folder:

```powershell
Import-Module .\QlikCloudCLI\QlikCloudCLI.psd1 -ErrorAction Stop
Get-Command -Module QlikCloudCLI
```

You can also import the manifest using its full path. For installation on your module search path, copy the entire `QlikCloudCLI` folder into a directory listed in `$env:PSModulePath`, then use `Import-Module QlikCloudCLI`. See [HOW-TO.md](HOW-TO.md) for installation details.

After replacing a module version, start a fresh `powershell.exe` session before importing it; loaded .NET assemblies remain in the current process.

For a signed release, you can also [check the manifest and module DLL signatures](HOW-TO.md#optional-signature-check) with `Get-AuthenticodeSignature`. Verify the publisher's identity and a `Valid` status on your machine.

## Make your first request

Use your tenant hostname or base URL, without `/api/v1`. This example prompts for an API key without displaying it, lists up to five apps visible to that key, and closes the connection afterward. For automation, obtain the key from your secret provider instead of embedding it in the script.

```powershell
$tenant = Read-Host 'Tenant hostname or URL'
$secureApiKey = Read-Host 'API key' -AsSecureString
$apiKey = ([pscredential]::new('QlikCloudAPIKey', $secureApiKey)).GetNetworkCredential().Password
$connection = $null

try {
    $connection = Connect-QlikCloud -Tenant $tenant -APIKey $apiKey `
        -PassThru -ErrorAction Stop

    $page = Get-QCItems -Connection $connection `
        -ResourceType App -Limit 5 -ErrorAction Stop
    $page.Data | Select-Object Name, ResourceType
}
finally {
    if ($null -ne $connection) {
        Disconnect-QlikCloud -Connection $connection -Confirm:$false
    }
    $apiKey = $null
    $secureApiKey.Dispose()
    $secureApiKey = $null
}
```

`-PassThru` returns an explicit connection without changing the current runspace's default. The first API request checks whether the credential is valid and permitted to read that resource. OAuth connection examples are in [HOW-TO.md](HOW-TO.md).

## Find commands and examples

```powershell
Get-Command Get-QCItems -Syntax
Get-Command New-QCUsers*Object
```

- [HOW-TO.md](HOW-TO.md): authentication, requests, model objects, files, pagination, and connection cleanup.
- [COMMANDS.md](COMMANDS.md): complete command and REST route index.
- [commands.json](commands.json): machine-readable command, parameter-set, route, and output metadata.
- [engine-commands.json](engine-commands.json): shared Engine command names and SDK version.
- [LLM.txt](LLM.txt): guidance for AI assistants and script authors using this package.

REST commands normally return typed results; `-RawResponse` exposes the response wrapper. Model constructors accept JSON or typed properties. Mutating REST commands support `-WhatIf` and `-Confirm`.

Pagination is explicit: a list command returns the requested page, not every page. Available APIs and resources depend on your credential's permissions. JWT authentication is unsupported.

The module's original source is MIT licensed. [Third-party notices](THIRD-PARTY-NOTICES.md) describe the separate upstream API and dependency terms.
