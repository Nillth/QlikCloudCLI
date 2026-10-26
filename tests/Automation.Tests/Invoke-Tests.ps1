#requires -Version 7.4
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot '../../scripts/Release.Common.psm1') -Force
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('qlikcloud-automation-tests-' + [guid]::NewGuid().ToString('N'))
$null = [IO.Directory]::CreateDirectory($temporary)
$passed = 0
function Assert-Test([bool] $Condition, [string] $Message) { if (-not $Condition) { throw $Message }; $script:passed++ }
function Assert-Throws([scriptblock] $Action, [string] $Message) { $thrown = $false; try { & $Action } catch { $thrown = $true }; Assert-Test $thrown $Message }
function Save-Schema([string] $Directory, [string] $Name, [hashtable] $Paths, [hashtable] $Metadata = @{}) {
    $path = Join-Path $Directory ($Name + '.json')
    $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path))
    $schema = @{ openapi = '3.0.0'; paths = $Paths }
    foreach ($key in $Metadata.Keys) { $schema[$key] = $Metadata[$key] }
    Write-ReleaseJson $path $schema
}
function Save-SnapshotManifest([string] $Directory) {
    $inventory = Get-SchemaInventory $Directory
    $apis = @($inventory.Apis.Keys | Sort-Object -CaseSensitive | ForEach-Object { @{ api = $_; sha256 = $inventory.Apis[$_] } })
    $files = @($inventory.Apis.Keys | Sort-Object -CaseSensitive | ForEach-Object { @{ path = $_ + '.json'; sha256 = $inventory.Apis[$_] } })
    Write-ReleaseJson (Join-Path $Directory 'manifest.json') @{ generator = 'QlikCloud.Generator'; formatVersion = 1; command = 'refresh'; apis = $apis; files = $files }
}
function Get-SnapshotBytes([string] $Directory) {
    return @(Get-ChildItem -LiteralPath $Directory -File -Recurse | Sort-Object FullName | ForEach-Object { $_.FullName + ':' + (Get-FileHash -LiteralPath $_.FullName).Hash }) -join "`n"
}
try {
    $before = Join-Path $temporary 'before'
    $after = Join-Path $temporary 'after'
    Save-Schema $before 'widgets' @{ '/widgets' = @{ get = @{} }; '/widgets/{id}' = @{ get = @{}; delete = @{} } }
    Save-Schema $after 'widgets' @{ '/widgets' = @{ get = @{} }; '/widgets/{id}' = @{ get = @{}; delete = @{} } }
    $original = Get-SchemaInventory $before
    $same = Compare-SchemaInventory $original (Get-SchemaInventory $after)
    Assert-Test (-not $same.Changed) 'Identical snapshots must not trigger a release.'
    $samePath = Join-Path $after 'widgets.json'
    $sameText = Get-Content -LiteralPath $samePath -Raw
    [IO.File]::WriteAllText($samePath, $sameText.Replace("`r`n", "`n").Replace("`n", "`r`n"))
    Assert-Test (-not (Compare-SchemaInventory $original (Get-SchemaInventory $after)).Changed) 'Windows Git line endings must not trigger a release.'
    Write-ReleaseJson (Join-Path $after 'manifest.json') @{ diagnostic = 'Different metadata must not count as an API change.' }
    Assert-Test (-not (Compare-SchemaInventory $original (Get-SchemaInventory $after)).Changed) 'Manifest-only changes must not release.'
    Save-Schema $after 'gadgets' @{ '/gadgets' = @{ post = @{} } }
    $added = Compare-SchemaInventory $original (Get-SchemaInventory $after)
    Assert-Test ($added.Changed -and $added.AddedApis.Count -eq 1 -and $added.AddedRoutes.Count -eq 1) 'New API documents must be detected.'
    Save-Schema $after 'widgets' @{ '/widgets' = @{ get = @{} }; '/widgets/{id}' = @{ get = @{} } }
    $removed = Compare-SchemaInventory $original (Get-SchemaInventory $after)
    Assert-Test ($removed.RemovedRoutes.Count -eq 1 -and $removed.RemovedRoutes[0] -eq 'widgets DELETE /widgets/{id}') 'Removed method on an existing path must be detected.'
    $removedState = New-SchemaReleaseState -Version '2.2.1' -PreviousVersion '2.2.0' -Inventory (Get-SchemaInventory $after) -Difference $removed
    $removedNotes = Get-SchemaReleaseNotes $removedState 'fixture-commit'
    Assert-Test ($removedState.schemaChanges.removedOperations.Count -eq 1 -and $removedNotes.Contains('Removed HTTP operations:') -and $removedNotes.Contains('widgets DELETE /widgets/{id}')) 'Removed operations must produce release notes without blocking a candidate.'
    Assert-Test (-not (Get-Command Assert-CompatibleSchemaUpdate -ErrorAction SilentlyContinue)) 'The obsolete removal veto must not remain exported.'

    $deprecatedBefore = Join-Path $temporary 'deprecated-before'
    $deprecatedAfter = Join-Path $temporary 'deprecated-after'
    Save-Schema $deprecatedBefore 'widgets' @{
        '/widgets' = @{ get = @{}; post = @{ 'x-qlik-deprecated' = $true } }
        '/retired' = @{ 'x-qlik-deprecated' = $true; get = @{} }
    }
    Save-Schema $deprecatedAfter 'widgets' @{ '/widgets' = @{ get = @{} } }
    $deprecatedOriginal = Get-SchemaInventory $deprecatedBefore
    $retired = Compare-SchemaInventory $deprecatedOriginal (Get-SchemaInventory $deprecatedAfter)
    Assert-Test ($deprecatedOriginal.Routes.Count -eq 3 -and $deprecatedOriginal.DeprecatedRoutes.Count -eq 2) 'All raw routes must be inventoried, with separate inherited deprecation metadata.'
    Assert-Test ($retired.Changed -and $retired.ChangedApis.Count -eq 1 -and $retired.RemovedRoutes.Count -eq 2) 'Removed deprecated operations must remain visible in release notes.'
    Save-Schema $deprecatedAfter 'widgets' @{ '/widgets' = @{ get = @{ 'x-qlik-deprecated' = $true } } }
    $newlyDeprecated = Compare-SchemaInventory $deprecatedOriginal (Get-SchemaInventory $deprecatedAfter)
    Assert-Test ($newlyDeprecated.DeprecatedRoutes.Count -eq 1 -and $newlyDeprecated.DeprecatedRoutes[0] -ceq 'widgets GET /widgets') 'Newly deprecated operations must be identified separately.'
    Assert-Test ($newlyDeprecated.RemovedRoutes -cnotcontains 'widgets GET /widgets') 'Deprecation must not be misreported as removal.'
    Save-Schema $deprecatedAfter 'widgets' @{ '/widgets' = @{ 'x-qlik-deprecated' = $true; get = @{} } }
    Assert-Test ((Compare-SchemaInventory $deprecatedOriginal (Get-SchemaInventory $deprecatedAfter)).DeprecatedRoutes -ccontains 'widgets GET /widgets') 'Path-level deprecation must be inherited by its operations.'
    Save-Schema $deprecatedAfter 'gadgets' @{ '/gadgets' = @{ get = @{} } }
    Remove-Item -LiteralPath (Join-Path $deprecatedAfter 'widgets.json')
    $apiRemoved = Compare-SchemaInventory $deprecatedOriginal (Get-SchemaInventory $deprecatedAfter)
    $apiRemovedNotes = Get-SchemaReleaseNotes (New-SchemaReleaseState -Version '2.2.1' -PreviousVersion '2.2.0' -Inventory (Get-SchemaInventory $deprecatedAfter) -Difference $apiRemoved) 'fixture-commit'
    Assert-Test ($apiRemoved.RemovedApis -ccontains 'widgets' -and $apiRemovedNotes.Contains('Removed APIs:')) 'Removed API documents must be recorded without blocking release notes.'

    $retainedBefore = Join-Path $temporary 'retained-before'
    $retainedAfter = Join-Path $temporary 'retained-after'
    Save-Schema $retainedBefore 'widgets' @{
        '/false' = @{ get = @{ 'x-qlik-deprecated' = $false } }
        '/string' = @{ get = @{ 'x-qlik-deprecated' = 'true' } }
        '/number' = @{ get = @{ 'x-qlik-deprecated' = 1 } }
        '/null' = @{ get = @{ 'x-qlik-deprecated' = $null } }
        '/case' = @{ get = @{ 'X-QLIK-DEPRECATED' = $true } }
        '/standard' = @{ get = @{ deprecated = $true } }
        '/standard-string' = @{ get = @{ deprecated = 'true' } }
    }
    Save-Schema $retainedAfter 'widgets' @{}
    $retained = Compare-SchemaInventory (Get-SchemaInventory $retainedBefore) (Get-SchemaInventory $retainedAfter)
    Assert-Test ($retained.RemovedRoutes.Count -eq 7) 'Lifecycle inventory must retain every raw route, regardless of deprecation flags.'
    $strictFlags = Get-SchemaInventory $retainedBefore
    Assert-Test ($strictFlags.DeprecatedRoutes.Count -eq 1 -and $strictFlags.DeprecatedRoutes.Contains('widgets GET /standard')) 'Only exact lowercase keys with boolean true mark deprecation; standard deprecated is supported.'

    $lifecycleBefore = Join-Path $temporary 'lifecycle-before'
    $lifecycleAfter = Join-Path $temporary 'lifecycle-after'
    Save-Schema $lifecycleBefore 'document' @{ '/one' = @{ get = @{} } }
    Save-Schema $lifecycleBefore 'info' @{ '/two' = @{ get = @{} } }
    Save-Schema $lifecycleBefore 'paths' @{ '/three' = @{ get = @{} } }
    Save-Schema $lifecycleAfter 'document' @{ '/one' = @{ get = @{ deprecated = $false } } } @{ deprecated = $true }
    Save-Schema $lifecycleAfter 'info' @{ '/two' = @{ get = @{} } } @{ info = @{ 'x-qlik-deprecated' = $true } }
    Save-Schema $lifecycleAfter 'paths' @{ deprecated = $true; '/three' = @{ get = @{} } }
    Save-Schema $lifecycleAfter 'new-deprecated' @{ '/four' = @{ get = @{ deprecated = $true } } } @{ 'x-qlik-deprecated' = $true }
    $lifecycle = Compare-SchemaInventory (Get-SchemaInventory $lifecycleBefore) (Get-SchemaInventory $lifecycleAfter)
    Assert-Test ($lifecycle.DeprecatedApis.Count -eq 3 -and $lifecycle.DeprecatedRoutes.Count -eq 4 -and $lifecycle.RemovedRoutes.Count -eq 0) 'Document/info/paths flags must inherit; false descendants must not erase an ancestor flag.'
    Assert-Test ($lifecycle.AddedApis -ccontains 'new-deprecated' -and $lifecycle.AddedRoutes -ccontains 'new-deprecated GET /four') 'New deprecated APIs and operations must be recorded as both additions and deprecations.'
    $state = New-SchemaReleaseState -Version '2.2.0' -PreviousVersion '2.1.1' -Inventory (Get-SchemaInventory $lifecycleAfter) -Difference $lifecycle -ReleaseNotes @('Adds runtime warnings for deprecated commands.')
    $statePath = Join-Path $temporary 'candidate.json'
    Write-ReleaseJson $statePath $state
    $notes = Get-SchemaReleaseNotes $state 'fixture-commit'
    $resumedNotes = Get-SchemaReleaseNotes (Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json) 'fixture-commit'
    Assert-Test ($notes -ceq $resumedNotes -and $notes.Contains('Newly deprecated APIs:') -and $notes.Contains('Newly deprecated HTTP operations:') -and $notes.Contains('Adds runtime warnings for deprecated commands.')) 'Lifecycle and source-release notes must survive durable state serialization and resume.'
    $unchangedDeprecated = New-SchemaReleaseState -Version '2.2.1' -PreviousVersion '2.2.0' -Inventory (Get-SchemaInventory $lifecycleAfter) -Difference (Compare-SchemaInventory (Get-SchemaInventory $lifecycleAfter) (Get-SchemaInventory $lifecycleAfter))
    $unchangedDeprecatedNotes = Get-SchemaReleaseNotes $unchangedDeprecated 'fixture-commit'
    Assert-Test ($unchangedDeprecated.schemaChanges.deprecatedOperations.Count -eq 0 -and $unchangedDeprecated.schemaChanges.currentDeprecatedOperations.Count -eq 4 -and $unchangedDeprecatedNotes.Contains('Deprecated HTTP operations in this snapshot:')) 'Existing deprecated operations must remain recorded even when no flag was newly added.'
    Assert-Test ((Compare-SchemaInventory (Get-SchemaInventory $lifecycleAfter) (Get-SchemaInventory $lifecycleAfter)).DeprecatedRoutes.Count -eq 0) 'Unchanged deprecations must not repeat as newly deprecated operations.'
    $legacyNotes = Get-SchemaReleaseNotes ([pscustomobject]@{ formatVersion = 1; version = '2.1.1'; schemaDigest = ('1' * 64); changedApis = @('widgets') }) 'legacy-commit'
    Assert-Test ($legacyNotes.Contains('- widgets') -and $legacyNotes.Contains('legacy candidate format')) 'Old candidate states must remain resumable without invented lifecycle details.'
    $emptyNotes = Get-SchemaReleaseNotes (New-SchemaReleaseState -Version '2.2.0' -PreviousVersion '2.1.1' -Inventory $original -Difference $same -ReleaseNotes @('Source-only update.')) 'fixture-commit'
    Assert-Test ($emptyNotes.Contains('Source-only update.') -and $emptyNotes.Contains('No schema content changes') -and -not $emptyNotes.Contains('Initial public release')) 'A source-only release must not be mislabelled as an initial schema release.'
    Assert-Throws { Get-SchemaReleaseNotes ([pscustomobject]@{ formatVersion = 2; version = '2.2.0'; schemaDigest = ('1' * 64) }) 'fixture-commit' } 'Malformed new candidate lifecycle state must still fail.'

    $excludedApiBefore = Join-Path $temporary 'excluded-api-before'
    $excludedApiAfter = Join-Path $temporary 'excluded-api-after'
    Save-Schema $excludedApiBefore 'retired' @{ '/retired' = @{ get = @{ 'x-qlik-deprecated' = $true } } }
    Save-Schema $excludedApiAfter 'widgets' @{ '/widgets' = @{ get = @{} } }
    $excludedApi = Compare-SchemaInventory (Get-SchemaInventory $excludedApiBefore) (Get-SchemaInventory $excludedApiAfter)
    Assert-Test ($excludedApi.RemovedApis.Count -eq 1 -and $excludedApi.RemovedRoutes.Count -eq 1) 'Removing a wholly deprecated API must include its API and raw operation in release notes.'

    $nestedBefore = Join-Path $temporary 'nested-before'
    $nestedAfter = Join-Path $temporary 'nested-after'
    Save-Schema $nestedBefore 'widgets' @{ '/widgets' = @{ get = @{} } }
    Save-Schema $nestedBefore 'analytics/widgets' @{ '/widgets' = @{ get = @{} } }
    Save-Schema $nestedBefore 'analytics/manifest' @{ '/manifests' = @{ get = @{} } }
    Save-SnapshotManifest $nestedBefore
    Copy-SchemaSnapshot -Source $nestedBefore -Destination $nestedAfter
    $nestedOriginal = Get-SchemaInventory $nestedBefore
    Assert-Test ($nestedOriginal.Apis.Count -eq 3 -and $nestedOriginal.Apis.ContainsKey('widgets') -and $nestedOriginal.Apis.ContainsKey('analytics/widgets') -and $nestedOriginal.Apis.ContainsKey('analytics/manifest')) 'Relative API identities must retain namespaces; only the root manifest is excluded.'
    Assert-Test ((Test-Path -LiteralPath (Join-Path $nestedAfter 'analytics/widgets.json')) -and (Test-Path -LiteralPath (Join-Path $nestedAfter 'manifest.json'))) 'Snapshot copy must preserve nested schema paths and copy the root manifest.'
    Assert-Test (-not (Compare-SchemaInventory $nestedOriginal (Get-SchemaInventory $nestedAfter)).Changed) 'Recursive snapshot copying must preserve the content digest.'
    Save-Schema $nestedAfter 'analytics/widgets' @{ '/widgets' = @{ get = @{}; post = @{} } }
    $nestedDifference = Compare-SchemaInventory $nestedOriginal (Get-SchemaInventory $nestedAfter)
    Assert-Test ($nestedDifference.ChangedApis.Count -eq 1 -and $nestedDifference.ChangedApis[0] -ceq 'analytics/widgets' -and $nestedDifference.AddedRoutes[0] -ceq 'analytics/widgets POST /widgets') 'A nested API change must not be attributed to a root API with the same filename.'
    Remove-Item -LiteralPath (Join-Path $nestedAfter 'analytics/manifest.json')
    $nestedRemoved = Compare-SchemaInventory $nestedOriginal (Get-SchemaInventory $nestedAfter)
    Assert-Test ($nestedRemoved.RemovedApis.Count -eq 1 -and $nestedRemoved.RemovedApis[0] -ceq 'analytics/manifest' -and $nestedRemoved.RemovedRoutes[0] -ceq 'analytics/manifest GET /manifests') 'Nested API and operation removals must retain their namespace.'

    # A replacement snapshot removes only obsolete, manifest-owned schema JSON.
    Save-SnapshotManifest $nestedAfter
    Set-Content -LiteralPath (Join-Path $nestedBefore 'keep.txt') -Value 'unknown non-schema content'
    Copy-SchemaSnapshot -Source $nestedAfter -Destination $nestedBefore
    Assert-Test (-not (Test-Path -LiteralPath (Join-Path $nestedBefore 'analytics/manifest.json')) -and (Test-Path -LiteralPath (Join-Path $nestedBefore 'keep.txt'))) 'Snapshot replacement must remove stale nested schema JSON while preserving unrelated files.'
    Assert-Test ((Get-SchemaInventory $nestedBefore).Digest -ceq (Get-SchemaInventory $nestedAfter).Digest) 'Copied snapshot must exactly match the replacement inventory after removals.'

    $caseTarget = Join-Path $temporary 'case-target'
    Save-Schema $caseTarget 'Analytics/other' @{ '/other' = @{ get = @{} } }
    Save-SnapshotManifest $caseTarget
    Assert-Throws { Copy-SchemaSnapshot -Source $nestedBefore -Destination $caseTarget } 'Snapshot copy must reject namespace casing collisions before writing any files.'
    Assert-Test (-not (Test-Path -LiteralPath (Join-Path $caseTarget 'widgets.json'))) 'Failed copy preflight must leave the destination unchanged.'

    $copySource = Join-Path $temporary 'copy-source'
    $copyTarget = Join-Path $temporary 'copy-target'
    Save-Schema $copySource 'current' @{ '/current' = @{ get = @{} } }
    Save-SnapshotManifest $copySource
    Save-Schema $copyTarget 'obsolete' @{ '/obsolete' = @{ get = @{} } }
    Save-SnapshotManifest $copyTarget
    $savedTarget = Get-SnapshotBytes $copyTarget
    $sourceManifestPath = Join-Path $copySource 'manifest.json'
    $sourceManifest = Get-Content -LiteralPath $sourceManifestPath -Raw | ConvertFrom-Json -AsHashtable
    $sourceManifest.apis[0].sha256 = '0' * 64
    Write-ReleaseJson $sourceManifestPath $sourceManifest
    Assert-Throws { Copy-SchemaSnapshot $copySource $copyTarget } 'A wrong source manifest hash must stop copying before stale schemas are deleted.'
    Assert-Test ((Get-SnapshotBytes $copyTarget) -ceq $savedTarget) 'A source manifest failure must leave every destination byte unchanged.'
    Save-SnapshotManifest $copySource
    $sourceManifest = Get-Content -LiteralPath $sourceManifestPath -Raw | ConvertFrom-Json -AsHashtable
    $sourceManifest.files[0].path = '../outside.json'
    Write-ReleaseJson $sourceManifestPath $sourceManifest
    Assert-Throws { Copy-SchemaSnapshot $copySource $copyTarget } 'Manifest paths outside the snapshot must be rejected.'
    Assert-Test ((Get-SnapshotBytes $copyTarget) -ceq $savedTarget) 'An unsafe source manifest must not mutate the destination.'
    Save-SnapshotManifest $copySource
    $sourceManifest = Get-Content -LiteralPath $sourceManifestPath -Raw | ConvertFrom-Json -AsHashtable
    $sourceManifest.files = @($sourceManifest.files[0], $sourceManifest.files[0])
    Write-ReleaseJson $sourceManifestPath $sourceManifest
    Assert-Throws { Copy-SchemaSnapshot $copySource $copyTarget } 'Duplicate manifest entries must fail preflight.'
    Save-SnapshotManifest $copySource
    Save-Schema $copySource 'unlisted' @{ '/unlisted' = @{ get = @{} } }
    Assert-Throws { Copy-SchemaSnapshot $copySource $copyTarget } 'A source schema absent from its manifest must fail preflight.'
    Assert-Test ((Get-SnapshotBytes $copyTarget) -ceq $savedTarget) 'An incomplete source manifest must not mutate the destination.'
    Save-SnapshotManifest $copySource
    $targetManifestPath = Join-Path $copyTarget 'manifest.json'
    $targetManifest = Get-Content -LiteralPath $targetManifestPath -Raw | ConvertFrom-Json -AsHashtable
    $targetManifest.files[0].sha256 = '0' * 64
    Write-ReleaseJson $targetManifestPath $targetManifest
    $invalidTarget = Get-SnapshotBytes $copyTarget
    Assert-Throws { Copy-SchemaSnapshot $copySource $copyTarget } 'A destination ownership/hash mismatch must prevent removal.'
    Assert-Test ((Get-SnapshotBytes $copyTarget) -ceq $invalidTarget) 'A mismatched destination manifest must be left unchanged.'
    Save-SnapshotManifest $copyTarget
    Write-ReleaseJson (Join-Path $copyTarget 'unowned.json') @{ purpose = 'Unrelated JSON is not a removable schema.' }
    $unknownTarget = Get-SnapshotBytes $copyTarget
    Assert-Throws { Copy-SchemaSnapshot $copySource $copyTarget } 'Unknown JSON in the destination must not be silently deleted.'
    Assert-Test ((Get-SnapshotBytes $copyTarget) -ceq $unknownTarget) 'Unknown destination files must survive a failed copy unchanged.'
    Remove-Item -LiteralPath (Join-Path $copyTarget 'unowned.json')
    Save-Schema $copyTarget 'unowned' @{ '/user-owned' = @{ get = @{} } }
    $unknownSchemaTarget = Get-SnapshotBytes $copyTarget
    Assert-Throws { Copy-SchemaSnapshot $copySource $copyTarget } 'A valid-looking schema absent from destination ownership metadata must not be deleted.'
    Assert-Test ((Get-SnapshotBytes $copyTarget) -ceq $unknownSchemaTarget) 'Unlisted destination schema files must remain byte-for-byte unchanged.'
    Remove-Item -LiteralPath (Join-Path $copyTarget 'unowned.json')
    $savedManifest = [IO.File]::ReadAllBytes($targetManifestPath)
    Remove-Item -LiteralPath $targetManifestPath
    Assert-Throws { Copy-SchemaSnapshot $copySource $copyTarget } 'An existing JSON tree without an ownership manifest must not be overwritten or pruned.'
    Assert-Test (Test-Path -LiteralPath (Join-Path $copyTarget 'obsolete.json')) 'Missing ownership metadata must preserve the existing schema.'
    [IO.File]::WriteAllBytes($targetManifestPath, $savedManifest)
    Assert-Throws { Copy-SchemaSnapshot $copySource (Join-Path $copySource 'child') } 'Overlapping snapshot trees must fail before creating a destination.'
    Assert-Throws { Copy-SchemaSnapshot $copySource $copySource } 'Copying a snapshot onto itself must fail.'
    Assert-Test (-not (Test-Path -LiteralPath (Join-Path $copySource 'child'))) 'An overlapping destination must not be created.'
    Copy-SchemaSnapshot $copySource $copyTarget
    Assert-Test (-not (Test-Path -LiteralPath (Join-Path $copyTarget 'obsolete.json')) -and (Get-SchemaInventory $copyTarget).Apis.Count -eq 2) 'A valid owned obsolete schema must be removed during replacement.'

    $collisionTarget = Join-Path $temporary 'file-directory-collision'
    Save-Schema $collisionTarget 'old' @{ '/old' = @{ get = @{} } }
    Save-SnapshotManifest $collisionTarget
    Set-Content -LiteralPath (Join-Path $collisionTarget 'analytics') -Value 'Unrelated file occupies a required directory.'
    $collisionBytes = Get-SnapshotBytes $collisionTarget
    Assert-Throws { Copy-SchemaSnapshot $nestedBefore $collisionTarget } 'File/directory collisions must fail before any writes or deletions.'
    Assert-Test ((Get-SnapshotBytes $collisionTarget) -ceq $collisionBytes) 'File/directory collision preflight must preserve all existing files.'

    $linkSource = Join-Path $temporary 'link-source'
    $null = [IO.Directory]::CreateDirectory($linkSource)
    $link = Join-Path $linkSource 'linked'
    $null = New-Item -ItemType Junction -Path $link -Target $nestedBefore
    try {
        Assert-Throws { Get-SchemaInventory $linkSource } 'Snapshot inventory must reject reparse-point directories.'
        Assert-Throws { Copy-SchemaSnapshot -Source $linkSource -Destination (Join-Path $temporary 'link-copy') } 'Snapshot copy must reject reparse-point source directories.'
        Assert-Throws { Copy-SchemaSnapshot -Source $nestedBefore -Destination (Join-Path $link 'new') } 'Snapshot copy must reject destination ancestors that are reparse points.'
    }
    finally { Remove-Item -LiteralPath $link -Force }

    $firstArchive = Join-Path $temporary 'schemas-first.zip'
    $secondArchive = Join-Path $temporary 'schemas-second.zip'
    [IO.Compression.ZipFile]::CreateFromDirectory($nestedBefore, $firstArchive, [IO.Compression.CompressionLevel]::NoCompression, $false)
    foreach ($file in Get-ChildItem -LiteralPath $nestedBefore -File -Recurse) { [IO.File]::SetLastWriteTimeUtc($file.FullName, [datetime]::new(2024, 1, 1, 0, 0, 0, [DateTimeKind]::Utc)) }
    Write-ReleaseJson (Join-Path $nestedBefore 'manifest.json') @{ archiveSha256 = ('2' * 64) }
    [IO.Compression.ZipFile]::CreateFromDirectory($nestedBefore, $secondArchive, [IO.Compression.CompressionLevel]::Optimal, $false)
    Assert-Test ((Get-FileHash -LiteralPath $firstArchive).Hash -cne (Get-FileHash -LiteralPath $secondArchive).Hash) 'Archive fixture bytes must differ through timestamps, compression, and provenance metadata.'
    $firstExtracted = Join-Path $temporary 'first-extracted'
    $secondExtracted = Join-Path $temporary 'second-extracted'
    [IO.Compression.ZipFile]::ExtractToDirectory($firstArchive, $firstExtracted)
    [IO.Compression.ZipFile]::ExtractToDirectory($secondArchive, $secondExtracted)
    Assert-Test (-not (Compare-SchemaInventory (Get-SchemaInventory $firstExtracted) (Get-SchemaInventory $secondExtracted)).Changed) 'ZIP and root-manifest metadata changes must not trigger a release when extracted schema content is unchanged.'

    Assert-Test ((Get-NextPatchVersion '2.0.9') -eq '2.0.10') 'Patch increments must be numeric.'
    Assert-Throws { Get-NextPatchVersion '2.0.0.1' } 'Four-part versions must not silently change policy.'
    Assert-Throws { Get-NextPatchVersion '2.0.65534' } 'Assembly version overflow must fail.'
    Assert-Test ((Get-ReleaseDisposition -Version '2.0.1' -CandidateVersion '2.0.1' -ReleaseExists $false -ReleaseIsDraft $false -GalleryPublished $false) -eq 'Resume') 'A committed candidate must resume after a failed GitHub draft creation.'
    Assert-Test ((Get-ReleaseDisposition -Version '2.0.1' -CandidateVersion '2.0.1' -ReleaseExists $true -ReleaseIsDraft $true -GalleryPublished $true) -eq 'Resume') 'A successful Gallery upload followed by a failed GitHub finalization must resume the same version.'
    Assert-Test ((Get-ReleaseDisposition -Version '2.0.1' -CandidateVersion '2.0.1' -ReleaseExists $true -ReleaseIsDraft $false -GalleryPublished $true) -eq 'Refresh') 'A fully published candidate permits a new schema check.'
    $archive = Join-Path $temporary 'bad.zip'
    $zip = [IO.Compression.ZipFile]::Open($archive, [IO.Compression.ZipArchiveMode]::Create)
    $null = $zip.CreateEntry('QlikCloudCLI/QlikCloudCLI.psd1')
    $null = $zip.CreateEntry('QlikCloudCLI/../escape.txt')
    $zip.Dispose()
    $hash = (Get-FileHash -LiteralPath $archive).Hash
    Assert-Throws { Assert-SafeArchive $archive $hash } 'Archive traversal must be rejected.'
    Assert-Throws { Assert-SafeArchive $archive ('0' * 64) } 'An archive with a wrong checksum must be rejected.'
    $module = Join-Path $temporary 'module'
    $null = [IO.Directory]::CreateDirectory($module)
    $bytes = [Text.Encoding]::UTF8.GetBytes('tested DLL bytes')
    $expected = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
    Write-ReleaseJson (Join-Path $module '.qlikcloudcli-module.json') @{ files = @(@{ path = 'bin/Widget.dll'; sha256 = $expected }) }
    $package = Join-Path $temporary 'good.nupkg'
    $zip = [IO.Compression.ZipFile]::Open($package, [IO.Compression.ZipArchiveMode]::Create)
    $entry = $zip.CreateEntry('bin/Widget.dll')
    $stream = $entry.Open()
    $stream.Write($bytes)
    $stream.Dispose()
    $zip.Dispose()
    Assert-GalleryPackageContent $package $module
    Assert-Test $true 'The exact tested module bytes should pass Gallery package verification.'
    Write-ReleaseJson (Join-Path $module '.qlikcloudcli-module.json') @{ files = @(@{ path = 'bin/Widget.dll'; sha256 = ('0' * 64) }) }
    Assert-Throws { Assert-GalleryPackageContent $package $module } 'A Gallery package with altered DLL bytes must be rejected.'
    Write-ReleaseJson (Join-Path $module '.qlikcloudcli-module.json') @{ files = @(@{ path = 'bin/Missing.dll'; sha256 = $expected }) }
    Assert-Throws { Assert-GalleryPackageContent $package $module } 'A Gallery package with missing module files must be rejected.'
    Write-Host "Automation checks passed: $passed"
}
finally {
    $resolved = [IO.Path]::GetFullPath($temporary)
    $tempRoot = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath([IO.Path]::GetTempPath())) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($resolved) -notlike 'qlikcloud-automation-tests-*') { throw 'Unsafe automation test cleanup path.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
