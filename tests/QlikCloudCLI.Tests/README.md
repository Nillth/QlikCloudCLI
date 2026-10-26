# QlikCloudCLI acceptance tests

Run these against a staged or extracted QlikCloudCLI module. The runner starts a clean, noninteractive PowerShell process; Pester and tenant credentials are not required. All acceptance cases run under Windows PowerShell 5.1 and PowerShell 7; neither host skips cases.

Lifecycle cases exercise deprecated GET operations and model constructors present in the installed catalog. They verify warning records, continued HTTP dispatch with the default warning behavior, `-WarningAction Stop` before HTTP, and quiet active operations. Generator fixtures cover deprecation metadata and selected parameter-set placement independently of the live schema inventory.

```powershell
powershell.exe -NoProfile -File .\tests\QlikCloudCLI.Tests\Invoke-AcceptanceTests.ps1 `
    -ModulePath 'C:\path\to\module\QlikCloudCLI\QlikCloudCLI.psd1'
```

By default the clean worker uses the same host as the launching script. Select the other host explicitly with `-HostPath`, which accepts an executable name or full path. Use separate evidence paths when testing both:

```powershell
pwsh.exe -NoProfile -File .\tests\QlikCloudCLI.Tests\Invoke-AcceptanceTests.ps1 `
    -ModulePath 'C:\path\to\module\QlikCloudCLI\QlikCloudCLI.psd1' `
    -HostPath powershell.exe -OutputPath 'C:\test-results\powershell51.json'

pwsh.exe -NoProfile -File .\tests\QlikCloudCLI.Tests\Invoke-AcceptanceTests.ps1 `
    -ModulePath 'C:\path\to\module\QlikCloudCLI\QlikCloudCLI.psd1' `
    -HostPath pwsh.exe -OutputPath 'C:\test-results\powershell7.json'
```

The module must ship `commands.json` and `QlikCloud.operations.json`. An optional `-ClientManifestPath` overrides the latter. JSON evidence defaults to `%LOCALAPPDATA%\QlikCloudBuild\cli-acceptance\results.json`; use `-OutputPath` to choose another location. Evidence records the actual worker executable, PowerShell version/edition, loaded module hashes, and every case's result. A nonzero process exit indicates failure, including inability to import or compile the fixture.

The mock transport is compiled in the worker with `Add-Type`. Its C# 5-compatible source works with Windows PowerShell's built-in compiler. Windows PowerShell uses its .NET Framework assemblies and `netstandard.dll` facade; .NET Framework 4.8 supplies that facade. PowerShell 7 uses the host's reference assembly directory. No separate fixture build, development SDK or downloaded test framework is needed.

The tests verify the manifest's Windows PowerShell 5.1/Desktop and Core declarations and both assemblies' .NET Standard 2.0 targets, then check the full endpoint and model command inventory against exported PowerShell metadata. Mock HTTP tests exercise typed and raw responses, path and body pipelines, mandatory parameters, optional false/zero values, nested constructor JSON, copied input models, HTTP errors, `-WhatIf`, exact binary bytes, cancellation, explicit versus default connections, and runspace cleanup. The mock handler has no network fallback. An OAuth client-credentials case injects that handler into the SDK, verifies token request serialization and IdentityModel token parsing, then calls a generated REST command on the authenticated mock connection. A separate Engine case initializes an API-key location without opening a session. API-key connection setup is local; unsupported JWT and token exchange use only local failure paths. No tenant request or authenticated browser flow is performed.

The inventory assertion checks every route's `(commandName, parameterSetName)` mapping, exact exported command names, per-set requiredness, CLR parameter types, aliases, pipeline binding and output metadata. It checks command aggregates against the operation list, singleton `Default` sets, grouped `List` defaults and PowerShell's 32-set limit. Operation counts and distinct command counts are recorded separately; grouping never removes REST operation coverage.

Mock grouping cases exercise `Get-QCCollections` list and item dispatch, preserve list `-Id` filtering while `-CollectionId` selects an item, and test scalar and property ID pipelines. `Get-QCCollectionsItems` retains the mandatory parent collection and selects child detail with `-ItemId`; list false/zero values and each set's typed/raw response survive. Audits retain list `-Id` and use `-AuditId` for detail. Conflicting list/item arguments fail parameter binding before the mock receives any HTTP request. The tests do not promise mixed list/item pipeline records.

The inventory also enforces the `QC` noun prefix for every endpoint and model constructor. Only `Connect-QlikCloud` and `Disconnect-QlikCloud` retain the old prefix; legacy command aliases are not exported. Handwritten connection commands are `Get-QCConnection` and `Receive-QCToken`; `Get-QlikUserAgent` and `Invoke-OpenApp` keep their existing names. The 16 shared Engine commands are listed separately in `engine-commands.json`. The module and CLR namespaces remain `QlikCloudCLI` and `QlikCloud`.

`QlikCloudCLI/Build.ps1 -Test` also runs `tests/QlikEngine.Tests` in both supported
PowerShell editions. That suite opens actual SDK WebSocket sessions against a
loopback Engine fixture, verifies the common script contract and lifecycle, and
tests the selected Desktop/Core SDK dependencies. It makes no tenant calls.

This suite validates the package and real PowerShell binding behavior in the selected host. A passing result establishes compatibility for these exercised paths in that exact host, not for untested hosts, live Qlik tenants, browser OAuth or Engine sessions.

## Optional live checks

Run live checks only for a tenant and operations you are authorized to test. Supply `QLIK_TENANT`, `QLIK_API_KEY`, and, for OAuth, `QLIK_CLIENT_ID`, `QLIK_CLIENT_SECRET`, and `QLIK_OAUTH_SCOPE` through your process environment or secret provider. Do not paste credentials into scripts or saved command history.

```powershell
$modulePath = 'C:\path\to\module\QlikCloudCLI\QlikCloudCLI.psd1'
pwsh -NoProfile -File .\tests\QlikCloudCLI.Tests\Invoke-LiveReadOnlyTests.ps1 `
    -ModulePath $modulePath
```

The read-only runner uses a hidden worker process with a 180-second overall limit (configurable with `-OverallTimeoutSeconds`). It requests OAuth tokens when client credentials are supplied and issues catalogued GETs for current user, users, spaces, items, app items, and discovered user/app IDs. API-key HTTP requests also have a 30-second timeout. OAuth has already used its HTTP client during connection, so that profile relies on the worker's overall bound. A denied request, skipped request, or failed deserialization is not a passed API check. Worker stdout/stderr are discarded; the report contains operation labels, status codes, types, counts, timing, and local binary hashes, without tenant records or credentials.

For a separately authorized write/delete test:

```powershell
pwsh -NoProfile -File .\tests\QlikCloudCLI.Tests\Invoke-LiveCollectionTests.ps1 `
    -ModulePath $modulePath
```

This API-key test creates one GUID-named private collection, reads it, lists it by name, changes its description with PUT and PATCH, then deletes it and verifies GET returns 404. Cleanup uses only the ID returned by its creation and checks the name, private type, and zero item count again. It never searches for deletion candidates or edits existing collections. HTTP requests use a 30-second timeout; there is no forced worker termination that would skip its `finally` cleanup. If creation has an ambiguous outcome, it does not automatically retry or guess a cleanup target. If cleanup fails, its report retains only the new test resource's ID for investigation.

Reports default to `%LOCALAPPDATA%\QlikCloudBuild\live-read-only\results.json` and `%LOCALAPPDATA%\QlikCloudBuild\live-collection\results.json`. Both runners accept `-OutputPath` and return a nonzero exit code on failure. The read runner accepts absent credentials for an intentionally omitted authentication profile and labels it skipped. A read test's success proves the selected reads under the supplied identity, not permission for every API. Neither runner tests browser OAuth, Engine sessions, uploads, downloads, or tenant-wide changes.

Live checks are deliberately excluded from public CI and release gates. Run them only when their tenant access is separately authorized; keep local reports and credentials outside the repository.
