# Qlik Cloud schema snapshot

`qlik-cloud/` contains the saved public REST OpenAPI documents downloaded from Qlik. `manifest.json` identifies the documents and their hashes. The file count changes as Qlik adds or removes APIs; the manifest is the authority for the current snapshot.

This directory is the default offline input for every `QlikCloud/QlikCloud.csproj` build. The manifest records the generator identity and document hashes. The generator's compatibility rules determine which API groups are emitted; schema updates do not happen implicitly during compilation.

Refresh the full snapshot from the repository root:

```powershell
dotnet run --project .\tools\QlikCloud.Generator -c Release -- refresh --output .\schemas\qlik-cloud
```

Review schema and manifest changes together, then run the generator tests and library build. Use another directory for selected-API experiments so the canonical input retains the complete catalog.

The downloader reads Qlik's public [tenant REST OpenAPI ZIP](https://qlik.dev/specs/rest/qlik-cloud-rest-openapi-specs.zip). Namespace directories are preserved: `apps.json` and `analytics/apps.json` are distinct APIs. The archive's `well-known.json` is saved as `.well-known.json` to preserve its existing client identity. The root `manifest.json` records each relative API identity and normalized schema hash; ZIP timestamps and compression do not affect change detection. No tenant credentials or tenant data are stored here. Organization API specifications are a separate catalog and are not included.

These documents remain Qlik's upstream material, outside this project's MIT license. See [third-party notices](../THIRD-PARTY-NOTICES.md) for source attribution and applicable upstream links.
