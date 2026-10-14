# Qlik Cloud schema snapshot

`qlik-cloud/` contains the saved public REST OpenAPI documents downloaded from Qlik. `manifest.json` identifies the documents and their hashes. The file count changes as Qlik adds or removes APIs; the manifest is the authority for the current snapshot.

This directory is the default offline input for every `QlikCloud/QlikCloud.csproj` build. The manifest records the generator identity and document hashes. The generator's compatibility rules determine which API groups are emitted; schema updates do not happen implicitly during compilation.

Refresh the full snapshot from the repository root:

```powershell
dotnet run --project .\tools\QlikCloud.Generator -c Release -- refresh --output .\schemas\qlik-cloud
```

Review schema and manifest changes together, then run the generator tests and library build. Use another directory for selected-API experiments so the canonical input retains the complete catalog.

The downloader discovers schemas through Qlik's public sitemap and downloads `https://qlik.dev/specs/rest/{api}.json`. The `pagination-sorting-filtering` and `rate-limiting` sitemap entries are guides, not OpenAPI documents, and are explicitly excluded by discovery. No tenant credentials or tenant data are stored here.

These documents remain Qlik's upstream material, outside this project's MIT license. See [third-party notices](../THIRD-PARTY-NOTICES.md) for source attribution and applicable upstream links.
