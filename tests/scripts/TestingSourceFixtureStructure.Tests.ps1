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
function New-Archive([string]$Label, [bool]$Source) {
    $manifest = if ($Source) { 'tests/Workbench.Testing.SourceAcceptance/plugin.json' } else { 'plugins/Workbench.Testing/plugin.json' }
    $items = [ordered]@{
        'Fixtures/figma.synthetic.json' = [byte[]](1,2); 'Fixtures/requirements.synthetic.json' = [byte[]](3,4)
        'Workbench.Testing.deps.json' = [Text.Encoding]::UTF8.GetBytes('{}'); 'Workbench.Testing.dll' = [byte[]](5,6)
        'Workbench.Testing.Domain.dll' = [byte[]](7,8); 'Workbench.Testing.Windows.dll' = [byte[]](9,10)
        'Workbench.OwnedLab.exe' = [byte[]](11,12); 'plugin.json' = [IO.File]::ReadAllBytes((Join-Path $repo $manifest))
    }
    if ($Source) { $items['Workbench.Testing.SourceAcceptance.dll'] = [byte[]](13,14); $items['Workbench.Testing.SourceAcceptance.deps.json'] = [Text.Encoding]::UTF8.GetBytes('{}') }
    $path = Join-Path $temp ($Label + '.zip'); $archive = [IO.Compression.ZipFile]::Open($path,[IO.Compression.ZipArchiveMode]::Create); $records = @()
    try { foreach ($name in $items.Keys) {
        $entry = $archive.CreateEntry($name); $stream = $entry.Open(); try { $stream.Write($items[$name]) } finally { $stream.Dispose() }
        $records += @{path=$name; length=$items[$name].Length; sha256=[Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData($items[$name]))}
    } } finally { $archive.Dispose() }
    return @{ zip=$path; records=$records }
}
function Read-Archive($fixture,[string]$profile) { & $module { param($f,$p) Read-TestingPluginArchive -ZipPath $f.zip -PayloadRecords $f.records -Profile $p } $fixture $profile }
try {
    $production=New-Archive 'production' $false; $source=New-Archive 'source' $true
    $p=Read-Archive $production 'Production'; $s=Read-Archive $source 'SourceAcceptance'
    if ($p.Count -ne 8 -or $s.Count -ne 10) { throw 'Profile inventories are incorrect.' }
    Refuses { Read-Archive $source 'Production' } 'TestingPayloadFileListInvalid'
    Refuses { Read-Archive $production 'SourceAcceptance' } 'TestingPayloadFileListInvalid'
    & $module { param($a,$b) Assert-TestingCommonPayload $a $b } $p $s
    foreach ($name in @('Workbench.Testing.dll','Workbench.Testing.deps.json','Fixtures/requirements.synthetic.json')) {
        $copy=@{}; foreach ($key in $s.Keys) { $copy[$key]=$s[$key].Clone() }; $copy[$name]=[byte[]](0)
        Refuses { & $module { param($a,$b) Assert-TestingCommonPayload $a $b } $p $copy } 'TestingProductionPairMismatch'
    }
    Refuses { & $module { param($a) Assert-TestingDistinctDeliveryDirectories $a $a } $temp } 'TestingProductionPairMismatch'
    Refuses { & $module { param($a) Assert-TestingDistinctDeliveryDirectories $a (Join-Path $a 'nested') } $temp } 'TestingProductionPairMismatch'
    Refuses { & $module { param($a) Assert-TestingDistinctDeliveryDirectories ([IO.Path]::GetPathRoot($a)) $a } $temp } 'TestingProductionPairMismatch'
    Write-Host 'Source fixture profile, inventories, seven common bytes and pair-directory guards passed; synthetic fixtures only.'
} finally { Remove-Item $temp -Recurse -Force }
