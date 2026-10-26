#requires -Version 7.4
[CmdletBinding()]
param(
    [switch] $PublishInitial,
    [string] $SigningMode = $env:QLIKCLOUD_SIGNING_MODE
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'Release.Common.psm1') -Force
$repository = 'Nillth/QlikCloudCLI'
$root = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$schemaPath = Join-Path $root 'schemas/qlik-cloud'
$versionPath = Join-Path $root 'version.json'
$statePath = Join-Path $root '.github/schema-release.json'
$work = Join-Path ([IO.Path]::GetTempPath()) ('qlikcloud-release-' + [guid]::NewGuid().ToString('N'))
$null = [IO.Directory]::CreateDirectory($work)

function Invoke-Native([string] $Command, [string[]] $Arguments) {
    $result = & $Command @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) { throw "$Command failed (exit $LASTEXITCODE): $($result -join "`n")" }
    return ($result -join "`n").Trim()
}
function Get-Release([string] $Tag) {
    $result = & gh release view $Tag --repo $repository --json isDraft,tagName,assets 2>&1
    if ($LASTEXITCODE -ne 0) {
        if (($result -join "`n") -match '(?i)release not found|HTTP 404') { return $null }
        throw "Could not inspect GitHub release: $($result -join "`n")"
    }
    return ($result -join "`n") | ConvertFrom-Json
}
function Test-GalleryVersion([string] $Version) {
    # A failed request is an error, never evidence that a version is available.
    if ($Version -cnotmatch '^[0-9]+\.[0-9]+\.[0-9]+$') { throw 'Invalid Gallery version lookup.' }
    $response = Invoke-WebRequest -Uri "https://www.powershellgallery.com/api/v2/Packages(Id='QlikCloudCLI',Version='$Version')" -UseBasicParsing -SkipHttpErrorCheck
    if ($response.StatusCode -eq 404) { return $false }
    if ($response.StatusCode -ne 200) { throw "Gallery lookup failed with HTTP $($response.StatusCode)." }
    [xml]$entry = $response.Content
    $node = $entry.SelectSingleNode('//*[local-name()="properties"]/*[local-name()="Version"]')
    if ($null -eq $node -or $node.InnerText -cne $Version) { throw 'Gallery returned unexpected package metadata.' }
    return $true
}
function Assert-PublisherConfiguration {
    if ($SigningMode -cnotin @('Authenticode','Unsigned')) { throw 'Set QLIKCLOUD_SIGNING_MODE explicitly to Authenticode or Unsigned before enabling publication. No unsigned fallback is allowed.' }
    if ([string]::IsNullOrWhiteSpace($env:PSGALLERY_API_KEY)) { throw 'The powershell-gallery environment needs PSGALLERY_API_KEY.' }
    if ($SigningMode -eq 'Authenticode' -and ([string]::IsNullOrWhiteSpace($env:SIGNING_URL) -or [string]::IsNullOrWhiteSpace($env:SIGNING_API_TOKEN))) { throw 'The powershell-gallery environment needs SIGNING_URL and SIGNING_API_TOKEN for Authenticode releases.' }
}
function Test-PublishedPackage([string] $Version, [string] $ModuleDirectory) {
    $package = Join-Path $work "QlikCloudCLI.$Version.nupkg"
    Invoke-WebRequest -Uri "https://www.powershellgallery.com/api/v2/package/QlikCloudCLI/$Version" -OutFile $package | Out-Null
    Assert-GalleryPackageContent $package $ModuleDirectory
}
function Build-Candidate([string] $Version) {
    $resultPath = Join-Path $work 'build-result.json'
    & (Join-Path $PSScriptRoot 'Test-Release.ps1') -RepositoryRoot $root -ArtifactsPath (Join-Path $work 'validation') -Version $Version -Sign:($SigningMode -eq 'Authenticode') -ResultPath $resultPath | Out-Host
    $result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
    if ($result.version -cne $Version) { throw 'Build result version differs from the release candidate.' }
    Assert-SafeArchive $result.packagePath $result.packageSha256
    Assert-ModuleFileHashes $result.moduleDirectory
    Assert-ReleaseSigning $result.moduleDirectory $SigningMode
    Test-ModuleManifest -Path $result.manifestPath -ErrorAction Stop | Out-Null
    Import-Module Microsoft.PowerShell.PSResourceGet -RequiredVersion 1.1.1 -ErrorAction Stop
    $galleryDirectory = Join-Path $work 'gallery-package'
    $null = [IO.Directory]::CreateDirectory($galleryDirectory)
    $nupkg = Compress-PSResource -Path $result.moduleDirectory -DestinationPath $galleryDirectory -PassThru -ErrorAction Stop
    Assert-GalleryPackageContent $nupkg.FullName $result.moduleDirectory
    $result | Add-Member -NotePropertyName galleryPackagePath -NotePropertyValue $nupkg.FullName
    $result | Add-Member -NotePropertyName galleryPackageSha256 -NotePropertyValue ((Get-FileHash -LiteralPath $nupkg.FullName).Hash.ToLowerInvariant())
    return $result
}
function Publish-Candidate($State, $Built) {
    Assert-PublisherConfiguration
    $tag = 'v' + $State.version
    $commit = Invoke-Native git @('rev-parse', "$tag^{commit}")
    $null = Invoke-Native git @('merge-base', '--is-ancestor', $commit, 'origin/main')
    $tagState = (Invoke-Native git @('show', "${tag}:.github/schema-release.json")) | ConvertFrom-Json
    if ($tagState.version -cne $State.version -or $tagState.schemaDigest -cne $State.schemaDigest) { throw 'Release tag points to a different candidate.' }
    # The immutable tag owns lifecycle notes on every retry, even if main later
    # contains another state or a newer inventory implementation.
    $State = $tagState
    $releaseNotes = Get-SchemaReleaseNotes -State $State -Commit $commit
    $release = Get-Release $tag
    if ($null -eq $release) {
        if (Test-GalleryVersion $State.version) { throw 'This Gallery version already exists without matching GitHub release provenance; refusing to overwrite or adopt it.' }
        $notes = Join-Path $work 'release-notes.md'
        [IO.File]::WriteAllText($notes, $releaseNotes, [Text.UTF8Encoding]::new($false))
        $null = Invoke-Native gh @('release','create',$tag,'--repo',$repository,'--verify-tag','--draft','--title',"QlikCloudCLI $($State.version)",'--notes-file',$notes)
        $release = Get-Release $tag
    }
    $receiptAsset = @($release.assets | Where-Object name -CEQ 'release.json')
    if ($receiptAsset.Count -eq 0) {
        if (Test-GalleryVersion $State.version) { throw 'Gallery has this version, but the durable release receipt is missing; manual recovery is required.' }
        if ($null -eq $Built) {
            $null = Invoke-Native git @('checkout','--detach',$tag)
            if ((Get-SchemaInventory $schemaPath).Digest -cne $State.schemaDigest) { throw 'Tagged schema snapshot differs from candidate state.' }
            $Built = Build-Candidate $State.version
        }
        $archiveName = [IO.Path]::GetFileName($Built.packagePath)
        $receipt = [ordered]@{ formatVersion = 1; version = $State.version; tag = $tag; commit = $commit; schemaDigest = $State.schemaDigest; packageFileName = $archiveName; packageSha256 = $Built.packageSha256; galleryPackageFileName = [IO.Path]::GetFileName($Built.galleryPackagePath); galleryPackageSha256 = $Built.galleryPackageSha256; signingMode = $SigningMode }
        $receiptPath = Join-Path $work 'release.json'
        Write-ReleaseJson $receiptPath $receipt
        foreach ($asset in $release.assets) {
            if ($asset.name -like 'QlikCloudCLI-*.zip' -or $asset.name -like 'QlikCloudCLI-*.zip.sha256' -or $asset.name -like 'QlikCloudCLI.*.nupkg') {
                $null = Invoke-Native gh @('release','delete-asset',$tag,$asset.name,'--repo',$repository,'--yes')
            }
        }
        # The receipt is uploaded last: a retry can replace incomplete package assets
        # before Gallery publication, but can never invent new bytes after it.
        $null = Invoke-Native gh @('release','upload',$tag,$Built.packagePath,($Built.packagePath + '.sha256'),$Built.galleryPackagePath,'--repo',$repository,'--clobber')
        $null = Invoke-Native gh @('release','upload',$tag,$receiptPath,'--repo',$repository)
    }
    $downloads = Join-Path $work 'release-assets'
    $null = [IO.Directory]::CreateDirectory($downloads)
    $null = Invoke-Native gh @('release','download',$tag,'--repo',$repository,'--pattern','release.json','--dir',$downloads)
    $receipt = Get-Content -LiteralPath (Join-Path $downloads 'release.json') -Raw | ConvertFrom-Json
    if ($receipt.formatVersion -ne 1 -or $receipt.version -cne $State.version -or $receipt.commit -cne $commit -or $receipt.schemaDigest -cne $State.schemaDigest -or $receipt.signingMode -cne $SigningMode) { throw 'Durable release provenance does not match the candidate or signing policy.' }
    if ([IO.Path]::GetFileName($receipt.packageFileName) -cne $receipt.packageFileName -or $receipt.packageFileName -notlike 'QlikCloudCLI-*.zip') { throw 'Invalid archive name in release receipt.' }
    $null = Invoke-Native gh @('release','download',$tag,'--repo',$repository,'--pattern',$receipt.packageFileName,'--dir',$downloads)
    $archive = Join-Path $downloads $receipt.packageFileName
    Assert-SafeArchive $archive $receipt.packageSha256
    if ($receipt.galleryPackageFileName -cne "QlikCloudCLI.$($State.version).nupkg") { throw 'Invalid Gallery package filename in release receipt.' }
    $null = Invoke-Native gh @('release','download',$tag,'--repo',$repository,'--pattern',$receipt.galleryPackageFileName,'--dir',$downloads)
    $galleryPackage = Join-Path $downloads $receipt.galleryPackageFileName
    if ((Get-FileHash -LiteralPath $galleryPackage).Hash -ine $receipt.galleryPackageSha256) { throw 'Gallery package checksum differs from the release receipt.' }
    $expanded = Join-Path $work 'release-module'
    [IO.Compression.ZipFile]::ExtractToDirectory($archive, $expanded)
    $module = Join-Path $expanded 'QlikCloudCLI'
    Assert-ModuleFileHashes $module
    Assert-ReleaseSigning $module $SigningMode
    Assert-GalleryPackageContent $galleryPackage $module
    $manifest = Join-Path $module 'QlikCloudCLI.psd1'
    if ([string](Import-PowerShellDataFile $manifest).ModuleVersion -cne $State.version) { throw 'Archive manifest version differs from candidate.' }
    Test-ModuleManifest -Path $manifest -ErrorAction Stop | Out-Null
    if (-not (Test-GalleryVersion $State.version)) {
        # Version is pinned on the CI publisher; customers do not need this module.
        Import-Module Microsoft.PowerShell.PSResourceGet -RequiredVersion 1.1.1 -ErrorAction Stop
        Publish-PSResource -NupkgPath $galleryPackage -Repository PSGallery -ApiKey $env:PSGALLERY_API_KEY -ErrorAction Stop
    }
    $verified = $false
    for ($attempt = 0; $attempt -lt 6; $attempt++) {
        try { Test-PublishedPackage $State.version $module; $verified = $true; break }
        catch {
            if ($attempt -eq 5) { throw }
            Write-Host 'Waiting for the published Gallery package to become available for verification.'
            Start-Sleep -Seconds 10
        }
    }
    if (-not $verified) { throw 'Gallery publication could not be verified.' }
    $release = Get-Release $tag
    if ($release.isDraft) { $null = Invoke-Native gh @('release','edit',$tag,'--repo',$repository,'--draft=false') }
    Write-Host "Published and verified QlikCloudCLI $($State.version) at https://github.com/$repository/releases/tag/$tag"
}

Push-Location $root
try {
    if ($env:GITHUB_REPOSITORY -cne $repository -or $env:GITHUB_REF -cne 'refs/heads/main' -or $env:GITHUB_EVENT_NAME -cnotin @('schedule','workflow_dispatch')) {
        throw 'Publication is restricted to the scheduled or manually dispatched workflow on Nillth/QlikCloudCLI main.'
    }
    $origin = Invoke-Native git @('remote','get-url','origin')
    if ($origin -cnotin @('https://github.com/Nillth/QlikCloudCLI','https://github.com/Nillth/QlikCloudCLI.git','git@github.com:Nillth/QlikCloudCLI.git')) { throw 'Unexpected release repository origin.' }
    if (Invoke-Native git @('status','--porcelain')) { throw 'Release requires a clean checkout.' }
    $null = Invoke-Native git @('fetch','origin','main','--tags')
    if ((Invoke-Native git @('rev-parse','HEAD')) -cne (Invoke-Native git @('rev-parse','origin/main'))) { throw 'Release checkout is no longer the current main commit; rerun the workflow.' }
    $version = (Get-Content -LiteralPath $versionPath -Raw | ConvertFrom-Json).version
    $baseline = Get-SchemaInventory $schemaPath
    if (Test-Path -LiteralPath $statePath) {
        $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
        if ($state.version -ceq $version) {
            $existing = Get-Release ('v' + $state.version)
            $galleryPublished = Test-GalleryVersion $version
            $disposition = Get-ReleaseDisposition -Version $version -CandidateVersion $state.version -ReleaseExists:($null -ne $existing) -ReleaseIsDraft:($null -ne $existing -and $existing.isDraft) -GalleryPublished:$galleryPublished
            if ($disposition -eq 'Resume') {
                Write-Host "Resuming existing release candidate $version; no additional version increment."
                Publish-Candidate $state $null
                return
            }
        }
    }
    $refreshed = Join-Path $work 'schemas'
    & dotnet run --project (Join-Path $root 'tools/QlikCloud.Generator') --configuration Release -- refresh --output $refreshed
    if ($LASTEXITCODE -ne 0) { throw "Schema refresh failed (exit $LASTEXITCODE)." }
    $current = Get-SchemaInventory $refreshed
    $difference = Compare-SchemaInventory $baseline $current
    if (-not $difference.Changed -and -not $PublishInitial) { Write-Host 'No OpenAPI schema changes; no version increment or publication.'; return }
    if ($PublishInitial -and (Test-Path -LiteralPath $statePath)) { throw 'publish_initial is only for the first release; an existing release state is present.' }
    Assert-PublisherConfiguration
    $candidateVersion = if ($PublishInitial) { $version } else { Get-NextPatchVersion $version }
    if (Test-GalleryVersion $candidateVersion) { throw "Gallery already contains $candidateVersion; choose a new version through a reviewed source change." }
    # Lifecycle changes are release notes, not a release veto. Snapshot copying
    # still verifies ownership and safely removes obsolete schema documents.
    Copy-SchemaSnapshot -Source $refreshed -Destination $schemaPath
    Write-ReleaseJson $versionPath ([ordered]@{ version = $candidateVersion })
    $state = New-SchemaReleaseState -Version $candidateVersion -PreviousVersion $version -Inventory $current -Difference $difference
    $null = Get-SchemaReleaseNotes -State $state -Commit 'pending validation'
    Write-ReleaseJson $statePath $state
    $null = Invoke-Native git @('config','user.name','github-actions[bot]')
    $null = Invoke-Native git @('config','user.email','41898282+github-actions[bot]@users.noreply.github.com')
    $null = Invoke-Native git @('add','--','schemas/qlik-cloud','version.json','.github/schema-release.json')
    $null = Invoke-Native git @('commit','-m',"Release QlikCloudCLI $candidateVersion from updated OpenAPI schemas")
    $null = Invoke-Native git @('tag',('v' + $candidateVersion))
    # Build from the exact local release commit, so SourceRevisionId and resumed
    # builds refer to the same source. Nothing is pushed until every check passes.
    $built = Build-Candidate $candidateVersion
    if (Invoke-Native git @('status','--porcelain')) { throw 'Release validation modified source files; refusing to publish an uncommitted build.' }
    $null = Invoke-Native git @('push','--atomic','origin','HEAD:main',('refs/tags/v' + $candidateVersion))
    $null = Invoke-Native git @('fetch','origin','main')
    Publish-Candidate ([pscustomobject]$state) $built
}
finally { Pop-Location }
