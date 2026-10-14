#requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $BuildDirectory,
    [Parameter(Mandatory)][string] $GeneratedDirectory,
    [Parameter(Mandatory)][string] $FileList,
    [Parameter(Mandatory)][version] $ModuleVersion,
    [switch] $Sign
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-ChildPath([string] $Path, [string] $Parent) {
    $absolute = [IO.Path]::GetFullPath($Path)
    $prefix = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($Parent)) + [IO.Path]::DirectorySeparatorChar
    if (-not $absolute.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Path is outside the module staging directory: $absolute"
    }
    return $absolute
}

function Assert-NoLinkedParents([string] $Path) {
    $candidate = [IO.Path]::GetFullPath($Path)
    while ($candidate) {
        if (Test-Path -LiteralPath $candidate) {
            $item = Get-Item -LiteralPath $candidate -Force
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Linked staging paths are not supported: $candidate" }
        }
        $candidate = [IO.Path]::GetDirectoryName($candidate)
    }
}

$buildRoot = [IO.Path]::GetFullPath($BuildDirectory)
$generatedRoot = [IO.Path]::GetFullPath($GeneratedDirectory)
$moduleParent = Assert-ChildPath (Join-Path $buildRoot 'module') $buildRoot
$destination = Assert-ChildPath (Join-Path $moduleParent 'QlikCloudCLI') $moduleParent
Assert-NoLinkedParents $destination
if (-not (Test-Path -LiteralPath (Join-Path $buildRoot 'QlikCloudCLI.dll') -PathType Leaf)) { throw 'Build QlikCloudCLI.dll before staging its module.' }
$inventory = Get-Content -LiteralPath (Join-Path $generatedRoot 'commands.json') -Raw | ConvertFrom-Json
if ($inventory.formatVersion -ne 2) { throw 'Module staging requires command inventory format 2.' }
$restCommands = @($inventory.commands.commandName)
if ($inventory.commandCount -ne $restCommands.Count -or @($restCommands | Sort-Object -Unique).Count -ne $restCommands.Count) {
    throw 'Command inventory contains an invalid REST command count or duplicate command definitions.'
}
$operationGroups = @($inventory.operations | Group-Object commandName)
if ($operationGroups.Count -ne $restCommands.Count -or @(Compare-Object ($operationGroups.Name | Sort-Object) ($restCommands | Sort-Object)).Count) {
    throw 'REST command definitions do not match the operation inventory.'
}
foreach ($definition in $inventory.commands) {
    $operations = @($inventory.operations | Where-Object commandName -EQ $definition.commandName)
    $setNames = @($operations.parameterSetName)
    if ($definition.operationCount -ne $operations.Count -or $setNames.Count -gt 32 -or
        @($setNames | Where-Object { [string]::IsNullOrWhiteSpace($_) }).Count -or
        @($setNames | Sort-Object -Unique).Count -ne $setNames.Count -or
        @($definition.parameterSetNames).Count -ne $setNames.Count -or
        @(Compare-Object ($setNames | Sort-Object) ($definition.parameterSetNames | Sort-Object)).Count -or
        $setNames -notcontains $definition.defaultParameterSetName) {
        throw "Invalid parameter-set inventory for $($definition.commandName)."
    }
}
$commands = $restCommands + @($inventory.constructors.commandName) + @(
    'Connect-QlikCloud', 'Disconnect-QlikCloud', 'Get-QCConnection',
    'Receive-QCToken', 'Get-QlikUserAgent', 'Invoke-OpenApp'
)
if (@($commands | Where-Object { [string]::IsNullOrWhiteSpace($_) -or $_ -notmatch '^[A-Za-z]+-[A-Za-z0-9]+$' }).Count) {
    throw 'Command inventory contains an invalid PowerShell command name.'
}
if (@($commands | Sort-Object -Unique).Count -ne $commands.Count) { throw 'Command inventory contains duplicate exports.' }

New-Item -ItemType Directory -Path $moduleParent -Force | Out-Null
$lease = [IO.File]::Open((Join-Path $moduleParent '.qlikcloudcli-stage.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
$stage = Assert-ChildPath (Join-Path $moduleParent ('.QlikCloudCLI.stage_' + [guid]::NewGuid().ToString('N'))) $moduleParent
$backup = Assert-ChildPath (Join-Path $moduleParent ('.QlikCloudCLI.previous_' + [guid]::NewGuid().ToString('N'))) $moduleParent
$markerName = '.qlikcloudcli-module.json'
try {
    if (Test-Path -LiteralPath $destination) {
        $marker = Join-Path $destination $markerName
        if (-not (Test-Path -LiteralPath $marker -PathType Leaf)) { throw "Refusing to replace an unowned module directory: $destination" }
        $previous = Get-Content -LiteralPath $marker -Raw | ConvertFrom-Json
        if ($previous.generator -cne 'QlikCloudCLI.Module') { throw "Module directory belongs to another tool: $destination" }
        foreach ($item in Get-ChildItem -LiteralPath $destination -Recurse -Force) {
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Linked module content cannot be replaced: $($item.FullName)" }
        }
    }
    New-Item -ItemType Directory -Path (Join-Path $stage 'bin') -Force | Out-Null
    $copied = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in Get-Content -LiteralPath $FileList) {
        if ([string]::IsNullOrWhiteSpace($entry)) { continue }
        $parts = $entry.Split('|')
        if ($parts.Length -ne 2) { throw "Malformed resolved module file entry: $entry" }
        $source = [IO.Path]::GetFullPath($parts[0])
        $relative = $parts[1].Replace('\', '/')
        if ([IO.Path]::IsPathRooted($relative) -or $relative.Split('/') -contains '..') { throw "Invalid module file name: $relative" }
        # Debug symbols belong to build output, not the consumer distribution.
        if ([IO.Path]::GetExtension($relative) -ieq '.pdb') { continue }
        if ([IO.Path]::GetFileName($relative) -eq 'System.Management.Automation.dll') { throw 'The host PowerShell assembly must not be packaged.' }
        $target = Assert-ChildPath (Join-Path (Join-Path $stage 'bin') $relative) $stage
        $hash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
        if ($copied.ContainsKey($relative)) {
            if ($copied[$relative] -cne $hash) { throw "Different dependencies target the same module file: $relative" }
            continue
        }
        $copied.Add($relative, $hash)
        New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($target)) -Force | Out-Null
        Copy-Item -LiteralPath $source -Destination $target
    }
    foreach ($required in @('QlikCloud.dll', 'QlikCloudCLI.dll')) {
        if (-not $copied.ContainsKey($required)) { throw "Resolved module file list is missing $required" }
    }
    Copy-Item -LiteralPath (Join-Path $generatedRoot 'commands.json') -Destination (Join-Path $stage 'commands.json')
    Copy-Item -LiteralPath (Join-Path $generatedRoot 'COMMANDS.md') -Destination (Join-Path $stage 'COMMANDS.md')
    Copy-Item -LiteralPath (Join-Path $buildRoot 'QlikCloud.operations.json') -Destination (Join-Path $stage 'QlikCloud.operations.json')
    if ((Get-FileHash -LiteralPath (Join-Path $stage 'bin/QlikCloud.dll')).Hash -ine $inventory.assemblySha256) {
        throw 'The staged SDK differs from the assembly used to generate the PowerShell commands.'
    }
    if ((Get-FileHash -LiteralPath (Join-Path $stage 'QlikCloud.operations.json')).Hash -ine $inventory.clientManifestSha256) {
        throw 'The staged SDK operation inventory differs from the PowerShell generator input.'
    }
    $helpRoot = Join-Path $generatedRoot 'en-US'
    if (Test-Path -LiteralPath $helpRoot -PathType Container) {
        New-Item -ItemType Directory -Path (Join-Path $stage 'en-US') -Force | Out-Null
        foreach ($help in Get-ChildItem -LiteralPath $helpRoot -File -Filter '*.xml') {
            Copy-Item -LiteralPath $help.FullName -Destination (Join-Path $stage 'en-US')
        }
    }
    foreach ($name in @('README.md', 'HOW-TO.md', 'LLM.txt')) {
        $document = Join-Path $PSScriptRoot "../../docs/consumer/$name"
        if (-not (Test-Path -LiteralPath $document -PathType Leaf)) { throw "Consumer documentation is missing: $document" }
        Copy-Item -LiteralPath $document -Destination (Join-Path $stage $name)
    }
    foreach ($name in @('LICENSE', 'THIRD-PARTY-NOTICES.md')) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot "../../../$name") -Destination (Join-Path $stage $name)
    }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../../../licenses') -Destination (Join-Path $stage 'licenses') -Recurse
    $exportLines = ($commands | Sort-Object | ForEach-Object { "        '$_'" }) -join ",`n"
    $manifest = @"
@{
    RootModule = 'bin/QlikCloudCLI.dll'
    ModuleVersion = '$ModuleVersion'
    GUID = '375be3c3-bf96-4a36-ab66-f524a8ec80ab'
    Author = 'Marc Collins'
    CompanyName = 'NNet'
    Copyright = '(c) 2026 Marc Collins. Licensed under MIT; see LICENSE and THIRD-PARTY-NOTICES.md.'
    Description = 'Qlik Cloud REST and Engine PowerShell commands generated from the QlikCloud client catalog.'
    PowerShellVersion = '5.1'
    CompatiblePSEditions = @('Desktop', 'Core')
    RequiredAssemblies = @('bin/QlikCloud.dll')
    FunctionsToExport = @()
    CmdletsToExport = @(
$exportLines
    )
    VariablesToExport = @()
    AliasesToExport = @()
    PrivateData = @{ PSData = @{
        Tags = @('Qlik', 'QlikCloud', 'REST', 'PSEdition_Desktop', 'PSEdition_Core', 'Windows')
        ProjectUri = 'https://github.com/Nillth/QlikCloudCLI'
        LicenseUri = 'https://github.com/Nillth/QlikCloudCLI/blob/main/LICENSE'
        ReleaseNotes = 'https://github.com/Nillth/QlikCloudCLI/releases/tag/v$ModuleVersion'
        RequireLicenseAcceptance = `$false
    } }
}
"@
    [IO.File]::WriteAllText((Join-Path $stage 'QlikCloudCLI.psd1'), $manifest.Replace("`r`n", "`n") + "`n", [Text.UTF8Encoding]::new($false))
    if ($Sign) {
        # Keep signing inside this transaction: a failed/invalid signature leaves
        # the previously staged module intact, and final hashes include signatures.
        & (Join-Path $PSScriptRoot 'Sign-ModuleFiles.ps1') -ModuleDirectory $stage
    }
    $fileHashes = @(Get-ChildItem -LiteralPath $stage -File -Recurse | Sort-Object FullName | ForEach-Object {
        [ordered]@{ path = [IO.Path]::GetRelativePath($stage, $_.FullName).Replace('\', '/'); sha256 = (Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant() }
    })
    [ordered]@{ generator = 'QlikCloudCLI.Module'; formatVersion = 1; commandCount = $commands.Count; files = $fileHashes } |
        ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $stage $markerName) -Encoding utf8
    if (Test-Path -LiteralPath $destination) { Move-Item -LiteralPath $destination -Destination $backup }
    try { Move-Item -LiteralPath $stage -Destination $destination }
    catch {
        if ((Test-Path -LiteralPath $backup) -and -not (Test-Path -LiteralPath $destination)) { Move-Item -LiteralPath $backup -Destination $destination }
        throw
    }
    if (Test-Path -LiteralPath $backup) {
        $safeBackup = Assert-ChildPath $backup $moduleParent
        Remove-Item -LiteralPath $safeBackup -Recurse -Force
    }
    Write-Host "Staged $($commands.Count) commands in $destination"
}
finally {
    if (Test-Path -LiteralPath $stage) {
        $safeStage = Assert-ChildPath $stage $moduleParent
        Remove-Item -LiteralPath $safeStage -Recurse -Force
    }
    $lease.Dispose()
}
