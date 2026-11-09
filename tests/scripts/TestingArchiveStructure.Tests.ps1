$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
Import-Module (Join-Path $repo 'scripts/TestingPluginDelivery.psm1') -Force
$module = Get-Module TestingPluginDelivery
$temp = Join-Path ([IO.Path]::GetTempPath()) ('testing-archive-regression-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($temp) | Out-Null
$script:cases = 0
function Refuses([scriptblock]$Action, [string]$Expected) {
    try { & $Action | Out-Null } catch {
        if ($_.Exception.Message -cne $Expected) { throw "Expected $Expected; observed $($_.Exception.Message)" }
        $script:cases++; return
    }
    throw "Expected $Expected; malformed archive accepted."
}
function Write-Archive([string]$Label, [scriptblock]$Change) {
    $items = [ordered]@{
        'Fixtures/figma.synthetic.json' = [byte[]](1,2); 'Fixtures/requirements.synthetic.json' = [byte[]](3,4)
        'Workbench.Testing.deps.json' = [Text.Encoding]::UTF8.GetBytes('{}')
        'Workbench.Testing.dll' = [byte[]](5,6); 'Workbench.Testing.Domain.dll' = [byte[]](7,8)
        'Workbench.Testing.Windows.dll' = [byte[]](9,10); 'Workbench.OwnedLab.exe' = [byte[]](11,12)
        'plugin.json' = [IO.File]::ReadAllBytes((Join-Path $repo 'plugins/Workbench.Testing/plugin.json'))
    }
    $context = @{ items = $items; duplicate = $null; link = $null; reparse = $null }
    if ($Change) { & $Change $context | Out-Null }
    $zipPath = Join-Path $temp ($Label + '.zip')
    $archive = [IO.Compression.ZipFile]::Open($zipPath, [IO.Compression.ZipArchiveMode]::Create)
    $records = @()
    try {
        foreach ($name in $items.Keys) {
            $entry = $archive.CreateEntry($name)
            if ($context.reparse -ceq $name) { $entry.ExternalAttributes = [int][IO.FileAttributes]::ReparsePoint }
            if ($context.link -ceq $name) { $entry.ExternalAttributes = -1577123840 }
            $stream = $entry.Open(); try { $stream.Write($items[$name]) } finally { $stream.Dispose() }
            $records += @{ path = $name; length = $items[$name].Length; sha256 = [Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData($items[$name])) }
        }
        if ($context.duplicate) { $archive.CreateEntry($context.duplicate) | Out-Null }
    } finally { $archive.Dispose() }
    return @{ zip = $zipPath; records = $records }
}
function Read-Archive($Fixture) {
    & $module { param($f) Read-TestingPluginArchive -ZipPath $f.zip -PayloadRecords $f.records } $Fixture
}
try {
    $valid = Write-Archive 'valid' $null
    if ((Read-Archive $valid).Count -ne 8) { throw 'Expected exactly eight payloads.' }; $script:cases++
    foreach ($mutation in @('missing-dll','renamed-dll','duplicate-dll','missing-deps','duplicate-manifest','extra-sdk','extra-runtimeconfig','linked-entry','windows-reparse-entry','empty-entry')) {
        $fixture = Write-Archive $mutation {
            param($c)
            switch ($mutation) {
                'missing-dll' { $c.items.Remove('Workbench.Testing.Domain.dll') }
                'renamed-dll' { $c.items['Renamed.dll'] = $c.items['Workbench.Testing.dll']; $c.items.Remove('Workbench.Testing.dll') }
                'duplicate-dll' { $c.duplicate = 'Workbench.Testing.dll' }
                'missing-deps' { $c.items.Remove('Workbench.Testing.deps.json') }
                'duplicate-manifest' { $c.duplicate = 'plugin.json' }
                'extra-sdk' { $c.items['Workbench.Sdk.dll'] = [byte[]](1) }
                'extra-runtimeconfig' { $c.items['Workbench.Testing.runtimeconfig.json'] = [byte[]](1) }
                'linked-entry' { $c.link = 'Workbench.Testing.dll' }
                'windows-reparse-entry' { $c.reparse = 'Workbench.Testing.dll' }
                'empty-entry' { $c.items['Workbench.Testing.dll'] = [byte[]]::new(0) }
            }
        }
        $expected = if ($mutation -cin @('linked-entry','windows-reparse-entry','empty-entry')) { 'TestingPayloadInvalid' } else { 'TestingPayloadFileListInvalid' }
        Refuses { Read-Archive $fixture } $expected
    }
    foreach ($mutation in @('id','entryAssembly','entryType','apiVersion','version','minimumHostVersion','capabilities','duplicate-capability','missing-capability','duplicate-json','unknown-field','missing-field','wrong-type','trailing-json','oversize-json','invalid-utf8','deps-duplicate-json','deps-depth','deps-oversize')) {
        $fixture = Write-Archive $mutation {
            param($c)
            $m = [Text.Encoding]::UTF8.GetString($c.items['plugin.json']) | ConvertFrom-Json -AsHashtable
            switch ($mutation) {
                'id' { $m.id = 'com.wrong.plugin' }; 'entryAssembly' { $m.entryAssembly = 'Wrong.dll' }; 'entryType' { $m.entryType = 'Wrong.Entry' }
                'apiVersion' { $m.apiVersion = 2 }; 'version' { $m.version = '0.3.0' }; 'minimumHostVersion' { $m.minimumHostVersion = '0.3.0' }
                'capabilities' { $m.capabilities += 'unexpected' }; 'duplicate-capability' { $m.capabilities[1] = $m.capabilities[0] }
                'missing-capability' { $m.capabilities = @($m.capabilities | Select-Object -Skip 1) }; 'unknown-field' { $m.extra = 1 }
                'missing-field' { $m.Remove('description') }; 'wrong-type' { $m.apiVersion = '1' }
            }
            $json = $m | ConvertTo-Json -Depth 10 -Compress
            if ($mutation -ceq 'duplicate-json') { $json = $json.Insert(1, '"ID":"duplicate",') }
            if ($mutation -ceq 'oversize-json') { $json = '{"description":"' + ('x' * 2097152) + '"}' }
            if ($mutation -ceq 'trailing-json') { $json += '{}' }
            $c.items['plugin.json'] = [Text.Encoding]::UTF8.GetBytes($json)
            if ($mutation -ceq 'invalid-utf8') { $c.items['plugin.json'] = [byte[]](0xff) }
            if ($mutation -ceq 'deps-oversize') { $c.items['Workbench.Testing.deps.json'] = [Text.Encoding]::UTF8.GetBytes('{"x":"' + ('x' * 2097152) + '"}') }
            if ($mutation -ceq 'deps-duplicate-json') { $c.items['Workbench.Testing.deps.json'] = [Text.Encoding]::UTF8.GetBytes('{"targets":{},"TARGETS":{}}') }
            if ($mutation -ceq 'deps-depth') { $c.items['Workbench.Testing.deps.json'] = [Text.Encoding]::UTF8.GetBytes(('[' * 33) + '0' + (']' * 33)) }
        }
        $expected = if ($mutation -clike 'deps-*') { 'TestingDepsJsonInvalid' } else { 'TestingManifestInvalid' }
        Refuses { Read-Archive $fixture } $expected
    }
    $tampered = Write-Archive 'hash-tamper' $null
    $tampered.records[0].sha256 = '0' * 64
    Refuses { Read-Archive $tampered } 'TestingPayloadHashMismatch'
    $provenance = Join-Path $temp 'provenance.json'
    [IO.File]::WriteAllText($provenance, '{"schemaVersion":1}', [Text.UTF8Encoding]::new($false))
    $parsed = & $module { param($p) Read-TestingProvenance $p } $provenance
    if ($parsed.schemaVersion -ne 1) { throw 'Valid provenance could not be read.' }; $script:cases++
    foreach ($mutation in @('duplicate','invalid-utf8','oversize','wrong-root')) {
        $bytes = switch ($mutation) {
            'duplicate' { [Text.Encoding]::UTF8.GetBytes('{"schemaVersion":1,"SCHEMAVERSION":1}') }
            'invalid-utf8' { [byte[]](0xff) }
            'oversize' { [Text.Encoding]::UTF8.GetBytes('{"x":"' + ('x' * 2097152) + '"}') }
            'wrong-root' { [Text.Encoding]::UTF8.GetBytes('[{"schemaVersion":1}]') }
        }
        [IO.File]::WriteAllBytes($provenance, $bytes)
        Refuses { & $module { param($p) Read-TestingProvenance $p } $provenance } 'TestingDeliveryProvenanceInvalid'
    }
    Write-Host "Production archive structural regressions passed: $script:cases cases; synthetic bytes, no native execution."
} finally { Remove-Item $temp -Recurse -Force }
