# QlikCloudCLI.Generator

This .NET 8 tool generates the binary PowerShell command layer from the **compiled QlikCloud.dll** and the operation manifest emitted when that DLL was built. It reads assembly metadata with `MetadataLoadContext`; inspecting the SDK does not execute its code. It does not regenerate HTTP transport, rediscover OpenAPI schemas, or contact a Qlik tenant.

The normal QlikCloudCLI build invokes this tool before compiling the module. For an independent generation or drift check:

```powershell
dotnet run --project .\tools\QlikCloudCLI.Generator -c Release -- generate `
  --assembly <path-to-QlikCloud.dll> `
  --client-manifest <path-to-QlikCloud.operations.json> `
  --output <dedicated-intermediate-directory>

# Run the same command with --check to verify exact existing output bytes.
```

`--client-manifest` also accepts the SDK generator's original `manifest.json`. The SDK's dependency assemblies must be beside its DLL or resolvable from the .NET runtime. `--rules` optionally selects another complete command-rules file; the default is the shipped `command-rules.json` beside the tool. All paths supplied on the command line resolve relative to the caller's current directory.

The tool emits:

- `src/*.cs`: one typed endpoint command per supported operation group and one constructor per reachable request model.
- `commands.json` (format version 2): distinct command groups, each route's command and parameter set, SDK bindings, typed parameters, mandatory and pipeline metadata, aliases, output types, constructor models, and input hashes.
- `COMMANDS.md`: endpoint and constructor reference with request/output conventions.
- `manifest.json`: ownership marker, input/tool hashes, counts, and output hashes.

Generator output records the unsigned input assembly hash in `commands.json` as `assemblySha256`. If module staging later signs the delivered SDK, the staged catalog preserves that original value as `unsignedAssemblySha256` and sets `assemblySha256` to the delivered signed `QlikCloud.dll` hash. Verify installed bytes against `assemblySha256`; the unsigned field retains generation provenance. This staging update does not rename or rewrite the generator's input files.

Command and constructor counts are discovered from the SDK inputs. Read `commands.json` for the inventory belonging to a particular build; schema refreshes can change these counts. Each included operation must have a command binding, including custom operations recorded by the SDK. API exclusions are explicit SDK generation policy rather than missing bindings in this tool.

Catalog `operations` retains every operation with a `parameterSetName`; `commandName` is therefore not unique in this array. Select operations by HTTP method and exact path. The separate `commands` array contains `commandName`, `defaultParameterSetName`, `parameterSetNames`, and `operationCount`, once per distinct endpoint command. `commandCount` counts those REST commands, excluding model constructors and handwritten runtime commands.

## Naming and binding

Endpoint nouns begin with `QC`, retain API context, and derive from literal route segments after `/api/vN`. Whole placeholder segments are omitted. Examples are `Get-QCUsers`, `Get-QCUsersMe`, and `Get-QCCollectionsItems`. GET maps to `Get`, root POST to `New`, other POST operations to `Invoke`, PUT to `Set`, PATCH to `Update`, and DELETE to `Remove`. HEAD and OPTIONS use distinct `Get...Headers` and `Get...Options` names. Every mutation command advertises `SupportsShouldProcess`.

When two GET routes have the same natural command name and one is exactly the other plus a final `/{id}` segment, generation combines them into `List` (default) and `By<PublicId>` sets. `Get-QCCollections` therefore accepts list filters in `List` or mandatory `CollectionId` in `ByCollectionId`. `Get-QCCollectionsItems` keeps the parent `CollectionId` mandatory in both sets and requires `ItemId` only in `ByItemId`. A singleton uses `Default`. Other collisions, incompatible types for a shared public parameter, ambiguous route selection, and more than 32 parameter sets fail generation. Nested resources retain separate commands.

Rules can explicitly rename an operation or its public parameter with a required explanation; stale, malformed, and unused rules fail validation. Explicit detail names resolve `Get-QCAppsReportFiltersById`, `Get-QCDirectAccessAgentsConnectorsFilesByFileType`, `Get-QCSharingTasksExecutionsByExecutionId`, and `Invoke-QCDataFilesActionsChangeSpaceById`. The audits item path parameter is exposed as `AuditId`, leaving the list query filter named `Id`. An alias is omitted when it conflicts with a real parameter in the group, and catalog metadata records that omission.

The handwritten runtime provides `Get-QCConnection`, `Receive-QCToken`, `Connect-QlikCloud`, `Disconnect-QlikCloud`, `Get-QlikUserAgent`, and legacy `Invoke-OpenApp`, plus the 16 shared Engine commands listed in `shared/Qlik.Engine.PowerShell/commands.json`. Engine commands are compiled from shared source, independently of REST schema generation. Generated route grouping does not change model or assembly namespaces.

SDK overloads bind using the manifest's method name and route parameter names, preferring required path parameters over optional query filters. Ambiguities fail generation unless an explicit rule identifies the signature. Existing rules select the generated Apps export operation over file-download convenience overloads and select the full identity-provider union operation over JWT-only/SAML-only overloads. Rules require explanations and fail when they stop matching.

Endpoint classes expose a union of compatible typed properties with separate parameter attributes for each set. `ProcessRecord` dispatches by the selected `ParameterSetName` and calls that operation's typed SDK async method through `QlikCloudCLI.Runtime.PSApiCmdlet.ExecuteApi`. This binds fresh pipeline values for each record and preserves SDK request encoding. Required path parameters bind by property name; a sole path parameter also binds by value, with an `Id` alias where unambiguous. Typed non-string body parameters bind by value. Optional SDK nullable values remain nullable, preserving explicitly supplied `false` and `0`. Common PowerShell and runtime parameter names are reserved and receive a `Parameter` suffix when a schema field conflicts.

Constructors use `New-QC<Api><Type>Object`, preventing collisions between same-named models in different API namespaces. Discovery follows endpoint parameter types through arrays, generic arguments, model properties, and constructors. Explicit roots preserve the JWT and SAML payloads behind the identity-provider `object` union. Arbitrary `object` unions remain available through object/hashtable/JSON values; the metadata cannot infer every schema union branch from a CLR `object` parameter.

Normal constructors support named properties, `-Json`, and typed `-InputObject`. They clone input models before applying explicitly bound overrides. Collection models provide `-Items`. FileParameter constructors accept `-Data` as an open stream and optional `-FileName`/`-ContentType`; copying a FileParameter creates another wrapper around the same stream. Constructor commands do not enforce every server-side schema constraint.

## Runtime contract

Generated endpoints derive from `QlikCloudCLI.Runtime.PSApiCmdlet`, which supplies `Connection`, `RawResponse`, and:

```csharp
protected void ExecuteApi(
    Func<CancellationToken, Task<object>> operation,
    string target, string action, bool modifiesResource);
```

The runtime resolves the connection, handles `ShouldProcess`, cancellation and errors, and unwraps `APIResponse<T>.Object` unless `-RawResponse` was requested. Binary downloads remain typed `FileResponse` instances; callers must dispose them after reading. No automatic paging or file-path download/upload convenience is inferred.

Generated constructors derive from `PSModelConstructor<T>`, using `ConvertModelFromJson`, `ConvertModelFromInputObject`, and `IsParameterBound`. Generated collection output is written as one model object.

## Ownership and validation

Output must be a dedicated directory. `SafeOutput` requires `generator: QlikCloudCLI.Generator`, holds a cross-process lease, stages complete output, replaces transactionally, rejects linked/unowned paths, and detects changed, missing, or stale files in `--check` mode. The SDK generator's ownership marker is deliberately different. No output directory is adopted merely because it contains C# files.

Run the isolated synthetic-SDK tests from the repository root:

```powershell
dotnet test .\tests\QlikCloudCLI.Generator.Tests\QlikCloudCLI.Generator.Tests.csproj
```

These tests cover metadata/overload coverage, custom operations, transitive constructors, deterministic bytes, nonmutating drift checks, stale source removal, failed binding rollback, invalid/unused policy, naming collisions, and refusal to overwrite unowned output. They require no tenant or previously built production SDK. The separate `tests/QlikCloudCLI.Tests` acceptance harness validates the built module and mock HTTP behavior.
