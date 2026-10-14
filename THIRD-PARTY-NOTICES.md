# Third-party notices

The repository's MIT license applies to original QlikCloudCLI source. It does not relicense Qlik API descriptions, dependency assemblies, or other upstream material. Retain this file and the `licenses/` directory when redistributing a module package.

## Qlik OpenAPI specifications

`schemas/qlik-cloud/*.json` contains Qlik's published REST OpenAPI documents, with `manifest.json` recording their source identity and hashes. They are downloaded from `https://qlik.dev/specs/rest/{api}.json`; discovery uses Qlik's public sitemap. The original documents remain unchanged on disk. Compatibility corrections are applied in memory by the generator.

Qlik retains its rights in its APIs and documentation. The checked-in documents do not declare an `info.license` or `termsOfService` field. This project does not claim an MIT license or additional redistribution grant for those documents. Refer to Qlik's [REST API documentation](https://qlik.dev/apis/rest/), [API policy](https://qlik.dev/apis/api-policy/), and [Developer Portal Terms of Use](https://qlik.dev/qlik-developer-portal-terms-of-use.pdf) for the applicable upstream terms. Generated API names, descriptions, and model documentation originate from those specifications.

Qlik and Qlik Sense are trademarks of their respective owners. Inclusion of these interfaces does not imply Qlik endorsement or support.

## Runtime dependencies

The following licenses are declared by the NuGet packages used by this module. Package versions are controlled by the project files and dependency resolution; the table describes the current dependency families rather than promising a fixed release inventory.

| Component | Upstream attribution | License |
| --- | --- | --- |
| QlikSense.NetSDK | Copyright ©2016–2025 QlikTech International AB | MIT; [license text](licenses/QlikSense.NetSDK-MIT.txt) |
| Newtonsoft.Json | Copyright © James Newton-King 2008 | MIT; [upstream text](licenses/Newtonsoft.Json-LICENSE.md) |
| IdentityModel and IdentityModel.OidcClient | Dominick Baier and Brock Allen; see upstream package metadata | Apache-2.0; [license text](licenses/Apache-2.0.txt) |
| Serilog, Serilog.Extensions.Logging, Serilog.Sinks.Console, Serilog.Sinks.File | Serilog Contributors | Apache-2.0; [license text](licenses/Apache-2.0.txt) |
| Microsoft.Extensions.*, Microsoft.Bcl.AsyncInterfaces, and modern System.* support packages | Microsoft Corporation and .NET contributors | MIT; [license text](licenses/dotnet-MIT.txt) and [third-party notices](licenses/dotnet-THIRD-PARTY-NOTICES.TXT) |
| System.Console 4.3.1 and related legacy .NET support packages | Microsoft Corporation | [Microsoft .NET Library license](licenses/dotnet-library-license.txt) and [third-party notices](licenses/dotnet-library-ThirdPartyNotices.txt) |

Dependency assemblies are distributed without modification. Their names, versions, copyright metadata, and original NuGet packages identify their upstream sources. NuGet package metadata is authoritative if a dependency is updated. Review license and notice changes alongside dependency updates.

Build and test dependencies are restored from NuGet and are not automatically shipped with the PowerShell module. Their own package terms continue to apply. The C# generator design was informed by the earlier QlikSenseCLI generator workflow; this repository does not grant rights to a separate QlikSenseCLI repository or to Qlik's proprietary material.
