$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
Import-Module (Join-Path $repo 'scripts/TestingPluginDelivery.psm1') -Force
$module = Get-Module TestingPluginDelivery
$temp = Join-Path ([IO.Path]::GetTempPath()) ('testing-source-package-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($temp) | Out-Null
function Refuses([scriptblock]$Action, [string]$Expected) {
    try { & $Action | Out-Null } catch {
        if ($_.Exception.Message -cne $Expected) { throw "Expected $Expected; observed $($_.Exception.Message)" }; return
    }
    throw "Expected $Expected; invalid source fixture was accepted."
}
function New-Archive([string]$Label, [bool]$Source, [scriptblock]$Change) {
    $manifest = if ($Source) { 'tests/Workbench.Testing.SourceAcceptance/plugin.json' } else { 'plugins/Workbench.Testing/plugin.json' }
    $items = [ordered]@{
        'Fixtures/figma.synthetic.json' = [byte[]](1,2); 'Fixtures/requirements.synthetic.json' = [byte[]](3,4)
        'Workbench.Testing.deps.json' = [Text.Encoding]::UTF8.GetBytes('{}'); 'Workbench.Testing.dll' = [byte[]](5,6)
        'Workbench.Testing.Domain.dll' = [byte[]](7,8); 'Workbench.Testing.Windows.dll' = [byte[]](9,10)
        'Workbench.OwnedLab.exe' = [byte[]](11,12); 'plugin.json' = [IO.File]::ReadAllBytes((Join-Path $repo $manifest))
    }
    if ($Source) { $items['Workbench.Testing.SourceAcceptance.dll'] = [byte[]](13,14); $items['Workbench.Testing.SourceAcceptance.deps.json'] = [Text.Encoding]::UTF8.GetBytes('{}') }
    $context=@{items=$items; duplicate=$null; link=$null; reparse=$null}
    if ($Change) { & $Change $context | Out-Null }
    $path = Join-Path $temp ($Label + '.zip'); $archive = [IO.Compression.ZipFile]::Open($path,[IO.Compression.ZipArchiveMode]::Create); $records = @()
    try { foreach ($name in $items.Keys) {
        $entry = $archive.CreateEntry($name)
        if ($context.reparse -ceq $name) { $entry.ExternalAttributes=[int][IO.FileAttributes]::ReparsePoint }
        if ($context.link -ceq $name) { $entry.ExternalAttributes=-1577123840 }
        $stream = $entry.Open(); try { $stream.Write($items[$name]) } finally { $stream.Dispose() }
        $records += @{path=$name; length=$items[$name].Length; sha256=[Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData($items[$name]))}
    }; if ($context.duplicate) { $archive.CreateEntry($context.duplicate) | Out-Null } } finally { $archive.Dispose() }
    return @{ zip=$path; records=$records }
}
function Read-Archive($fixture,[string]$profile) { & $module { param($f,$p) Read-TestingPluginArchive -ZipPath $f.zip -PayloadRecords $f.records -Profile $p } $fixture $profile }
try {
    $production=New-Archive 'production' $false; $source=New-Archive 'source' $true
    $p=Read-Archive $production 'Production'; $s=Read-Archive $source 'SourceAcceptance'
    if ($p.Count -ne 8 -or $s.Count -ne 10) { throw 'Profile inventories are incorrect.' }
    Refuses { Read-Archive $source 'Production' } 'TestingPayloadFileListInvalid'
    Refuses { Read-Archive $production 'SourceAcceptance' } 'TestingPayloadFileListInvalid'
    foreach ($mutation in @('missing-fixture','missing-deps','duplicate-fixture','extra-sdk','linked-fixture','reparse-fixture','wrong-id','wrong-entry','missing-capability','duplicate-capability','unknown-field','duplicate-json','invalid-utf8','bad-deps')) {
        $invalid = New-Archive $mutation $true {
            param($c)
            switch ($mutation) {
                'missing-fixture' { $c.items.Remove('Workbench.Testing.SourceAcceptance.dll') }
                'missing-deps' { $c.items.Remove('Workbench.Testing.SourceAcceptance.deps.json') }
                'duplicate-fixture' { $c.duplicate='Workbench.Testing.SourceAcceptance.dll' }
                'extra-sdk' { $c.items['Workbench.Sdk.dll']=[byte[]](1) }
                'linked-fixture' { $c.link='Workbench.Testing.SourceAcceptance.dll' }
                'reparse-fixture' { $c.reparse='Workbench.Testing.SourceAcceptance.dll' }
                'bad-deps' { $c.items['Workbench.Testing.SourceAcceptance.deps.json']=[Text.Encoding]::UTF8.GetBytes('{"targets":{},"TARGETS":{}}') }
                'invalid-utf8' { $c.items['plugin.json']=[byte[]](0xff) }
                default {
                    $m=[Text.Encoding]::UTF8.GetString($c.items['plugin.json']) | ConvertFrom-Json -AsHashtable
                    switch ($mutation) {
                        'wrong-id' { $m.id='com.example.invalid' }; 'wrong-entry' { $m.entryType='Other.Entry' }
                        'missing-capability' { $m.capabilities=@($m.capabilities | Where-Object { $_ -cne 'source-acceptance-fixture' }) }
                        'duplicate-capability' { $m.capabilities[0]=$m.capabilities[1] }; 'unknown-field' { $m.extra=1 }
                    }
                    $json=$m | ConvertTo-Json -Depth 10 -Compress
                    if ($mutation -ceq 'duplicate-json') { $json=$json.Insert(1,'"ID":"duplicate",') }
                    $c.items['plugin.json']=[Text.Encoding]::UTF8.GetBytes($json)
                }
            }
        }
        $expected = if ($mutation -cin @('missing-fixture','missing-deps','duplicate-fixture','extra-sdk')) { 'TestingPayloadFileListInvalid' }
            elseif ($mutation -cin @('linked-fixture','reparse-fixture')) { 'TestingPayloadInvalid' }
            elseif ($mutation -ceq 'bad-deps') { 'TestingDepsJsonInvalid' } else { 'TestingManifestInvalid' }
        Refuses { Read-Archive $invalid 'SourceAcceptance' } $expected
    }
    & $module { param($a,$b) Assert-TestingCommonPayload $a $b } $p $s
    foreach ($name in @('Workbench.Testing.dll','Workbench.Testing.deps.json','Fixtures/requirements.synthetic.json')) {
        $copy=@{}; foreach ($key in $s.Keys) { $copy[$key]=$s[$key].Clone() }; $copy[$name]=[byte[]](0)
        Refuses { & $module { param($a,$b) Assert-TestingCommonPayload $a $b } $p $copy } 'TestingProductionPairMismatch'
    }
    Refuses { & $module { param($a) Assert-TestingDistinctDeliveryDirectories $a $a } $temp } 'TestingProductionPairMismatch'
    Refuses { & $module { param($a) Assert-TestingDistinctDeliveryDirectories $a (Join-Path $a 'nested') } $temp } 'TestingProductionPairMismatch'
    Refuses { & $module { param($a) Assert-TestingDistinctDeliveryDirectories ([IO.Path]::GetPathRoot($a)) $a } $temp } 'TestingProductionPairMismatch'
    Write-Host 'Source fixture profile, 14 structural/manifest refusals, common bytes and pair-directory guards passed; synthetic fixtures only.'
} finally { Remove-Item $temp -Recurse -Force }
