#requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $ModuleDirectory
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'Module Authenticode signing requires Windows.' }

$moduleRoot = [IO.Path]::GetFullPath($ModuleDirectory)
if (-not (Test-Path -LiteralPath $moduleRoot -PathType Container)) { throw "Module staging directory does not exist: $moduleRoot" }
$relativePaths = @('bin/QlikCloud.dll', 'bin/QlikCloudCLI.dll', 'QlikCloudCLI.psd1')
$unsignedHashes = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::Ordinal)
foreach ($relative in $relativePaths) {
    $path = Join-Path $moduleRoot $relative
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required signing input is missing: $relative" }
    $unsignedHashes.Add($relative, (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant())
}

$catalogPath = Join-Path $moduleRoot 'commands.json'
$catalog = Get-Content -LiteralPath $catalogPath -Raw | ConvertFrom-Json -AsHashtable
if ($catalog['assemblySha256'] -ine $unsignedHashes['bin/QlikCloud.dll']) {
    throw 'The unsigned staged SDK differs from the command catalog; signing was not started.'
}
if ($catalog.ContainsKey('unsignedAssemblySha256')) {
    throw 'The staged command catalog already contains signing provenance; start from a fresh unsigned stage.'
}

Import-Module (Join-Path $PSScriptRoot 'Signing.psm1') -Force -ErrorAction Stop
$records = [Collections.Generic.List[object]]::new()
$certificateThumbprint = $null
foreach ($relative in $relativePaths) {
    $path = Join-Path $moduleRoot $relative
    # Sign only the module's two assemblies and manifest. Vendor dependencies
    # remain byte-for-byte copies of the resolved build inputs.
    Invoke-ArtifactSigning -FilePath $path -SigningUrl $env:SIGNING_URL -ApiToken $env:SIGNING_API_TOKEN
    $signature = Get-AuthenticodeSignature -LiteralPath $path -ErrorAction Stop
    if ($signature.Status -ne [Management.Automation.SignatureStatus]::Valid) {
        throw "Authenticode verification failed for $relative`: $($signature.Status)."
    }
    if ($null -eq $signature.SignerCertificate -or $null -eq $signature.TimeStamperCertificate) {
        throw "A valid signer and timestamp certificate are required for $relative."
    }
    $thumbprint = $signature.SignerCertificate.Thumbprint.ToUpperInvariant()
    if ([string]::IsNullOrWhiteSpace($thumbprint)) { throw "The signer certificate has no thumbprint for $relative." }
    if ($null -eq $certificateThumbprint) { $certificateThumbprint = $thumbprint }
    elseif ($certificateThumbprint -cne $thumbprint) { throw "The signer certificate differs between module files: $relative." }
    $records.Add([ordered]@{
        path = $relative
        unsignedSha256 = $unsignedHashes[$relative]
        signedSha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
        certificateThumbprint = $thumbprint
        certificateSubject = $signature.SignerCertificate.Subject
        status = [string]$signature.Status
        timestampPresent = $true
        timestampCertificateThumbprint = $signature.TimeStamperCertificate.Thumbprint
        timestampCertificateSubject = $signature.TimeStamperCertificate.Subject
    })
}

# The generated catalog remains untouched. Its staged copy describes the bytes
# consumers receive, while retaining the exact generator-input SDK fingerprint.
$catalog['unsignedAssemblySha256'] = $catalog['assemblySha256']
$catalog['assemblySha256'] = $records[0].signedSha256
$catalog | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $catalogPath -Encoding utf8
[ordered]@{
    formatVersion = 1
    signatureType = 'Authenticode'
    verifiedAtUtc = [datetime]::UtcNow.ToString('o')
    files = $records.ToArray()
} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $moduleRoot 'signing.json') -Encoding utf8
Write-Host 'Verified timestamped Authenticode signatures for both module assemblies and the manifest.'
