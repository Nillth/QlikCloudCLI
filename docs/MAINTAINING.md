# Maintaining QlikCloudCLI

## Development requirements

Use Windows, PowerShell 7.4 or newer, and .NET SDK 8.0.100 or a later 8.0 feature band permitted by `global.json`. Windows PowerShell 5.1 must remain installed for compatibility checks. These are maintainer requirements: consumers install the compiled module and its bundled dependencies.

The supported build path is the custom C# SDK generator, the custom C# PowerShell generator, compilation, and module staging. NSwag, retired PowerShell code generators, and Fody/Costura are not part of this path. Generated clients, generated command classes, build output, and release archives are not source files to commit.

## Build and test

From the repository root:

```powershell
pwsh -NoProfile -File ./scripts/Test-Release.ps1 -ArtifactsPath ./artifacts/validation
```

This runs the automation and C# generator tests, builds both assemblies, runs module acceptance tests in clean Windows PowerShell 5.1 and PowerShell 7 processes, and packages the tested module. The acceptance tests use an in-memory HTTP handler; they do not require a Qlik tenant or credentials.

For a module build without the generator test suites:

```powershell
pwsh -NoProfile -File ./QlikCloudCLI/Build.ps1 -Configuration Release -Test -Package
```

The build prints the manifest and ZIP locations. `-ArtifactsPath` and `-PackageDirectory` select output directories; `-ResultPath` writes a machine-readable build result. Use fresh PowerShell processes when testing a rebuilt module because loaded assemblies cannot be replaced in an existing process.

`version.json` is the version source. Its stable `major.minor.patch` value is used for the module manifest and both assemblies. `Build.ps1 -Version` can override it for a local candidate without rewriting the source file. The Gallery cannot replace an already published version.

## Refresh schemas

Normal compilation uses the saved snapshot and does not download schemas. To refresh deliberately:

```powershell
dotnet run --project ./tools/QlikCloud.Generator -c Release -- refresh --output ./schemas/qlik-cloud
pwsh -NoProfile -File ./scripts/Test-Release.ps1 -ArtifactsPath ./artifacts/schema-validation
```

Refresh downloads the official tenant REST OpenAPI ZIP. It preserves namespace directories and existing API identities, including `.well-known`. Review the schema documents and `manifest.json` together. Compare normalized schema contents, not ZIP bytes: repackaging the same documents must not create a release. Compatibility corrections belong in `SchemaTransforms.cs`, with focused tests. Grouping and route-name exceptions belong in the PowerShell generator's rules. Do not edit generated C# output to repair a schema change.

Schema snapshots remain upstream Qlik content. Keep their provenance and [third-party notices](../THIRD-PARTY-NOTICES.md) intact. Dependency updates also require reviewing the package's license and notice changes.

## GitHub Actions

`ci.yml` builds and tests pushes, pull requests, and manual dispatches on a hosted Windows runner with read-only repository permissions. It has no Gallery secret and does not sign or publish packages. Manual dispatch can validate the saved snapshot after an automated release commit, because commits pushed with the workflow token do not trigger another push workflow.

`schema-release.yml` checks the tenant REST ZIP for schema changes each Monday at 03:23 UTC and supports manual dispatch. Its inventory includes nested namespace paths, so APIs such as `apps` and `analytics/apps` remain distinct. Releases run only for `Nillth/QlikCloudCLI` on `main`, with one release run at a time. An unchanged snapshot creates no version, tag, or package publication. Changed snapshots must pass all release checks before the patch version is advanced.

GitHub can delay scheduled runs and automatically disables schedules in public repositories after 60 days without repository activity. Check the workflow's status after a quiet period and re-enable it if necessary; manual dispatch remains available. See [GitHub's schedule behavior](https://docs.github.com/en/actions/reference/workflows-and-actions/events-that-trigger-workflows#schedule).

The automatic compatibility check stops when an API document or HTTP operation disappears. Generator and PowerShell metadata checks catch unsupported schema shapes, ambiguous command grouping, type mismatches, and invalid binding. These checks do not replace review of upstream API behavior or permission changes. Treat a failed weekly run as an item to investigate; do not bypass the checks to obtain a release.

After validation, automation checks the module manifest and creates its Gallery `.nupkg` with the pinned PSResourceGet publication client. It commits the candidate snapshot, version, and release state, tags the candidate, and stores the tested ZIP, `.nupkg`, hashes, and provenance in a draft GitHub Release. It publishes that exact `.nupkg` to PSGallery, verifies the downloaded module files against the candidate, then makes the GitHub Release public. A retry resumes an existing candidate instead of assigning another version or building different bytes for that version.

### Repository setup

Create the `powershell-gallery` GitHub environment, then configure its secrets and the repository variables before enabling publication:

| Setting | Purpose |
| --- | --- |
| `PSGALLERY_API_KEY` environment secret | Gallery API key with permission to publish QlikCloudCLI. Never place it in a file or workflow input. |
| `SIGNING_URL` environment secret | Full HTTPS signing endpoint URL, required for `Authenticode` releases. |
| `SIGNING_API_TOKEN` environment secret | Bearer token for the signing endpoint, required for `Authenticode` releases. |
| `QLIKCLOUD_SIGNING_MODE` repository variable | Explicit release policy: `Authenticode` or `Unsigned`. A missing value blocks a release candidate. |

`Authenticode` sends each file to the configured HTTPS endpoint as a binary POST with bearer authentication and a filename header. The endpoint returns the signed file. Keep the endpoint URL and token in GitHub environment secrets; do not put them in source files or workflow inputs.

The build signs `QlikCloud.dll`, `QlikCloudCLI.dll`, and the module manifest, then verifies valid, timestamped signatures from the same publisher before hashing and packaging. Vendor DLLs retain their original signatures. Signing secrets are available only to the release job; pull-request builds have no signing access.

Choose `Unsigned` only when the release owner has explicitly approved unsigned distribution. The workflow must not silently fall back to unsigned files if signing fails. For a local signed build, supply `SIGNING_URL` and `SIGNING_API_TOKEN` through the process environment and run `Build.ps1 -Sign -Test -Package`.

Allow the release workflow to create commits, tags, and GitHub Releases on `main`; configure branch rules to permit the chosen automation identity. Keep the Gallery secret limited to the release job. If an environment requires a human approval, weekly publication will wait for that approval rather than proceed unattended.

### First release and recovery

The initial source version is `2.0.0`. After configuration, manually dispatch `schema-release.yml` with `publish_initial` enabled to publish the current version even when Qlik's schemas have not changed.

If a run fails after creating a candidate, preserve its tag, draft release, and state. Correct the external configuration and rerun the workflow; the existing package is the retry source. Do not delete a tag, bump the version, or replace a ZIP merely to retry Gallery publication. If a Gallery version exists but does not match the release state, stop and investigate rather than overwrite it.

For a breaking schema update or a deliberate API/command change, review the change explicitly and decide the release version and publication plan separately. The weekly path only makes patch releases for accepted schema changes; `publish_initial` is not a general force-release switch. Keep release notes clear about command, parameter, type, and behavior changes.

## Optional live validation

The scripts under `tests/QlikCloudCLI.Tests/` include opt-in tenant checks. They are separate from CI because they require tenant-specific permissions and may create disposable resources. Read their [test instructions](../tests/QlikCloudCLI.Tests/README.md), obtain authorization for the intended operations, and supply credentials through the process environment or a secret provider. Do not commit tenant data, credentials, or local validation reports.
