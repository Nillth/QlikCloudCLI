# QlikCloud.Generator

This .NET 8 command-line tool discovers public Qlik Cloud OpenAPI documents and generates the C# clients and models compiled into `QlikCloud.dll`. The generator and its templates are the only client-generation path. The SDK targets `netstandard2.0`; the generator's SDK requirement applies to maintainers, not module users.

Run the following commands from the repository root.

## Refresh and generate

```powershell
# Explicit network step: discover and download the complete public catalog.
dotnet run --project .\tools\QlikCloud.Generator -c Release -- refresh `
  --output .\schemas\qlik-cloud

# Generate from saved schemas without contacting Qlik.
dotnet run --project .\tools\QlikCloud.Generator -c Release -- generate `
  --schemas .\schemas\qlik-cloud `
  --output .\QlikCloud\obj\Generated\Manual\netstandard2.0

# Verify existing output without replacing it.
dotnet run --project .\tools\QlikCloud.Generator -c Release -- generate `
  --schemas .\schemas\qlik-cloud `
  --output .\QlikCloud\obj\Generated\Manual\netstandard2.0 --check
```

Refresh follows Qlik's sitemap indexes and REST API pages, then downloads `https://qlik.dev/specs/rest/{api}.json`. The `pagination-sorting-filtering` and `rate-limiting` pages are excluded because they are guides without schemas. Failed downloads, malformed schemas, empty discovery, and unmatched API selections fail the operation. The default discovery entry point is `https://qlik.dev/sitemap-index.xml`; `--sitemap <url>` overrides it.

Omit `--api` for the complete catalog. `--api users,apps` is useful for experiments, but use a separate schema directory: refresh replaces its destination with the complete selected set. The production SDK needs all APIs referenced by its custom code.

The checked-in `schemas/qlik-cloud` snapshot is the default build input. Refresh produces normalized JSON and content hashes without timestamps, so unchanged schema content produces no changes. Review schema diffs alongside build and test results. Newly introduced schema constructs may need a renderer correction before release.

Generation reads root-level schema JSON files, excluding `manifest.json`. `generation-rules.json` records API exclusions, custom operations, and explicit method names. `SchemaTransforms.cs` applies named corrections to a copy of each schema; downloaded documents remain unchanged. `--rules <file>` and `--templates <directory>` select alternate generator inputs.

## Build and test

```powershell
dotnet build .\QlikCloud\QlikCloud.csproj -c Release
dotnet test .\tests\QlikCloud.Generator.Tests\QlikCloud.Generator.Tests.csproj -c Release
```

`QlikCloud/BuildFiles/Generation.targets` generates clients beneath the intermediate output directory before compilation and copies the operation inventory beside the SDK as `QlikCloud.operations.json`. The module generator consumes that DLL and its matching inventory. Normal builds do not refresh schemas, mutate source versions, sign files, or publish packages. NuGet restore may download dependencies.

`QlikCloudSchemaDirectory` selects another complete offline snapshot. `QlikCloudGeneratedRoot` selects another location beneath the project's intermediate directory. Missing or invalid schemas and failed generation stop the build even when older generated files exist. IDE design-time builds use existing intermediate files; a normal build creates them on a fresh checkout.

```powershell
dotnet build .\QlikCloud\QlikCloud.csproj -c Release `
  -p:QlikCloudGeneratedRoot=obj\Generated\Review\netstandard2.0
```

The tests exercise real generated clients against an in-memory HTTP handler, schema correction fixtures, model serialization, deterministic output, and build integration. They need no Qlik tenant or credentials. For packaged-module compatibility checks, use the repository's module build instructions.

## Output ownership and schema policy

Output directories are dedicated to this tool. Existing output must contain a `manifest.json` identifying `QlikCloud.Generator`; unmarked or linked directories are rejected. A cross-process lease protects transactional replacement. Generated text uses UTF-8 and LF; manifests record schema, renderer, template, and output hashes. `--check` detects changed, missing, and stale files without replacing output. An adjacent lock file may remain after execution.

The renderer supports the Qlik Cloud schemas exercised by this repository. Unsupported or ambiguous shapes should receive a focused fixture and an explicit correction. Path declarations must match URL placeholders. For example, the apps reload-metadata policy corrects an upstream `reloadId` declared as a query parameter even though it appears in the route. It does not infer additional endpoints from descriptive text.
