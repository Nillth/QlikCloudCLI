#requires -Version 7.4
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-SchemaDirectoryPath {
    param([Parameter(Mandatory)][string] $Path)
    $directory = [IO.DirectoryInfo]::new([IO.Path]::GetFullPath($Path))
    while ($null -ne $directory) {
        if (Test-Path -LiteralPath $directory.FullName) {
            $entry = Get-Item -LiteralPath $directory.FullName -Force
            if (-not $entry.PSIsContainer -or ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Schema directory paths cannot contain files or reparse points.' }
        }
        $directory = $directory.Parent
    }
}

function Get-SchemaFiles {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string] $Path, [switch] $IncludeDirectories)
    $root = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($Path))
    Assert-SchemaDirectoryPath $root
    $directory = Get-Item -LiteralPath $root -Force
    if (-not $directory.PSIsContainer) { throw 'Schema snapshot must be a directory.' }
    $pending = [Collections.Generic.Stack[IO.DirectoryInfo]]::new()
    $pending.Push($directory)
    $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    while ($pending.Count -gt 0) {
        $directory = $pending.Pop()
        if (($directory.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Schema snapshot cannot contain reparse points.' }
        foreach ($entry in Get-ChildItem -LiteralPath $directory.FullName -Force) {
            if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Schema snapshot cannot contain reparse points.' }
            $relative = [IO.Path]::GetRelativePath($root, $entry.FullName).Replace('\', '/')
            if ([IO.Path]::IsPathRooted($relative) -or $relative -match '(^|/)\.\.(/|$)|:' -or -not $names.Add($relative)) {
                throw "Unsafe or case-colliding schema path: $relative"
            }
            if ($entry.PSIsContainer) {
                $pending.Push($entry)
                if ($IncludeDirectories) { [pscustomobject]@{ FullName = $entry.FullName; RelativePath = $relative; IsDirectory = $true } }
                continue
            }
            if ($entry.Extension -ieq '.json') {
                [pscustomobject]@{ FullName = $entry.FullName; RelativePath = $relative; Api = $relative.Substring(0, $relative.Length - 5); IsDirectory = $false }
            }
        }
    }
}

function Test-DeprecatedSchemaObject {
    param($Value)
    if ($Value -isnot [Collections.IDictionary]) { return $false }
    foreach ($name in @('x-qlik-deprecated', 'deprecated')) {
        # JSON keys and boolean values are exact: strings/numbers are not flags.
        if (@($Value.Keys | Where-Object { $_ -ceq $name }).Count -and $Value[$name] -is [bool] -and $Value[$name]) { return $true }
    }
    return $false
}

function Get-SchemaInventory {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string] $Path)
    $apis = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::Ordinal)
    $routes = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $deprecatedApis = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $deprecatedRoutes = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($file in Get-SchemaFiles $Path | Sort-Object RelativePath -CaseSensitive) {
        if ($file.RelativePath -ieq 'manifest.json') { continue }
        $content = (Get-Content -LiteralPath $file.FullName -Raw).Replace("`r`n", "`n").Replace("`r", "`n")
        $schema = $content | ConvertFrom-Json -AsHashtable
        if ($schema -isnot [Collections.IDictionary] -or $schema['paths'] -isnot [Collections.IDictionary] -or (-not $schema.Contains('openapi') -and -not $schema.Contains('swagger'))) {
            throw "Invalid schema: $($file.RelativePath)"
        }
        # Git checkout line endings must not create a weekly false positive.
        $apis.Add($file.Api, [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($content))).ToLowerInvariant())
        $apiDeprecated = (Test-DeprecatedSchemaObject $schema) -or (Test-DeprecatedSchemaObject $schema['info'])
        if ($apiDeprecated) { $null = $deprecatedApis.Add($file.Api) }
        $pathsDeprecated = $apiDeprecated -or (Test-DeprecatedSchemaObject $schema.paths)
        foreach ($route in $schema.paths.Keys) {
            if (-not $route.StartsWith('/', [StringComparison]::Ordinal)) { continue }
            if ($route -match '[\r\n]' -or $schema.paths[$route] -isnot [Collections.IDictionary]) { throw "Invalid path item in $($file.RelativePath)." }
            $pathDeprecated = $pathsDeprecated -or (Test-DeprecatedSchemaObject $schema.paths[$route])
            foreach ($method in $schema.paths[$route].Keys) {
                if ($method -cin @('get','post','put','patch','delete','head','options','trace')) {
                    $operation = $schema.paths[$route][$method]
                    if ($operation -isnot [Collections.IDictionary]) { throw "Invalid HTTP operation in $($file.RelativePath)." }
                    $identity = "$($file.Api) $($method.ToUpperInvariant()) $route"
                    $null = $routes.Add($identity)
                    if ($pathDeprecated -or (Test-DeprecatedSchemaObject $operation)) { $null = $deprecatedRoutes.Add($identity) }
                }
            }
        }
    }
    if ($apis.Count -eq 0) { throw 'Schema snapshot contains no API documents.' }
    $text = (@($apis.Keys | Sort-Object -CaseSensitive | ForEach-Object { "$_ $($apis[$_])" }) -join "`n") + "`n"
    $digest = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($text))).ToLowerInvariant()
    [pscustomobject]@{ Apis = $apis; Routes = $routes; DeprecatedApis = $deprecatedApis; DeprecatedRoutes = $deprecatedRoutes; Digest = $digest }
}

function Assert-SchemaSnapshotManifest {
    param([Parameter(Mandatory)][string] $Path, [Parameter(Mandatory)] $Inventory)
    $manifestPath = Join-Path $Path 'manifest.json'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw 'Schema snapshot copying requires its generator manifest.' }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -AsHashtable
    if ($manifest -isnot [Collections.IDictionary] -or $manifest['generator'] -cne 'QlikCloud.Generator' -or $manifest['formatVersion'] -ne 1 -or $manifest['command'] -cne 'refresh' -or
        $manifest['apis'] -isnot [array] -or $manifest['files'] -isnot [array]) { throw 'Invalid schema snapshot manifest.' }
    foreach ($collection in @('apis','files')) {
        $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($entry in $manifest[$collection]) {
            if ($entry -isnot [Collections.IDictionary]) { throw "Invalid $collection entry in schema snapshot manifest." }
            $name = if ($collection -eq 'apis') { $entry['api'] } else { $entry['path'] }
            if ($name -isnot [string] -or -not $seen.Add($name)) { throw "Duplicate or invalid $collection name in schema snapshot manifest." }
            $api = if ($collection -eq 'files' -and $name.EndsWith('.json', [StringComparison]::Ordinal)) { $name.Substring(0, $name.Length - 5) } elseif ($collection -eq 'apis') { $name } else { $null }
            if ($null -eq $api -or -not $Inventory.Apis.ContainsKey($api) -or $entry['sha256'] -isnot [string] -or $entry['sha256'] -notmatch '^[a-fA-F0-9]{64}$' -or $entry['sha256'] -ine $Inventory.Apis[$api]) {
                throw "Schema manifest $collection entry does not match the snapshot: $name"
            }
        }
        if ($seen.Count -ne $Inventory.Apis.Count) { throw "Schema manifest $collection inventory does not match the snapshot." }
    }
}

function Copy-SchemaSnapshot {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string] $Source, [Parameter(Mandatory)][string] $Destination)
    $sourceRoot = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($Source))
    $root = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($Destination))
    if ($sourceRoot.Equals($root, [StringComparison]::OrdinalIgnoreCase) -or
        $sourceRoot.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
        $root.StartsWith($sourceRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Source and destination schema snapshots must not overlap.' }
    # Finish validation of both trees, all paths, and both ownership manifests
    # before copying or deleting anything. Unknown JSON is never presumed owned.
    $entries = @(Get-SchemaFiles $Source -IncludeDirectories)
    $files = @($entries | Where-Object { -not $_.IsDirectory })
    $sourceInventory = Get-SchemaInventory $Source
    Assert-SchemaSnapshotManifest $Source $sourceInventory
    Assert-SchemaDirectoryPath $root
    $existing = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::OrdinalIgnoreCase)
    $existingEntries = @()
    $removedFiles = @()
    if (Test-Path -LiteralPath $root) {
        $existingEntries = @(Get-SchemaFiles $root -IncludeDirectories)
        foreach ($entry in $existingEntries) { $existing.Add($entry.RelativePath, $entry.RelativePath) }
        if (@($existingEntries | Where-Object { -not $_.IsDirectory }).Count) {
            $existingInventory = Get-SchemaInventory $root
            Assert-SchemaSnapshotManifest $root $existingInventory
            $removedFiles = @($existingEntries | Where-Object { -not $_.IsDirectory -and $_.RelativePath -cne 'manifest.json' -and -not $sourceInventory.Apis.ContainsKey($_.Api) })
        }
    }
    foreach ($entry in $entries) {
        if ($existing.ContainsKey($entry.RelativePath) -and $existing[$entry.RelativePath] -cne $entry.RelativePath) {
            throw "Schema path casing differs from the saved snapshot: $($entry.RelativePath)"
        }
        $target = [IO.Path]::GetFullPath((Join-Path $root $entry.RelativePath))
        if (-not $target.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Schema copy target is outside the snapshot directory.' }
        if ((Test-Path -LiteralPath $target) -and (Get-Item -LiteralPath $target -Force).PSIsContainer -ne $entry.IsDirectory) { throw "Schema copy would replace a file with a directory or a directory with a file: $($entry.RelativePath)" }
    }
    foreach ($file in $removedFiles) {
        $target = [IO.Path]::GetFullPath($file.FullName)
        if (-not $target.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Schema removal target is outside the snapshot directory.' }
    }
    foreach ($file in $files | Where-Object RelativePath -CNE 'manifest.json') {
        $target = Join-Path $root $file.RelativePath
        $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
        Copy-Item -LiteralPath $file.FullName -Destination $target -Force
    }
    # Remove only obsolete schema files verified against the previous manifest.
    # Leave unknown non-JSON files and directories alone; never recursively delete.
    foreach ($file in $removedFiles) { Remove-Item -LiteralPath $file.FullName -Force }
    Copy-Item -LiteralPath (Join-Path $sourceRoot 'manifest.json') -Destination (Join-Path $root 'manifest.json') -Force
}

function Compare-SchemaInventory {
    [CmdletBinding()]
    param([Parameter(Mandatory)] $Before, [Parameter(Mandatory)] $After)
    [pscustomobject]@{
        Changed = $Before.Digest -cne $After.Digest
        AddedApis = @($After.Apis.Keys | Where-Object { -not $Before.Apis.ContainsKey($_) } | Sort-Object)
        ChangedApis = @($After.Apis.Keys | Where-Object { $Before.Apis.ContainsKey($_) -and $Before.Apis[$_] -cne $After.Apis[$_] } | Sort-Object)
        RemovedApis = @($Before.Apis.Keys | Where-Object { -not $After.Apis.ContainsKey($_) } | Sort-Object)
        AddedRoutes = @($After.Routes | Where-Object { -not $Before.Routes.Contains($_) } | Sort-Object)
        RemovedRoutes = @($Before.Routes | Where-Object { -not $After.Routes.Contains($_) } | Sort-Object)
        DeprecatedApis = @($After.DeprecatedApis | Where-Object { -not $Before.DeprecatedApis.Contains($_) } | Sort-Object)
        DeprecatedRoutes = @($After.DeprecatedRoutes | Where-Object { -not $Before.DeprecatedRoutes.Contains($_) } | Sort-Object)
    }
}

function New-SchemaReleaseState {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string] $Version,
        [Parameter(Mandatory)][string] $PreviousVersion,
        [Parameter(Mandatory)] $Inventory,
        [Parameter(Mandatory)] $Difference,
        [string[]] $ReleaseNotes = @()
    )
    [pscustomobject][ordered]@{
        formatVersion = 2; version = $Version; previousVersion = $PreviousVersion; schemaDigest = $Inventory.Digest
        changedApis = @($Difference.AddedApis) + @($Difference.ChangedApis)
        releaseNotes = @($ReleaseNotes)
        schemaChanges = [ordered]@{
            addedApis = @($Difference.AddedApis); changedApis = @($Difference.ChangedApis)
            deprecatedApis = @($Difference.DeprecatedApis); removedApis = @($Difference.RemovedApis)
            addedOperations = @($Difference.AddedRoutes); deprecatedOperations = @($Difference.DeprecatedRoutes)
            removedOperations = @($Difference.RemovedRoutes)
            currentDeprecatedApis = @($Inventory.DeprecatedApis | Sort-Object -CaseSensitive)
            currentDeprecatedOperations = @($Inventory.DeprecatedRoutes | Sort-Object -CaseSensitive)
        }
    }
}

function Get-SchemaReleaseNotes {
    [CmdletBinding()]
    param([Parameter(Mandatory)] $State, [Parameter(Mandatory)][string] $Commit)
    # JSON round-trip gives identical behavior for freshly created, tagged, and
    # resumed state, including old format-1 candidates without lifecycle fields.
    $candidate = $State | ConvertTo-Json -Depth 12 | ConvertFrom-Json -AsHashtable
    if ($candidate['formatVersion'] -notin @(1,2)) { throw 'Unsupported release candidate state format.' }
    $lines = [Collections.Generic.List[string]]::new()
    foreach ($line in @("QlikCloudCLI $($candidate['version'])", '', 'Built and tested with Windows PowerShell 5.1 and PowerShell 7.', '', "Schema snapshot SHA256: $($candidate['schemaDigest'])", "Source commit: $Commit", '')) { $lines.Add($line) }
    if ($candidate.Contains('releaseNotes')) {
        if ($candidate['releaseNotes'] -isnot [array]) { throw 'Invalid candidate release notes.' }
        foreach ($note in $candidate['releaseNotes']) {
            if ($note -isnot [string] -or [string]::IsNullOrWhiteSpace($note)) { throw 'Invalid candidate release note.' }
            $lines.Add('- ' + $note)
        }
        if ($candidate['releaseNotes'].Count) { $lines.Add('') }
    }
    if ($candidate['formatVersion'] -eq 1) {
        $lines.Add('Updated API schemas:')
        foreach ($api in $candidate['changedApis']) { $lines.Add('- ' + $api) }
        if (-not $candidate['changedApis'].Count) { $lines.Add('- No schema changes were recorded for this candidate.') }
        $lines.Add('')
        $lines.Add('Detailed API and operation lifecycle changes were not recorded by this legacy candidate format.')
    }
    else {
        $changes = $candidate['schemaChanges']
        if ($changes -isnot [Collections.IDictionary]) { throw 'Candidate state is missing schema lifecycle changes.' }
        $headings = [ordered]@{ addedApis = 'Added APIs'; changedApis = 'Updated API schemas'; deprecatedApis = 'Newly deprecated APIs'; removedApis = 'Removed APIs'; addedOperations = 'Added HTTP operations'; deprecatedOperations = 'Newly deprecated HTTP operations'; removedOperations = 'Removed HTTP operations'; currentDeprecatedApis = 'Deprecated APIs in this snapshot'; currentDeprecatedOperations = 'Deprecated HTTP operations in this snapshot' }
        $count = 0
        foreach ($entry in $headings.GetEnumerator()) {
            if ($changes[$entry.Key] -isnot [array]) { throw "Candidate state is missing lifecycle list: $($entry.Key)" }
            if (-not $changes[$entry.Key].Count) { continue }
            $lines.Add($entry.Value + ':')
            foreach ($value in $changes[$entry.Key]) {
                if ($value -isnot [string] -or [string]::IsNullOrWhiteSpace($value) -or $value -match '[\r\n]') { throw "Invalid candidate lifecycle entry: $($entry.Key)" }
                $lines.Add('- ' + $value)
                $count++
            }
            $lines.Add('')
        }
        if (-not $count) { $lines.Add('No schema content changes; this release contains source or packaging updates.') }
    }
    return ($lines -join "`n") + "`n"
}

function Get-NextPatchVersion {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string] $Version)
    if ($Version -cnotmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') { throw "Expected a three-part stable version: $Version" }
    $parts = $Version.Split('.')
    $patch = [long]::Parse($parts[2]) + 1
    if ($patch -gt 65534) { throw 'Patch version would exceed the assembly version limit; increment the minor version manually.' }
    '{0}.{1}.{2}' -f $parts[0], $parts[1], $patch
}

function Get-ReleaseDisposition {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string] $Version,
        [string] $CandidateVersion,
        [bool] $ReleaseExists,
        [bool] $ReleaseIsDraft,
        [bool] $GalleryPublished
    )
    if ($Version -ceq $CandidateVersion -and (-not $ReleaseExists -or $ReleaseIsDraft -or -not $GalleryPublished)) { return 'Resume' }
    return 'Refresh'
}

function Assert-SafeArchive {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string] $Path, [Parameter(Mandatory)][string] $ExpectedSha256)
    if ((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ine $ExpectedSha256) { throw 'Release archive does not match its recorded SHA256.' }
    $zip = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($entry in $zip.Entries) {
            $name = $entry.FullName.Replace('\','/')
            if (-not $name.StartsWith('QlikCloudCLI/', [StringComparison]::Ordinal) -or $name -match '(^|/)\.\.(/|$)|:' -or -not $names.Add($name)) {
                throw "Unsafe or duplicate release archive entry: $name"
            }
        }
        if (-not $names.Contains('QlikCloudCLI/QlikCloudCLI.psd1')) { throw 'Release archive has no module manifest.' }
    }
    finally { $zip.Dispose() }
}

function Assert-ModuleFileHashes {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string] $ModuleDirectory)
    $root = [IO.Path]::GetFullPath($ModuleDirectory)
    $receipt = Get-Content -LiteralPath (Join-Path $root '.qlikcloudcli-module.json') -Raw | ConvertFrom-Json
    if ($receipt.generator -cne 'QlikCloudCLI.Module' -or $receipt.formatVersion -ne 1) { throw 'Unexpected module ownership receipt.' }
    foreach ($file in $receipt.files) {
        $path = [IO.Path]::GetFullPath((Join-Path $root $file.path))
        if (-not $path.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Module receipt contains an unsafe path.' }
        if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ine $file.sha256) { throw "Module file hash mismatch: $($file.path)" }
    }
}

function Assert-ReleaseSigning {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string] $ModuleDirectory, [Parameter(Mandatory)][ValidateSet('Authenticode','Unsigned')][string] $SigningMode)
    if ($SigningMode -eq 'Unsigned') { return }
    foreach ($name in @('bin/QlikCloud.dll','bin/QlikCloudCLI.dll','QlikCloudCLI.psd1')) {
        $signature = Get-AuthenticodeSignature -LiteralPath (Join-Path $ModuleDirectory $name)
        if ($signature.Status -ne 'Valid' -or $null -eq $signature.TimeStamperCertificate) { throw "Release requires a valid timestamped signature: $name" }
    }
}

function Assert-GalleryPackageContent {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string] $PackagePath, [Parameter(Mandatory)][string] $ModuleDirectory)
    $receipt = Get-Content -LiteralPath (Join-Path $ModuleDirectory '.qlikcloudcli-module.json') -Raw | ConvertFrom-Json
    $zip = [IO.Compression.ZipFile]::OpenRead($PackagePath)
    try {
        foreach ($file in $receipt.files) {
            $entry = $zip.GetEntry($file.path)
            if ($null -eq $entry) { throw "Gallery package is missing $($file.path)." }
            $stream = $entry.Open()
            try { $actual = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant() }
            finally { $stream.Dispose() }
            if ($actual -cne $file.sha256) { throw "Gallery package content does not match the tested module: $($file.path)." }
        }
    }
    finally { $zip.Dispose() }
}

function Write-ReleaseJson {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string] $Path, [Parameter(Mandatory)] $Value)
    [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 12) + "`n", [Text.UTF8Encoding]::new($false))
}

Export-ModuleMember -Function Get-SchemaInventory,Copy-SchemaSnapshot,Compare-SchemaInventory,New-SchemaReleaseState,Get-SchemaReleaseNotes,Get-NextPatchVersion,Get-ReleaseDisposition,Assert-SafeArchive,Assert-ModuleFileHashes,Assert-ReleaseSigning,Assert-GalleryPackageContent,Write-ReleaseJson
