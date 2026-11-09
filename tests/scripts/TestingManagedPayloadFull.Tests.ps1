[CmdletBinding()]
param([Parameter(Mandatory)][string]$BuildDirectory, [Parameter(Mandatory)][string]$HostDirectory, [Parameter(Mandatory)][string]$PluginCommit, [Parameter(Mandatory)][string]$HostCommit)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot '../../scripts/TestingPluginDelivery.psm1') -Force
if (-not ('WorkbenchTestingManagedAudit' -as [type])) { throw 'ManagedAuditInterfaceMissing' }
$script:Cases = 0
function Refuses([scriptblock]$Action, [string]$Expected) {
    try { & $Action | Out-Null } catch {
        $message = $_.Exception.GetBaseException().Message
        if ($message -cne $Expected) { throw "Expected $Expected; observed $message" }
        $script:Cases++; Write-Host "Exact metadata refusal: $Expected"; return
    }
    throw "Expected $Expected; managed mutation accepted."
}
$payload = @{}
foreach ($name in @('Workbench.Testing.dll','Workbench.Testing.Domain.dll','Workbench.Testing.Windows.dll','Workbench.Testing.deps.json','Workbench.Testing.SourceAcceptance.dll','Workbench.Testing.SourceAcceptance.deps.json')) {
    $payload[$name] = [IO.File]::ReadAllBytes((Join-Path $BuildDirectory $name))
}
$entry = 'Workbench.Testing.SourceAcceptance.SourceAcceptancePlugin'
function Replace-PeUtf8([byte[]]$Image, [string]$Old, [string]$New) {
    if ([Text.Encoding]::UTF8.GetByteCount($Old) -ne [Text.Encoding]::UTF8.GetByteCount($New)) { throw 'Mutation must preserve PE layout.' }
    $hex = [Convert]::ToHexString($Image)
    $oldHex = [Convert]::ToHexString([Text.Encoding]::UTF8.GetBytes($Old))
    if (-not $hex.Contains($oldHex,[StringComparison]::Ordinal)) { throw 'Actual PE attribute to mutate was not found.' }
    return ,[Convert]::FromHexString($hex.Replace($oldHex,[Convert]::ToHexString([Text.Encoding]::UTF8.GetBytes($New))))
}
function Set-PeMetadata([byte[]]$Image, [string]$Mutation) {
    $changed = $Image.Clone()
    $stream = [IO.MemoryStream]::new($Image,$false)
    $pe = [Reflection.PortableExecutable.PEReader]::new($stream)
    try {
        $md = [Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
        if ($Mutation -ceq 'assembly-version') {
            $offset = $pe.PEHeaders.MetadataStartOffset + [Reflection.Metadata.Ecma335.MetadataReaderExtensions]::GetTableMetadataOffset($md,[Reflection.Metadata.Ecma335.TableIndex]::Assembly) + 4
            $changed[$offset] = 2
        } else {
            foreach ($handle in $md.TypeDefinitions) {
                $type = $md.GetTypeDefinition($handle)
                if ($md.GetString($type.Name) -cne 'SourceAcceptancePlugin') { continue }
                if ($Mutation -ceq 'abstract-entry') {
                    $row = [Reflection.Metadata.Ecma335.MetadataTokens]::GetRowNumber([Reflection.Metadata.EntityHandle]$handle)
                    $offset = $pe.PEHeaders.MetadataStartOffset + [Reflection.Metadata.Ecma335.MetadataReaderExtensions]::GetTableMetadataOffset($md,[Reflection.Metadata.Ecma335.TableIndex]::TypeDef) + ($row-1) * [Reflection.Metadata.Ecma335.MetadataReaderExtensions]::GetTableRowSize($md,[Reflection.Metadata.Ecma335.TableIndex]::TypeDef)
                    $changed[$offset] = $changed[$offset] -bor 0x80
                } else {
                    foreach ($methodHandle in $type.GetMethods()) {
                        $method = $md.GetMethodDefinition($methodHandle)
                        if ($md.GetString($method.Name) -cne '.ctor') { continue }
                        $row = [Reflection.Metadata.Ecma335.MetadataTokens]::GetRowNumber([Reflection.Metadata.EntityHandle]$methodHandle)
                        $offset = $pe.PEHeaders.MetadataStartOffset + [Reflection.Metadata.Ecma335.MetadataReaderExtensions]::GetTableMetadataOffset($md,[Reflection.Metadata.Ecma335.TableIndex]::MethodDef) + ($row-1) * [Reflection.Metadata.Ecma335.MetadataReaderExtensions]::GetTableRowSize($md,[Reflection.Metadata.Ecma335.TableIndex]::MethodDef) + 6
                        $changed[$offset] = ($changed[$offset] -band 0xf8) -bor 1
                    }
                }
            }
        }
    } finally { $pe.Dispose(); $stream.Dispose() }
    return ,$changed
}

$identity = [WorkbenchTestingManagedAudit]::Inspect($payload['Workbench.Testing.SourceAcceptance.dll'], 'Workbench.Testing.SourceAcceptance', $PluginCommit, $false, $entry) | ConvertFrom-Json
if ($identity.name -cne 'Workbench.Testing.SourceAcceptance' -or $identity.assemblyVersion -cne '1.0.0.0' -or $identity.fileVersion -cne '0.2.0.0' -or $identity.informationalVersion -cne "0.2.0+$PluginCommit" -or $identity.machine -cne 'Amd64') { throw 'Actual fixture PE identity mismatch.' }; $script:Cases++
[WorkbenchTestingManagedAudit]::Inspect($payload['Workbench.Testing.Domain.dll'], 'Workbench.Testing.Domain', $PluginCommit, $true, '') | Out-Null; $script:Cases++
foreach ($name in @('Workbench.Testing','Workbench.Testing.Windows')) {
    $entryType = if ($name -ceq 'Workbench.Testing') { 'Workbench.Testing.TestingPlugin' } else { '' }
    $facts = [WorkbenchTestingManagedAudit]::Inspect($payload["$name.dll"],$name,$PluginCommit,$false,$entryType)
    Write-Host $facts; $script:Cases++
}
Write-Host ($identity | ConvertTo-Json -Depth 20 -Compress)
foreach ($mutation in @('file-version','informational-version','assembly-version','abstract-entry','private-constructor','wrong-contract')) {
    $bytes = switch ($mutation) {
        'file-version' { Replace-PeUtf8 $payload['Workbench.Testing.SourceAcceptance.dll'] '0.2.0.0' '0.3.0.0' }
        'informational-version' { Replace-PeUtf8 $payload['Workbench.Testing.SourceAcceptance.dll'] "0.2.0+$PluginCommit" "0.3.0+$PluginCommit" }
        'wrong-contract' { Replace-PeUtf8 $payload['Workbench.Testing.SourceAcceptance.dll'] 'Workbench.Sdk' 'Workbench.Xdk' }
        default { Set-PeMetadata $payload['Workbench.Testing.SourceAcceptance.dll'] $mutation }
    }
    $expected = if ($mutation -cin @('file-version','informational-version','assembly-version')) { 'TestingManagedIdentityInvalid' } else { 'TestingManagedEntryInvalid' }
    Refuses { [WorkbenchTestingManagedAudit]::Inspect($bytes,'Workbench.Testing.SourceAcceptance',$PluginCommit,$false,$entry) } $expected
}

foreach ($case in @(
    @{ image = [byte[]](0,1,2); name = 'Workbench.Testing.SourceAcceptance'; commit = $PluginCommit; portable = $false; entry = $entry; expected = 'TestingManagedPeInvalid' },
    @{ image = $payload['Workbench.Testing.Domain.dll']; name = 'Workbench.Testing.SourceAcceptance'; commit = $PluginCommit; portable = $false; entry = $entry; expected = 'TestingManagedIdentityInvalid' },
    @{ image = $payload['Workbench.Testing.SourceAcceptance.dll']; name = 'Workbench.Testing.SourceAcceptance'; commit = ('0' * 40); portable = $false; entry = $entry; expected = 'TestingManagedIdentityInvalid' },
    @{ image = $payload['Workbench.Testing.Domain.dll']; name = 'Workbench.Testing.Domain'; commit = $PluginCommit; portable = $false; entry = ''; expected = 'TestingManagedArchitectureInvalid' },
    @{ image = $payload['Workbench.Testing.SourceAcceptance.dll']; name = 'Workbench.Testing.SourceAcceptance'; commit = $PluginCommit; portable = $true; entry = ''; expected = 'TestingManagedArchitectureInvalid' },
    @{ image = $payload['Workbench.Testing.SourceAcceptance.dll']; name = 'Workbench.Testing.SourceAcceptance'; commit = $PluginCommit; portable = $false; entry = 'Missing.Plugin'; expected = 'TestingManagedEntryInvalid' }
)) {
    Refuses { [WorkbenchTestingManagedAudit]::Inspect($case.image,$case.name,$case.commit,$case.portable,$case.entry) } $case.expected
}
# Genuine SDK-produced reference assembly, never a counterfeit native helper.
$reference = Join-Path $BuildDirectory 'reference/Workbench.Testing.SourceAcceptance.dll'
if (-not (Test-Path -LiteralPath $reference)) { throw 'Pass the preserved genuine compiler reference assembly at reference/Workbench.Testing.SourceAcceptance.dll.' }
Refuses { [WorkbenchTestingManagedAudit]::Inspect([IO.File]::ReadAllBytes($reference),'Workbench.Testing.SourceAcceptance',$PluginCommit,$false,$entry) } 'TestingManagedReferenceAssembly'
$audit = [WorkbenchTestingManagedAudit]::Audit($payload,$HostDirectory,$PluginCommit,$HostCommit,'SourceAcceptance') | ConvertFrom-Json
if ($audit.schemaVersion -ne 1 -or $audit.assemblies.Count -ne 4 -or $audit.deps.Count -ne 2 -or $audit.hostBindings.Count -lt 1) { throw 'Actual managed closure facts missing.' }; $script:Cases++
$production = @{}; foreach ($key in $payload.Keys) { if ($key -cnotlike '*SourceAcceptance*') { $production[$key] = $payload[$key] } }
[WorkbenchTestingManagedAudit]::Audit($production,$HostDirectory,$PluginCommit,$HostCommit,'Production') | Out-Null; $script:Cases++
foreach ($mutation in @('foreign-managed-runtimeTarget','missing-private','renamed-private','foreign-private','wrong-target','wrong-rid','wrong-root','missing-library','renamed-runtime','missing-edge','missing-reachable-edge','wrong-edge','foreign-library','foreign-runtime','private-runtime-version','duplicate-deps','missing-host','conflicting-host','linked-host','dangling-linked-host')) {
    $changed = @{}; foreach ($key in $payload.Keys) { $changed[$key] = $payload[$key].Clone() }
    $pairedHostDirectory = $HostDirectory
    switch ($mutation) {
        'missing-private' { $changed.Remove('Workbench.Testing.Domain.dll') }
        'renamed-private' { $changed['Renamed.dll'] = $changed['Workbench.Testing.Domain.dll']; $changed.Remove('Workbench.Testing.Domain.dll') }
        'foreign-private' { $changed['Workbench.Testing.Domain.dll'] = $changed['Workbench.Testing.SourceAcceptance.dll'] }
        'linked-host' { $pairedHostDirectory = Join-Path $BuildDirectory ('linked-host-' + [Guid]::NewGuid().ToString('N')); [IO.Directory]::CreateSymbolicLink($pairedHostDirectory,[IO.Path]::GetFullPath($HostDirectory)) | Out-Null }
        'dangling-linked-host' { $pairedHostDirectory = Join-Path $BuildDirectory ('dangling-host-' + [Guid]::NewGuid().ToString('N')); [IO.Directory]::CreateSymbolicLink($pairedHostDirectory,(Join-Path $BuildDirectory 'missing-host')) | Out-Null }
        'missing-host' { $pairedHostDirectory = Join-Path $BuildDirectory 'missing-host' }
        'conflicting-host' {
            $pairedHostDirectory = Join-Path $BuildDirectory ('conflicting-host-' + [Guid]::NewGuid().ToString('N'))
            New-Item $pairedHostDirectory -ItemType Directory | Out-Null
            foreach ($name in @('Workbench.Core') + @($audit.hostBindings | ForEach-Object name)) { Copy-Item -LiteralPath (Join-Path $HostDirectory "$name.dll") -Destination (Join-Path $pairedHostDirectory "$name.dll") }
            Copy-Item -LiteralPath (Join-Path $HostDirectory 'Workbench.App.deps.json') -Destination (Join-Path $pairedHostDirectory 'Workbench.App.deps.json')
            [IO.File]::WriteAllBytes((Join-Path $pairedHostDirectory 'Workbench.Sdk.dll'),$payload['Workbench.Testing.Domain.dll'])
        }
        default {
            $deps = [Text.Encoding]::UTF8.GetString($changed['Workbench.Testing.SourceAcceptance.deps.json']) | ConvertFrom-Json -AsHashtable
            $target = '.NETCoreApp,Version=v10.0'
            $root = 'Workbench.Testing.SourceAcceptance/0.2.0'
            switch ($mutation) {
                'foreign-managed-runtimeTarget' {
                    $deps.targets[$target]['Workbench.Sdk/0.4.0'].runtimeTargets = @{
                        'runtimes/win-x64/lib/net10.0/Workbench.Core.dll' = @{ rid = 'win-x64'; assetType = 'runtime'; assemblyVersion = '1.0.0.0'; fileVersion = '0.4.0.0' }
                    }
                    Write-Host 'I1 exact mutation: Workbench.Core managed runtimeTarget; expected TestingManagedDepsInvalid.'
                }
                'wrong-target' { $deps.runtimeTarget.name = '.NETCoreApp,Version=v9.0' }
                'wrong-rid' { $deps.runtimeTarget.name += '/win-x64' }
                'wrong-root' { $deps.targets[$target]['Other/0.2.0'] = $deps.targets[$target][$root]; $deps.targets[$target].Remove($root) }
                'missing-library' { $deps.libraries.Remove('Workbench.Testing.Domain/0.2.0') }
                'renamed-runtime' { $deps.targets[$target][$root].runtime = @{ 'Renamed.dll' = @{} } }
                'missing-reachable-edge' { $deps.targets[$target]['Workbench.Testing/0.2.0'].dependencies.Remove('Workbench.Testing.Domain') }
                'foreign-runtime' { $deps.targets[$target]['Workbench.Sdk/0.4.0'].runtime['Foreign.Private.dll'] = @{} }
                'private-runtime-version' { $deps.targets[$target][$root].runtime['Workbench.Testing.SourceAcceptance.dll']['assemblyVersion'] = '2.0.0.0' }
                'missing-edge' { $deps.targets[$target][$root].dependencies.Remove('Workbench.Testing') }
                'wrong-edge' { $deps.targets[$target][$root].dependencies['Workbench.Testing'] = '0.3.0' }
                'foreign-library' { $deps.libraries['Foreign.Private/0.2.0'] = @{ type='project'; serviceable=$false; sha512='' }; $deps.targets[$target]['Foreign.Private/0.2.0'] = @{ runtime=@{ 'Foreign.Private.dll'=@{} } } }
            }
            $json = $deps | ConvertTo-Json -Depth 32 -Compress
            if ($mutation -ceq 'duplicate-deps') { $json = $json.Insert(1,'"TARGETS":{},') }
            $changed['Workbench.Testing.SourceAcceptance.deps.json'] = [Text.Encoding]::UTF8.GetBytes($json)
        }
    }
    $expected = switch ($mutation) {
        { $_ -cin @('missing-private','renamed-private') } { 'TestingManagedPayloadInvalid' }
        'foreign-private' { 'TestingManagedIdentityInvalid' }
        { $_ -cin @('missing-host','conflicting-host') } { 'TestingManagedHostBindingInvalid' }
        'duplicate-deps' { 'TestingDepsJsonInvalid' }
        { $_ -cin @('linked-host','dangling-linked-host') } { 'TestingDeliveryLinkRefused' }
        default { 'TestingManagedDepsInvalid' }
    }
    Refuses { [WorkbenchTestingManagedAudit]::Audit($changed,$pairedHostDirectory,$PluginCommit,$HostCommit,'SourceAcceptance') } $expected
}
# I1 coverage uses the genuine payload PEs. Host declarations added for these cases live
# only in fresh owned peer copies; the preserved graph and metadata host are never edited.
$runtimeTargetCases = @(
    @{ name = 'approved-shared-managed'; mirror = $true; accepted = $true },
    @{ name = 'shared-managed-undeclared'; mirror = $false },
    @{ name = 'foreign-managed-undeclared'; mirror = $false },
    @{ name = 'rid-path-mismatch'; mirror = $true },
    @{ name = 'rid-wrong-type'; mirror = $true },
    @{ name = 'missing-rid'; mirror = $true },
    @{ name = 'unknown-asset-type'; mirror = $true },
    @{ name = 'missing-asset-type'; mirror = $true },
    @{ name = 'native-type-managed-path'; mirror = $true },
    @{ name = 'unknown-metadata'; mirror = $true },
    @{ name = 'metadata-array'; mirror = $true },
    @{ name = 'targets-array'; mirror = $false },
    @{ name = 'path-traversal'; mirror = $true },
    @{ name = 'assembly-version-conflicts-with-pe'; mirror = $true },
    @{ name = 'file-version-wrong-type'; mirror = $true }
)
$ownedRuntimeTargets = Join-Path ([IO.Path]::GetTempPath()) ('testing-managed-runtime-targets-' + [Guid]::NewGuid().ToString('N'))
Write-Host "I1 owned host-peer evidence: $ownedRuntimeTargets"
# A published RID host may omit the unselected native RID declarations. Those unused
# native assets remain valid dependency metadata and are never copied into the payload.
$nativePeerDirectory = Join-Path $ownedRuntimeTargets 'genuine-native-rid-subset-host'
New-Item $nativePeerDirectory -ItemType Directory -Force | Out-Null
foreach ($name in @('Workbench.Core') + @($audit.hostBindings | ForEach-Object name)) {
    Copy-Item -LiteralPath (Join-Path $HostDirectory "$name.dll") -Destination (Join-Path $nativePeerDirectory "$name.dll")
}
$nativeHostDeps = Get-Content -LiteralPath (Join-Path $HostDirectory 'Workbench.App.deps.json') -Raw | ConvertFrom-Json -AsHashtable
foreach ($hostTarget in $nativeHostDeps.targets.Values) {
    foreach ($hostNode in $hostTarget.Values) { $hostNode.Remove('runtimeTargets') }
}
$nativeHostDeps | ConvertTo-Json -Depth 32 | Set-Content -LiteralPath (Join-Path $nativePeerDirectory 'Workbench.App.deps.json') -Encoding utf8
[WorkbenchTestingManagedAudit]::Audit($payload,$nativePeerDirectory,$PluginCommit,$HostCommit,'SourceAcceptance') | Out-Null
$script:Cases++; Write-Host 'I1 genuine native RID graph accepted without unselected host runtimeTargets declarations.'
foreach ($case in $runtimeTargetCases) {
    $assetPath = 'runtimes/win-x64/lib/net10.0/Microsoft.WinUI.dll'
    $peerIdentity = @($audit.hostBindings | Where-Object name -CEQ 'Microsoft.WinUI')[0]
    $assetMetadata = @{ rid = 'win-x64'; assetType = 'runtime'; assemblyVersion = $peerIdentity.assemblyVersion; fileVersion = $peerIdentity.fileVersion }
    switch ($case.name) {
        'foreign-managed-undeclared' { $assetPath = 'runtimes/win-x64/lib/net10.0/Foreign.Private.dll' }
        'rid-path-mismatch' { $assetMetadata.rid = 'win-arm64' }
        'rid-wrong-type' { $assetMetadata.rid = 7 }
        'missing-rid' { $assetMetadata.Remove('rid') }
        'unknown-asset-type' { $assetMetadata.assetType = 'managed' }
        'missing-asset-type' { $assetMetadata.Remove('assetType') }
        'native-type-managed-path' { $assetMetadata.assetType = 'native'; $assetMetadata.Remove('assemblyVersion') }
        'unknown-metadata' { $assetMetadata.extra = 'unexpected' }
        'metadata-array' { $assetMetadata = @('unexpected') }
        'path-traversal' { $assetPath = 'runtimes/win-x64/lib/../Microsoft.WinUI.dll' }
        'assembly-version-conflicts-with-pe' { $assetMetadata.assemblyVersion = '2.0.0.0' }
        'file-version-wrong-type' { $assetMetadata.fileVersion = 7 }
    }
    $runtimeTargets = @{ $assetPath = $assetMetadata }
    if ($case.name -ceq 'targets-array') { $runtimeTargets = @('unexpected') }
    $changed = @{}; foreach ($key in $payload.Keys) { $changed[$key] = $payload[$key] }
    $deps = [Text.Encoding]::UTF8.GetString($payload['Workbench.Testing.SourceAcceptance.deps.json']) | ConvertFrom-Json -AsHashtable
    $target = $deps.targets['.NETCoreApp,Version=v10.0']
    $libraries = @($target.GetEnumerator() | Where-Object {
        $node = $_.Value
        $node -is [Collections.IDictionary] -and $node.Contains('runtime') -and $node['runtime'] -is [Collections.IDictionary] -and
            @($node['runtime'].Keys | Where-Object { $_ -ceq 'Microsoft.WinUI.dll' -or $_.EndsWith('/Microsoft.WinUI.dll',[StringComparison]::Ordinal) }).Count -gt 0
    })
    if ($libraries.Count -ne 1) { throw 'TestingManagedWinUiLibraryNotUnique' }
    $libraryName = $libraries[0].Key
    $target[$libraryName]['runtimeTargets'] = $runtimeTargets
    $changed['Workbench.Testing.SourceAcceptance.deps.json'] = [Text.Encoding]::UTF8.GetBytes(($deps | ConvertTo-Json -Depth 32 -Compress))
    $peerDirectory = $HostDirectory
    if ($case.mirror) {
        $peerDirectory = Join-Path $ownedRuntimeTargets $case.name
        New-Item $peerDirectory -ItemType Directory -Force | Out-Null
        foreach ($name in @('Workbench.Core') + @($audit.hostBindings | ForEach-Object name)) {
            Copy-Item -LiteralPath (Join-Path $HostDirectory "$name.dll") -Destination (Join-Path $peerDirectory "$name.dll")
        }
        $hostDeps = Get-Content -LiteralPath (Join-Path $HostDirectory 'Workbench.App.deps.json') -Raw | ConvertFrom-Json -AsHashtable
        $hostNodes = @($hostDeps.targets.Values | Where-Object { $_ -is [Collections.IDictionary] -and $_.Contains($libraryName) })
        if ($hostNodes.Count -lt 1) { throw 'TestingManagedWinUiHostLibraryMissing' }
        $hostNodes[0][$libraryName]['runtimeTargets'] = $runtimeTargets
        $hostDeps | ConvertTo-Json -Depth 32 | Set-Content -LiteralPath (Join-Path $peerDirectory 'Workbench.App.deps.json') -Encoding utf8
    }
    if ($case.ContainsKey('accepted') -and $case.accepted) {
        [WorkbenchTestingManagedAudit]::Audit($changed,$peerDirectory,$PluginCommit,$HostCommit,'SourceAcceptance') | Out-Null
        $script:Cases++; Write-Host "I1 approved shared managed runtimeTarget accepted: $($case.name)"
    } else {
        Write-Host "I1 runtimeTarget guard: $($case.name)"
        Refuses { [WorkbenchTestingManagedAudit]::Audit($changed,$peerDirectory,$PluginCommit,$HostCommit,'SourceAcceptance') } 'TestingManagedDepsInvalid'
    }
}
Write-Host "Managed metadata/closure guards passed: $script:Cases cases on genuine compiled PEs; this is not native package/activation evidence."
