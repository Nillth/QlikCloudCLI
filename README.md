# QlikCloudCLI

QlikCloudCLI is a PowerShell module for Qlik Cloud REST APIs and app Engine access. It provides typed requests and responses, request-model constructors, and native PowerShell parameter sets for related list/detail operations.

The compiled module supports Windows PowerShell 5.1, included with Windows, and PowerShell 7. Both project assemblies target .NET Standard 2.0. Dependencies are bundled with the module; customers do not need a .NET SDK or a source checkout.

```powershell
Import-Module QlikCloudCLI
Get-Command Get-QCCollections -Syntax

# After connecting to your tenant:
Get-QCCollections
Get-QCCollections -CollectionId $collectionId
```

Use the [operator guide](QlikCloudCLI/docs/consumer/HOW-TO.md) for installation, authentication, pagination, files, and request bodies. [LLM.txt](QlikCloudCLI/docs/consumer/LLM.txt) explains how script-writing assistants should use the installed command catalog. Every release includes its own `README.md`, `HOW-TO.md`, `LLM.txt`, `COMMANDS.md`, and `commands.json`.

## Install

Published releases can be installed from the PowerShell Gallery:

```powershell
Install-Module -Name QlikCloudCLI -Repository PSGallery -Scope CurrentUser
Import-Module QlikCloudCLI
```

ZIP packages can also be installed without PowerShellGet. Unblock a trusted downloaded ZIP **before** extracting it, then import the enclosed `QlikCloudCLI.psd1`. Keep its entire folder, including `bin`, together. See the [ZIP instructions](QlikCloudCLI/docs/consumer/HOW-TO.md#extract-the-zip-and-import-the-module).

## Build and maintain

Maintainers use PowerShell 7.4 or newer and a supported .NET SDK selected by `global.json`. A Windows build verifies the module in both Windows PowerShell 5.1 and PowerShell 7:

```powershell
pwsh -NoProfile -File ./QlikCloudCLI/Build.ps1 -Configuration Release -Test -Package
```

`QlikCloud/` contains the runtime SDK, `QlikCloudCLI/` contains the PowerShell module, and `tools/` contains the two C# generators. `schemas/qlik-cloud/` is the saved OpenAPI input. Normal builds generate from that snapshot without contacting a Qlik tenant or downloading replacement schemas. Fody/Costura embedding is disabled; dependency DLLs are staged beside the project DLLs.

The weekly workflow checks Qlik's published OpenAPI schemas. Schema changes must pass generation, generator tests, operation-coverage checks, and both PowerShell acceptance suites before an automatic release. An unchanged snapshot produces no new version. Publishing requires the repository's release credentials and signing policy to be configured.

See [maintainer instructions](docs/MAINTAINING.md) for refreshing schemas, release configuration, versioning, and retrying a partial publication. Live tenant tests are optional and never run as part of public CI.

## License and upstream content

Original QlikCloudCLI source is licensed under [MIT](LICENSE). Qlik's OpenAPI descriptions and third-party libraries retain their own rights and terms; see [third-party notices](THIRD-PARTY-NOTICES.md). This is a community project, not a Qlik-supported product.
