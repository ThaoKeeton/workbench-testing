[CmdletBinding()]
param([string]$Directory, [switch]$AllowReferenceOnly, [switch]$RequireCurrentSource, [switch]$HelperGuardsOnly)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot '../../scripts/TestingPluginDelivery.psm1') -Force
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$temp = Join-Path ([IO.Path]::GetTempPath()) ('testing-package-regression-' + [Guid]::NewGuid().ToString('N'))
$hiddenParent = Join-Path $temp '.hidden-parent'
$hiddenLink = Join-Path $hiddenParent 'linked-parent'
function Refuses([scriptblock]$Action, [string]$Expected) {
    try { & $Action | Out-Null }
    catch {
        if ($_.Exception.Message -cne $Expected) { throw "Expected $Expected; observed $($_.Exception.Message)" }
        Write-Host "Exact refusal: $Expected"; return
    }
    throw "Expected $Expected; corrupt testing delivery was accepted."
}
if ($HelperGuardsOnly) {
    $module = Get-Module TestingPluginDelivery
    $descriptor = @{ Available = $true; ImageSha = '1' * 64; NativeSourceSha = '2' * 64; SourceCommit = '3' * 40; Msvc = 'actual-msvc-required'; Sdk = 'actual-sdk-required'; Profile = 'Production' }
    Refuses { & $module { param($d) Assert-TestingOwnedHelper -Image ([byte[]]::new(0)) -Descriptor $d } $descriptor } 'TestingOwnedHelperInvalid'
    Refuses { & $module { param($d) Assert-TestingOwnedHelper -Image ([byte[]]::new(1024)) -Descriptor $d } $descriptor } 'TestingOwnedHelperInvalid'
    $descriptor.Profile = 'ReferenceOnly'; $descriptor.Available = $false
    Refuses { & $module { param($d) Assert-TestingOwnedHelper -Image ([byte[]]::new(1024)) -Descriptor $d } $descriptor } 'TestingOwnedHelperDescriptorInvalid'
    Refuses { New-TestingPluginDelivery -BuildDirectory $temp -OutputDirectory (Join-Path $temp 'delivery') -ReferenceOnly } 'TestingDeliveryReferenceOnly'
    Refuses { New-TestingPluginDelivery -BuildDirectory $temp -OutputDirectory (Join-Path $temp 'delivery') } 'TestingOwnedHelperMissing'
    if (Test-Path (Join-Path $temp 'delivery')) { throw 'Missing/reference helper created a production output.' }
    $records = @(& $module { param($root) Get-TestingSourceFiles $root } $repo | Where-Object path -CLike 'native/Workbench.OwnedLab/*' | Sort-Object path -CaseSensitive)
    $sourceText = ($records | ForEach-Object { "$($_.path) $($_.length) $($_.sha256)`n" }) -join ''
    $sourceText += "profile=Production`nmacro=`n"
    $descriptor.NativeSourceSha = [Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData([Text.UTF8Encoding]::new($false).GetBytes($sourceText)))
    $build = @{ sources = $records; staticCrt = $true; macro = '' }
    & $module { param($b,$d) Assert-TestingOwnedSource $b $d } $build $descriptor
    $build.macro = 'FORBIDDEN_PRODUCTION_FAULT'
    Refuses { & $module { param($b,$d) Assert-TestingOwnedSource $b $d } $build $descriptor } 'TestingOwnedHelperSourceInvalid'
    $build.macro = ''; $records[0].sha256 = '0' * 64
    Refuses { & $module { param($b,$d) Assert-TestingOwnedSource $b $d } $build $descriptor } 'TestingOwnedHelperSourceInvalid'
    $allSources = @(& $module { param($root) Get-TestingSourceFiles $root } $repo)
    foreach ($path in @('scripts/OwnedLabProbeStaging.psm1','tests/scripts/OwnedLabProbeStaging.Tests.ps1','scripts/OwnedLabProbeOutput.psm1','tests/scripts/OwnedLabProbeOutput.Tests.ps1',
        'plugins/Workbench.Testing/NativeActivityCoordinator.cs','tests/Workbench.Testing.Tests/TestingNativeActivityTests.cs','tests/Workbench.Testing.Tests/TestingControllerAdmissionTests.cs','tests/Workbench.Testing.Tests/Workbench.Testing.Tests.csproj',
        'plugins/Workbench.Testing/NativeOwnedPilot.cs','plugins/Workbench.Testing/NativeOwnedEvidence.cs','plugins/Workbench.Testing/NativeTestingLifecycle.cs','plugins/Workbench.Testing/TestingOwnedPilotPanel.cs',
        'plugins/Workbench.Testing/NativeInvocationFeedback.cs','tests/Workbench.Testing.Tests/TestingNativeFeedbackTests.cs',
        'plugins/Workbench.Testing/TestingSourceServices.cs','plugins/Workbench.Testing/TestingPlugin.cs','plugins/Workbench.Testing/Workbench.Testing.csproj','plugins/Workbench.Testing/plugin.json',
        'tests/Workbench.Testing.SourceAcceptance/Workbench.Testing.SourceAcceptance.csproj','tests/Workbench.Testing.SourceAcceptance/plugin.json',
        'tests/Workbench.Testing.SourceAcceptance/SourceAcceptancePlugin.cs','tests/Workbench.Testing.SourceAcceptance/SourceAcceptanceProtocol.cs',
        'tests/Workbench.Testing.SourceAcceptance/SourceAcceptanceScenarioLedger.cs','tests/Workbench.Testing.SourceAcceptance/SourceAcceptanceHttpHandlers.cs','tests/Workbench.Testing.SourceAcceptance/SourceAcceptanceOwnership.cs',
        'eng/host-sdk.lock.json','eng/migration-source.json','scripts/prepare-host-sdk.py',
        'tests/Workbench.Testing.Tests/SourceAcceptanceTests.cs','tests/Workbench.Testing.Tests/SourceAcceptanceTests.Ownership.cs','tests/Workbench.Testing.Tests/SourceAcceptanceTests.Transport.cs',
        'docs/testing-plugin.md','docs/windows-verification.md','.github/workflows/build.yml','scripts/TestingPluginDelivery.psm1','tests/scripts/TestingPluginPackage.Tests.ps1',
        'tests/Workbench.Testing.Tests/TestingNativeOwnedPilotTests.cs','tests/Workbench.Testing.Tests/TestingNativeOwnedEvidenceTests.cs','tests/Workbench.Testing.Tests/TestingNativeLifecycleTests.cs',
        'tests/Workbench.Testing.Tests/OwnedAdmissionTests.cs','tests/Workbench.Testing.Tests/OwnedJournalTests.cs','tests/Workbench.Testing.Tests/OwnedFixedPlansTests.cs')) {
        $record = @($allSources | Where-Object path -CEQ $path)
        $file = Get-Item -LiteralPath (Join-Path $repo $path)
        if ($record.Count -ne 1 -or $record[0].length -ne $file.Length -or $record[0].sha256 -cne (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()) { throw "Native activity/staging source is missing or incorrect in delivery provenance: $path" }
        $changed = @($allSources | ForEach-Object { @{ path = $_.path; length = $_.length; sha256 = $(if ($_.path -ceq $path) { '0' * 64 } else { $_.sha256 }) } })
        Refuses { & $module { param($current,$recorded) Assert-TestingSourceRecords -Current $current -Recorded $recorded } $allSources $changed } 'TestingSourceHashMismatch'
        Write-Host "Source hash corruption refused: $path"
    }
    Write-Host 'Owned helper packaging guards passed; synthetic guard checks do not establish native execution.'
    return
}
if (-not $Directory) { throw 'Pass the actual delivery directory or -HelperGuardsOnly.' }
Test-TestingPluginDelivery -Directory $Directory -AllowReferenceOnly:$AllowReferenceOnly -RequireCurrentSource:$RequireCurrentSource
function Refresh-Sums([string]$Root) {
    @('README.md','provenance.json','workbench-testing-plugin-0.2.0.zip' | ForEach-Object { (Get-FileHash (Join-Path $Root $_) -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $_ }) | Set-Content (Join-Path $Root 'SHA256SUMS')
}
try {
    $hiddenDelivery = Join-Path $hiddenParent 'delivery'
    New-Item $hiddenDelivery -ItemType Directory -Force | Out-Null
    # Dot names are Hidden on Unix; Windows requires the actual filesystem attribute.
    if ($IsWindows) { [IO.File]::SetAttributes($hiddenParent, [IO.File]::GetAttributes($hiddenParent) -bor [IO.FileAttributes]::Hidden) }
    $hiddenItem = Get-Item -LiteralPath $hiddenParent -Force
    if (($hiddenItem.Attributes -band [IO.FileAttributes]::Hidden) -eq 0) { throw 'Hidden-parent regression requires an actually hidden directory.' }
    Write-Host "Hidden ancestor confirmed: $($hiddenItem.Attributes); explicitly set on Windows: $IsWindows."
    Copy-Item (Join-Path $Directory '*') $hiddenDelivery -Recurse
    Test-TestingPluginDelivery -Directory $hiddenDelivery -AllowReferenceOnly:$AllowReferenceOnly
    Write-Host 'Valid testing delivery accepted beneath the actual hidden ancestor.'
    Add-Content (Join-Path $hiddenDelivery 'README.md') 'tamper'
    Refuses { Test-TestingPluginDelivery -Directory $hiddenDelivery -AllowReferenceOnly:$AllowReferenceOnly } 'TestingDeliveryHashMismatch'
    Copy-Item -LiteralPath (Join-Path $Directory 'README.md') -Destination (Join-Path $hiddenDelivery 'README.md') -Force
    $linkTarget = Join-Path $temp 'link-target'
    New-Item $linkTarget -ItemType Directory | Out-Null
    Copy-Item -LiteralPath $hiddenDelivery -Destination (Join-Path $linkTarget 'delivery') -Recurse
    if ($IsWindows) { New-Item -Path $hiddenLink -ItemType Junction -Target $linkTarget | Out-Null }
    else { [IO.Directory]::CreateSymbolicLink($hiddenLink, $linkTarget) | Out-Null }
    if (((Get-Item -LiteralPath $hiddenLink -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) { throw 'Hidden-parent regression requires an actual linked ancestor.' }
    Refuses { Test-TestingPluginDelivery -Directory (Join-Path $hiddenLink 'delivery') -AllowReferenceOnly:$AllowReferenceOnly } 'TestingDeliveryLinkRefused'
    Remove-Item -LiteralPath $hiddenLink -Force
    Remove-Item $temp -Recurse -Force
    New-Item $temp -ItemType Directory | Out-Null
    Copy-Item (Join-Path $Directory '*') $temp -Recurse
    Add-Content (Join-Path $temp 'README.md') 'tamper'
    Refuses { Test-TestingPluginDelivery -Directory $temp -AllowReferenceOnly:$AllowReferenceOnly } 'TestingDeliveryHashMismatch'
    Remove-Item $temp -Recurse -Force
    New-Item $temp -ItemType Directory | Out-Null
    Copy-Item (Join-Path $Directory '*') $temp -Recurse
    New-Item (Join-Path $temp 'Workbench.Sdk.dll') -ItemType File | Out-Null
    Refuses { Test-TestingPluginDelivery -Directory $temp -AllowReferenceOnly:$AllowReferenceOnly } 'TestingDeliveryFileListInvalid'
    Remove-Item $temp -Recurse -Force
    New-Item $temp -ItemType Directory | Out-Null
    Copy-Item (Join-Path $Directory '*') $temp -Recurse
    $metadata = Get-Content (Join-Path $temp 'provenance.json') -Raw | ConvertFrom-Json -AsHashtable
    if ($metadata.referenceOnly) { Refuses { Test-TestingPluginDelivery -Directory $temp } 'TestingDeliveryReferenceOnly' }
    $module = Get-Module TestingPluginDelivery
    $sourceRecords = @($metadata.sourceFiles)
    & $module { param($records, $root) Assert-TestingSourceRecords -Current @(Get-TestingSourceFiles $root) -Recorded $records } $sourceRecords $repo
    $metadata.sourceFiles[0].sha256 = '0' * 64
    $metadata | ConvertTo-Json -Depth 20 | Set-Content (Join-Path $temp 'provenance.json')
    Refresh-Sums $temp
    # This deliberately dirty/ReferenceOnly fixture passes all outer guards after sums refresh.
    # Invoke the SAME private source-record guard used by RequireCurrentSource, without inventing clean provenance.
    Test-TestingPluginDelivery -Directory $temp -AllowReferenceOnly:$AllowReferenceOnly
    Refuses { & $module { param($records, $root) Assert-TestingSourceRecords -Current @(Get-TestingSourceFiles $root) -Recorded $records } @($metadata.sourceFiles) $repo } 'TestingSourceHashMismatch'
    Refuses { & $module { param($records, $root) Assert-TestingSourceRecords -Current @(Get-TestingSourceFiles $root) -Recorded $records } @($metadata.sourceFiles | Select-Object -Skip 1) $repo } 'TestingSourceFileListInvalid'
    & $module { Assert-TestingSourceIdentity -RecordedCommit ('a' * 40) -RecordedDirty $false -CurrentCommit ('a' * 40) -CurrentDirty $false }
    Refuses { & $module { Assert-TestingSourceIdentity -RecordedCommit ('b' * 40) -RecordedDirty $false -CurrentCommit ('a' * 40) -CurrentDirty $false } } 'TestingCurrentSourceMismatch'
    Refuses { & $module { Assert-TestingSourceIdentity -RecordedCommit ('a' * 40) -RecordedDirty $true -CurrentCommit ('a' * 40) -CurrentDirty $false } } 'TestingCurrentSourceMismatch'
    Refuses { & $module { Assert-TestingSourceIdentity -RecordedCommit ('a' * 40) -RecordedDirty $false -CurrentCommit ('a' * 40) -CurrentDirty $true } } 'TestingCurrentSourceMismatch'
    foreach ($mutation in @('extra-sdk','duplicate-manifest','changed-entry','wrong-manifest')) {
        Remove-Item $temp -Recurse -Force
        New-Item $temp -ItemType Directory | Out-Null
        Copy-Item (Join-Path $Directory '*') $temp -Recurse
        $zipPath = Join-Path $temp 'workbench-testing-plugin-0.2.0.zip'
        $archive = [IO.Compression.ZipFile]::Open($zipPath, [IO.Compression.ZipArchiveMode]::Update)
        try {
            if ($mutation -eq 'extra-sdk') { $entry = $archive.CreateEntry('Workbench.Sdk.dll'); $text = 'forbidden SDK copy' }
            elseif ($mutation -eq 'duplicate-manifest') { $entry = $archive.CreateEntry('plugin.json'); $text = '{}' }
            elseif ($mutation -eq 'changed-entry') { $archive.GetEntry('Workbench.Testing.dll').Delete(); $entry = $archive.CreateEntry('Workbench.Testing.dll'); $text = 'changed payload' }
            else {
                $entry = $archive.GetEntry('plugin.json')
                $reader = [IO.StreamReader]::new($entry.Open()); try { $manifest = $reader.ReadToEnd() | ConvertFrom-Json -AsHashtable } finally { $reader.Dispose() }
                $manifest.minimumHostVersion = '0.3.0'; $text = $manifest | ConvertTo-Json -Depth 10
                $entry.Delete(); $entry = $archive.CreateEntry('plugin.json')
            }
            $writer = [IO.StreamWriter]::new($entry.Open(), [Text.UTF8Encoding]::new($false)); try { $writer.Write($text) } finally { $writer.Dispose() }
        }
        finally { $archive.Dispose() }
        $metadata = Get-Content (Join-Path $temp 'provenance.json') -Raw | ConvertFrom-Json -AsHashtable
        $metadata.zip.length = (Get-Item $zipPath).Length
        $metadata.zip.sha256 = (Get-FileHash $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($mutation -eq 'wrong-manifest') {
            $record = @($metadata.payload | Where-Object path -CEQ 'plugin.json')[0]
            $bytes = [Text.UTF8Encoding]::new($false).GetBytes($text)
            $record.length = $bytes.Length; $record.sha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
        }
        $metadata | ConvertTo-Json -Depth 20 | Set-Content (Join-Path $temp 'provenance.json')
        Refresh-Sums $temp
        $expected = switch ($mutation) { 'extra-sdk' { 'TestingPayloadFileListInvalid' }; 'duplicate-manifest' { 'TestingPayloadFileListInvalid' }; 'changed-entry' { 'TestingPayloadHashMismatch' }; 'wrong-manifest' { 'TestingManifestInvalid' } }
        Refuses { Test-TestingPluginDelivery -Directory $temp -AllowReferenceOnly:$AllowReferenceOnly } $expected
    }
    Write-Host 'Testing package regressions passed: actual hidden-parent acceptance/tamper/link guards, independently attributed outer checksum, file list, ReferenceOnly, source hash/list/three identity refusals, and four ZIP/payload/manifest refusals.'

}
finally {
    if (Test-Path -LiteralPath $hiddenLink) { Remove-Item -LiteralPath $hiddenLink -Force }
    if (Test-Path $temp) { Remove-Item $temp -Recurse -Force }
}
