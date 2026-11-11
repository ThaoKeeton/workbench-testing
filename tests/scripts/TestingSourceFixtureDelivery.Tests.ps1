[CmdletBinding()]
param([Parameter(Mandatory)][string]$Directory, [Parameter(Mandatory)][string]$ProductionDirectory)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot '../../scripts/TestingPluginDelivery.psm1') -Force
function Refuses([scriptblock]$Action, [string]$Expected) {
    try { & $Action | Out-Null } catch {
        if ($_.Exception.Message -cne $Expected) { throw "Expected $Expected; observed $($_.Exception.Message)" }; return
    }
    throw "Expected $Expected; altered source fixture was accepted."
}
function Refresh-Sums([string]$Root) {
    @('workbench-testing-source-acceptance-0.2.0.zip','provenance.json','README.md' | ForEach-Object { (Get-FileHash (Join-Path $Root $_) -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $_ }) | Set-Content (Join-Path $Root 'SHA256SUMS') -Encoding utf8
}
Test-TestingSourceFixtureDelivery -Directory $Directory -ProductionDirectory $ProductionDirectory -RequireCurrentSource
$temp = Join-Path ([IO.Path]::GetTempPath()) ('testing-source-delivery-regression-' + [Guid]::NewGuid().ToString('N'))
try {
    $mutations = @('wrong-production-hash','changed-common-payload','wrong-manifest','changed-managed-fact')
    if ($env:WORKBENCH_HOST_PROFILE -ceq 'source-acceptance') { $mutations += @('wrong-sdk-commit','wrong-sdk-package','wrong-sdk-manifest','wrong-host-binary','wrong-source-repository','dirty-source') }
    foreach ($mutation in $mutations) {
        if (Test-Path $temp) { Remove-Item $temp -Recurse -Force }
        New-Item $temp -ItemType Directory | Out-Null
        Copy-Item (Join-Path $Directory '*') $temp -Recurse
        $metadata = Get-Content (Join-Path $temp 'provenance.json') -Raw | ConvertFrom-Json -AsHashtable
        if ($mutation -ceq 'wrong-production-hash') { $metadata.pairedProductionSha256 = '0' * 64 }
        elseif ($mutation -ceq 'changed-managed-fact') { $metadata.managed.assemblies[0].sha256 = '0' * 64 }
        elseif ($mutation -ceq 'wrong-sdk-commit') { $metadata.sdkSourceCommit = '0' * 40 }
        elseif ($mutation -ceq 'wrong-sdk-package') { $metadata.sdk.packages[0].sha256 = '0' * 64 }
        elseif ($mutation -ceq 'wrong-sdk-manifest') { $metadata.sdkManifestSha256 = '0' * 64 }
        elseif ($mutation -ceq 'wrong-host-binary') { $metadata.host.dllSha256 = '0' * 64 }
        elseif ($mutation -ceq 'wrong-source-repository') { $metadata.sourceRepository = 'https://example.invalid/foreign' }
        elseif ($mutation -ceq 'dirty-source') { $metadata.sourceTreeDirty = $true }
        else {
            $zipPath = Join-Path $temp 'workbench-testing-source-acceptance-0.2.0.zip'
            $archive = [IO.Compression.ZipFile]::Open($zipPath,[IO.Compression.ZipArchiveMode]::Update)
            try {
                $name = if ($mutation -ceq 'wrong-manifest') { 'plugin.json' } else { 'Workbench.Testing.dll' }
                $entry = $archive.GetEntry($name); $input = $entry.Open()
                try { $buffer=[IO.MemoryStream]::new(); $input.CopyTo($buffer); $bytes=$buffer.ToArray(); $buffer.Dispose() } finally { $input.Dispose() }
                if ($mutation -ceq 'wrong-manifest') {
                    $manifest=[Text.Encoding]::UTF8.GetString($bytes) | ConvertFrom-Json -AsHashtable; $manifest.id='com.example.invalid'
                    $bytes=[Text.UTF8Encoding]::new($false).GetBytes(($manifest | ConvertTo-Json -Depth 10))
                } else { $bytes[0] = $bytes[0] -bxor 1 }
                $entry.Delete(); $entry=$archive.CreateEntry($name); $output=$entry.Open(); try { $output.Write($bytes) } finally { $output.Dispose() }
            } finally { $archive.Dispose() }
            $record=@($metadata.payload | Where-Object path -CEQ $name)[0]
            $record.length=$bytes.Length; $record.sha256=[Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData($bytes))
            $metadata.zip.length=(Get-Item $zipPath).Length; $metadata.zip.sha256=(Get-FileHash $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
        }
        $metadata | ConvertTo-Json -Depth 40 | Set-Content (Join-Path $temp 'provenance.json') -Encoding utf8
        Refresh-Sums $temp
        $expected = switch ($mutation) { 'wrong-manifest' { 'TestingManifestInvalid' }; 'changed-managed-fact' { 'TestingManagedAuditMismatch' }; default { 'TestingProductionPairMismatch' } }
        Refuses { Test-TestingSourceFixtureDelivery -Directory $temp -ProductionDirectory $ProductionDirectory } $expected
    }
    Write-Host "Source fixture delivery rejected $($mutations.Count) altered pair, source, SDK and host cases for the selected profile."
} finally { if (Test-Path $temp) { Remove-Item $temp -Recurse -Force } }
