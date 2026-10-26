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
function Save-Schema([string] $Directory, [string] $Name, [hashtable] $Paths) {
    $path = Join-Path $Directory ($Name + '.json')
    $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path))
    Write-ReleaseJson $path @{ openapi = '3.0.0'; paths = $Paths }
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
    Assert-CompatibleSchemaUpdate $added
    Save-Schema $after 'widgets' @{ '/widgets' = @{ get = @{} }; '/widgets/{id}' = @{ get = @{} } }
    $removed = Compare-SchemaInventory $original (Get-SchemaInventory $after)
    Assert-Test ($removed.RemovedRoutes.Count -eq 1 -and $removed.RemovedRoutes[0] -eq 'widgets DELETE /widgets/{id}') 'Removed method on an existing path must be detected.'
    Assert-Throws { Assert-CompatibleSchemaUpdate $removed } 'Removed operations must block automatic release.'
    Assert-Throws { Assert-CompatibleSchemaUpdate ([pscustomobject]@{ RemovedApis = @('widgets'); RemovedRoutes = @() }) } 'Removed API documents must block automatic release.'

    $deprecatedBefore = Join-Path $temporary 'deprecated-before'
    $deprecatedAfter = Join-Path $temporary 'deprecated-after'
    Save-Schema $deprecatedBefore 'widgets' @{
        '/widgets' = @{ get = @{}; post = @{ 'x-qlik-deprecated' = $true } }
        '/retired' = @{ 'x-qlik-deprecated' = $true; get = @{} }
    }
    Save-Schema $deprecatedAfter 'widgets' @{ '/widgets' = @{ get = @{} } }
    $deprecatedOriginal = Get-SchemaInventory $deprecatedBefore
    $retired = Compare-SchemaInventory $deprecatedOriginal (Get-SchemaInventory $deprecatedAfter)
    Assert-Test ($deprecatedOriginal.Routes.Count -eq 1 -and $deprecatedOriginal.Routes.Contains('widgets GET /widgets')) 'Generator-excluded operations and path items must not enter the active inventory.'
    Assert-Test ($retired.Changed -and $retired.ChangedApis.Count -eq 1 -and $retired.RemovedRoutes.Count -eq 0) 'Retiring already-excluded operations must retain content change detection without reporting a command removal.'
    Assert-CompatibleSchemaUpdate $retired
    Assert-Test $true 'Retiring already-excluded operations must permit automatic validation.'
    Save-Schema $deprecatedAfter 'widgets' @{ '/widgets' = @{ get = @{ 'x-qlik-deprecated' = $true } } }
    $newlyDeprecated = Compare-SchemaInventory $deprecatedOriginal (Get-SchemaInventory $deprecatedAfter)
    Assert-Test ($newlyDeprecated.RemovedRoutes.Count -eq 1 -and $newlyDeprecated.RemovedRoutes[0] -ceq 'widgets GET /widgets') 'Newly deprecating an active operation must count as removal.'
    Assert-Throws { Assert-CompatibleSchemaUpdate $newlyDeprecated } 'Newly deprecated active operations must still block automatic release.'
    Save-Schema $deprecatedAfter 'widgets' @{ '/widgets' = @{ 'x-qlik-deprecated' = $true; get = @{} } }
    Assert-Throws { Assert-CompatibleSchemaUpdate (Compare-SchemaInventory $deprecatedOriginal (Get-SchemaInventory $deprecatedAfter)) } 'Newly deprecated active paths must still block automatic release.'
    Save-Schema $deprecatedAfter 'gadgets' @{ '/gadgets' = @{ get = @{} } }
    Remove-Item -LiteralPath (Join-Path $deprecatedAfter 'widgets.json')
    Assert-Throws { Assert-CompatibleSchemaUpdate (Compare-SchemaInventory $deprecatedOriginal (Get-SchemaInventory $deprecatedAfter)) } 'Deprecation filtering must not bypass removed API document protection.'

    $retainedBefore = Join-Path $temporary 'retained-before'
    $retainedAfter = Join-Path $temporary 'retained-after'
    Save-Schema $retainedBefore 'widgets' @{
        '/false' = @{ get = @{ 'x-qlik-deprecated' = $false } }
        '/string' = @{ get = @{ 'x-qlik-deprecated' = 'true' } }
        '/number' = @{ get = @{ 'x-qlik-deprecated' = 1 } }
        '/null' = @{ get = @{ 'x-qlik-deprecated' = $null } }
        '/case' = @{ get = @{ 'X-QLIK-DEPRECATED' = $true } }
        '/standard' = @{ get = @{ deprecated = $true } }
    }
    Save-Schema $retainedAfter 'widgets' @{}
    $retained = Compare-SchemaInventory (Get-SchemaInventory $retainedBefore) (Get-SchemaInventory $retainedAfter)
    Assert-Test ($retained.RemovedRoutes.Count -eq 6) 'Only the exact Qlik boolean true flag may exclude a route; other values, casing and standard deprecated remain protected.'
    Assert-Throws { Assert-CompatibleSchemaUpdate $retained } 'Removal of operations retained by the generator must still block automatic release.'

    $excludedApiBefore = Join-Path $temporary 'excluded-api-before'
    $excludedApiAfter = Join-Path $temporary 'excluded-api-after'
    Save-Schema $excludedApiBefore 'retired' @{ '/retired' = @{ get = @{ 'x-qlik-deprecated' = $true } } }
    Save-Schema $excludedApiAfter 'widgets' @{ '/widgets' = @{ get = @{} } }
    $excludedApi = Compare-SchemaInventory (Get-SchemaInventory $excludedApiBefore) (Get-SchemaInventory $excludedApiAfter)
    Assert-Test ($excludedApi.RemovedApis.Count -eq 1 -and $excludedApi.RemovedRoutes.Count -eq 0) 'API documents remain inventoried even when all their operations are excluded.'
    Assert-Throws { Assert-CompatibleSchemaUpdate $excludedApi } 'Removing an API with only excluded operations must still block automatic release.'

    $nestedBefore = Join-Path $temporary 'nested-before'
    $nestedAfter = Join-Path $temporary 'nested-after'
    Save-Schema $nestedBefore 'widgets' @{ '/widgets' = @{ get = @{} } }
    Save-Schema $nestedBefore 'analytics/widgets' @{ '/widgets' = @{ get = @{} } }
    Save-Schema $nestedBefore 'analytics/manifest' @{ '/manifests' = @{ get = @{} } }
    Write-ReleaseJson (Join-Path $nestedBefore 'manifest.json') @{ archiveSha256 = ('1' * 64) }
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
    Assert-Throws { Assert-CompatibleSchemaUpdate $nestedRemoved } 'A removed nested API must block automatic release.'

    $caseTarget = Join-Path $temporary 'case-target'
    Save-Schema $caseTarget 'Analytics/other' @{ '/other' = @{ get = @{} } }
    Assert-Throws { Copy-SchemaSnapshot -Source $nestedBefore -Destination $caseTarget } 'Snapshot copy must reject namespace casing collisions before writing any files.'
    Assert-Test (-not (Test-Path -LiteralPath (Join-Path $caseTarget 'widgets.json'))) 'Failed copy preflight must leave the destination unchanged.'

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
