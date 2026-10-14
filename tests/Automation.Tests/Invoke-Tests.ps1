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
    $null = [IO.Directory]::CreateDirectory($Directory)
    Write-ReleaseJson (Join-Path $Directory ($Name + '.json')) @{ openapi = '3.0.0'; paths = $Paths }
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
