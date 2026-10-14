#requires -Version 7.4
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-SchemaInventory {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string] $Path)
    $apis = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::Ordinal)
    $routes = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($file in Get-ChildItem -LiteralPath $Path -File -Filter '*.json' | Sort-Object Name) {
        if ($file.Name -eq 'manifest.json') { continue }
        $content = (Get-Content -LiteralPath $file.FullName -Raw).Replace("`r`n", "`n").Replace("`r", "`n")
        $schema = $content | ConvertFrom-Json -AsHashtable
        if (-not $schema.Contains('paths') -or (-not $schema.Contains('openapi') -and -not $schema.Contains('swagger'))) {
            throw "Invalid schema: $($file.Name)"
        }
        # Git checkout line endings must not create a weekly false positive.
        $apis.Add($file.BaseName, [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($content))).ToLowerInvariant())
        foreach ($route in $schema.paths.Keys) {
            foreach ($method in $schema.paths[$route].Keys) {
                if ($method -cin @('get','post','put','patch','delete','head','options','trace')) {
                    $null = $routes.Add("$($file.BaseName) $($method.ToUpperInvariant()) $route")
                }
            }
        }
    }
    if ($apis.Count -eq 0) { throw 'Schema snapshot contains no API documents.' }
    $text = (@($apis.Keys | Sort-Object -CaseSensitive | ForEach-Object { "$_ $($apis[$_])" }) -join "`n") + "`n"
    $digest = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($text))).ToLowerInvariant()
    [pscustomobject]@{ Apis = $apis; Routes = $routes; Digest = $digest }
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
    }
}

function Assert-CompatibleSchemaUpdate {
    [CmdletBinding()]
    param([Parameter(Mandatory)] $Difference)
    if ($Difference.RemovedApis.Count -ne 0 -or $Difference.RemovedRoutes.Count -ne 0) {
        throw ('Automatic release stopped: upstream removed APIs or operations. Review these removals explicitly before updating the saved snapshot: ' + (@($Difference.RemovedApis) + @($Difference.RemovedRoutes) -join '; '))
    }
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

Export-ModuleMember -Function Get-SchemaInventory,Compare-SchemaInventory,Assert-CompatibleSchemaUpdate,Get-NextPatchVersion,Get-ReleaseDisposition,Assert-SafeArchive,Assert-ModuleFileHashes,Assert-ReleaseSigning,Assert-GalleryPackageContent,Write-ReleaseJson
