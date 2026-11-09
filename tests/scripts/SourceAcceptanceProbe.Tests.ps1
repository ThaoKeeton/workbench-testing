[CmdletBinding()]
param([switch]$UtcInteropOnly)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot '../../scripts/SourceAcceptanceProbe.psm1') -Force
# These are synthetic report/byte fixtures, never Windows runtime evidence.
$temp = Join-Path ([IO.Path]::GetTempPath()) ('source-report-regression-' + [Guid]::NewGuid().ToString('N'))
New-Item $temp -ItemType Directory | Out-Null
$script:cases = 0
$script:comparisonSeed = $null
function Assert-Refused([scriptblock]$Action) {
    $caught = $false
    try { & $Action | Out-Null } catch { $caught = $true }
    if (-not $caught) { throw 'A malformed source report was accepted.' }
    $script:cases++
}
function Hash([byte[]]$Bytes) { return [Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData($Bytes)) }
function Write-Json([string]$Path, $Value) {
    [IO.File]::WriteAllText($Path, (ConvertTo-Json -InputObject $Value -Depth 30 -Compress), [Text.UTF8Encoding]::new($false))
}
$hash = 'a' * 64; $commit = 'b' * 40; $birth = [long]134439123456789123
$ownedRoot = Join-Path (Join-Path ([IO.Path]::GetTempPath()) 'ProjectWorkbench-source-acceptance-probe') ('c' * 32)
function New-ActualUtcStamp {
    # Use the real producer serializer, then retain its exact string; never normalize/rewrite the offset.
    $wire = [System.Text.Json.JsonSerializer]::Serialize[DateTimeOffset]([DateTimeOffset]::UtcNow,[System.Text.Json.JsonSerializerOptions]::new())
    $document = [System.Text.Json.JsonDocument]::Parse($wire)
    try {
        $stamp = $document.RootElement.GetString()
        if (-not $stamp.EndsWith('+00:00',[StringComparison]::Ordinal)) { throw 'Actual UTC serializer no longer emits the expected explicit zero offset.' }
        return $stamp
    } finally { $document.Dispose() }
}
function New-Native([string]$Phase, [string]$Root = $ownedRoot, [switch]$UtcCompletion) {
    [string[]]$holdProviders = @(if ($Phase -ceq 'seed') { @('figma','figma','ai','figma','ai') } elseif ($Phase -ceq 'stop-ai') { @('ai') } elseif ($Phase -ceq 'stop-figma') { @('figma') } else { @() })
    $holds = @(for ($i=0; $i -lt $holdProviders.Count; $i++) {
        @{ root=$Root; runtimeGeneration=[long]1; descriptor=@{ protocolVersion=1; fixtureId='com.projectworkbench.testing.source-acceptance'; pluginId='com.projectworkbench.testing.source-acceptance';
            runId=[IO.Path]::GetFileName($Root); projectId='default'; instanceId='d'*32; generation=[long]($i+1); provider=$holdProviders[$i]; scenario='hold'; releaseId=($i+30).ToString('x32') } }
    })

    $counter = @{ entered = [long]0; accepted = [long]0; settled = [long]0; unexpected = [long]0; limitExceeded = $false; cancellationObserved = $false }
    $checks = @(Get-SourceAcceptanceRequiredChecks $Phase)
    return @{
        schemaVersion = 1; kind = 'SourceAcceptanceNative'; succeeded = $true; phase = $Phase; userRoot = $Root
        sourceCommit = $commit; packageSha256 = $hash; hostExeSha256 = $hash; hostDllSha256 = $hash
        processId = 501; processCreationFileTimeUtc = $(if ($Phase -ceq 'verify') { $birth + 1 } else { $birth }); actualWindowClosed = $true; rootsRetired = $true
        roots = @($Root); osDescription = 'Synthetic Windows Server report fixture'; processArchitecture = 'X64'
        checks = $checks; observations = @($checks | ForEach-Object { @{ check = $_; projectId = 'default'; instanceId = 'd' * 32; generation = [long]1;
            runtimeGeneration = [long]1; figma = $counter.Clone(); ai = $counter.Clone(); sourceIds = @(); caseSetIds = @(); runCount = 0 } })
        records = @{ figmaId = $null; happyReceiptId = $null; partialReceiptId = $null; caseSetId = $null; seedInstanceId = 'd' * 32; secondaryId = $null }
        holds = $holds; evidence = @(); completedUtc = $(if ($UtcCompletion) { New-ActualUtcStamp } else { '2026-11-08T12:00:00Z' })
    }
}
function Add-RecordFixture($r, [string]$directory, [switch]$UtcSources, [switch]$UtcCases) {
    $phase = $r.phase
    if ($phase -ceq 'stop-figma') { return }
    $figmaId = '1' * 32; $receiptId = '2' * 32; $partialId = '3' * 32; $setId = '4' * 32; $navigationId = '5' * 32; $decoyId = '6' * 32
    $r.records.figmaId = $figmaId
    $r.evidence = @()
    $base = Join-Path $directory ($phase + '-evidence')
    function Save-Evidence([string]$area, $data) {
        $relative = 'default/' + $area + '/' + $data.id + '.json'; $path = Join-Path $base $relative
        [IO.Directory]::CreateDirectory((Split-Path $path -Parent)) | Out-Null
        Write-Json $path $data
        $bytes = [IO.File]::ReadAllBytes($path)
        $r.evidence += @{ projectId = 'default'; area = $area; id = $data.id; path = $relative; length = $bytes.Length; sha256 = (Hash $bytes) }
        return Hash $bytes
    }
    function Envelope([string]$id, [string]$kind, [string]$provider, [string]$document, [string]$title, [byte[]]$raw, [object[]]$items) {
        return [ordered]@{ schemaVersion = 1; id = $id; projectId = 'default'; producerPluginId = 'com.projectworkbench.testing.source-acceptance'; createdUtc = $(if ($UtcSources) { New-ActualUtcStamp } else { '2026-11-08T12:00:00Z' });
            kind = $kind; providerId = $provider; providerVersion = '1'; documentId = $document; title = $title; originalBytes = [Convert]::ToBase64String($raw); originalSha256 = (Hash $raw); items = $items }
    }
    $figmaRaw = [Text.Encoding]::UTF8.GetBytes('{"name":"Synthetic source acceptance file","version":"source-v1","nodes":{"2:3":{"document":{"id":"2:3","name":"Synthetic heading","type":"TEXT","characters":"Synthetic heading text"}},"1:2":{"document":{"id":"1:2","name":"Synthetic confirmation","type":"TEXT","characters":"Synthetic confirmation text"}}}}')
    $figmaItems = @(@{ locator = '2:3'; title = 'Synthetic heading'; description = "Observed type: TEXT`nObserved characters: Synthetic heading text"; acceptanceCriteria = @('Synthetic heading is visible.','Synthetic navigation is reviewed manually.'); virtualScenario = $null },
        @{ locator = '1:2'; title = 'Synthetic confirmation'; description = "Observed type: TEXT`nObserved characters: Synthetic confirmation text"; acceptanceCriteria = @('Synthetic confirmation is reviewed manually.'); virtualScenario = $null })
    $figmaRevision = Save-Evidence 'sources' (Envelope $figmaId 'Figma' 'figma.rest.nodes.manual-criteria' 'SourceAcceptanceFile1@source-v1' 'Synthetic source acceptance file' $figmaRaw $figmaItems)
    if ($phase -ceq 'stop-ai') { return }
    $r.records.happyReceiptId = $receiptId; $r.records.partialReceiptId = $partialId; $r.records.caseSetId = $setId; $r.records.secondaryId = '7' * 32
    $r.roots += (Join-Path $r.userRoot ('.workspaces/projects/' + $r.records.secondaryId))
    if ($phase -ceq 'verify') { foreach ($row in $r.observations) { $row.instanceId = '8' * 32 } }
    $inputText = '{"sources":[{"snapshotId":"FIGMA_ID","snapshotRevision":"FIGMA_REVISION","kind":"Figma","providerId":"figma.rest.nodes.manual-criteria","providerVersion":"1","documentId":"SourceAcceptanceFile1@source-v1","title":"Synthetic source acceptance file","items":[{"locator":"2:3","title":"Synthetic heading","description":"Observed type: TEXT\nObserved characters: Synthetic heading text","criteria":[{"criterionIndex":0,"text":"Synthetic heading is visible."},{"criterionIndex":1,"text":"Synthetic navigation is reviewed manually."}]},{"locator":"1:2","title":"Synthetic confirmation","description":"Observed type: TEXT\nObserved characters: Synthetic confirmation text","criteria":[{"criterionIndex":2,"text":"Synthetic confirmation is reviewed manually."}]}]}]}'
    $inputBytes = [Text.Encoding]::UTF8.GetBytes($inputText.Replace('FIGMA_ID',$figmaId).Replace('FIGMA_REVISION',$figmaRevision))
    $originalTraces = @(@{ snapshotId = $figmaId; snapshotRevision = $figmaRevision; locator = '2:3'; criterionIndex = 0 },
        @{ snapshotId = $figmaId; snapshotRevision = $figmaRevision; locator = '2:3'; criterionIndex = 1 },
        @{ snapshotId = $figmaId; snapshotRevision = $figmaRevision; locator = '1:2'; criterionIndex = 0 })
    $receiptRevision = $null
    foreach ($partial in @($false,$true)) {
        $pre = if ($partial) { 'p' * 4096 } else { 'Synthetic manual review only.' }
        $generatedCases = foreach ($index in @(2,0,1)) { '{"criterionIndex":' + $index + ',"title":"Synthetic manual case ' + $index + '","prerequisites":"' + $pre + '","steps":[{"instruction":"Review synthetic criterion ' + $index + ' manually.","expected":"Synthetic criterion ' + $index + ' is satisfied."}]}' }
        $generated = '{"cases":[' + ($generatedCases -join ',') + ']}'
        $quoted = [System.Text.Json.JsonSerializer]::Serialize[string]($generated,[System.Text.Json.JsonSerializerOptions]::new())
        $response = [Text.Encoding]::UTF8.GetBytes(('{"id":"resp_source_acceptance_1","model":"gpt-5.4-mini-2026-03-17","status":"completed","output":[{"type":"message","role":"assistant","status":"completed","content":[{"type":"output_text","text":' + $quoted + '}]}]}'))
        $receipt = @{ schemaVersion = 1; requestedModel = 'gpt-5.4-mini-2026-03-17'; observedModel = 'gpt-5.4-mini-2026-03-17'; responseId = 'resp_source_acceptance_1';
            inputUtf8Base64 = [Convert]::ToBase64String($inputBytes); inputSha256 = (Hash $inputBytes); responseUtf8Base64 = [Convert]::ToBase64String($response); responseSha256 = (Hash $response); originalTraces = $originalTraces }
        $items = @(for ($i = 0; $i -lt 3; $i++) { $index = @(2,0,1)[$i]; @{ locator = 'case-' + ($i+1); title = "Synthetic manual case $index"; description = 'AI generated; manual review required.'; acceptanceCriteria = @("Synthetic criterion $index is satisfied."); virtualScenario = $null } })
        $id = if ($partial) { $partialId } else { $receiptId }
        $revision = Save-Evidence 'sources' (Envelope $id 'Requirements' 'openai.responses.test-cases' 'resp_source_acceptance_1' 'AI generation receipt (manual review required)' ([Text.Encoding]::UTF8.GetBytes((ConvertTo-Json -InputObject $receipt -Depth 20 -Compress))) $items)
        if (-not $partial) { $receiptRevision = $revision }
    }
    $caseRows = @(for ($i = 0; $i -lt 3; $i++) {
        $index = @(2,0,1)[$i]; $traces = @($originalTraces[$index], @{ snapshotId = $receiptId; snapshotRevision = $receiptRevision; locator = 'case-' + ($i+1); criterionIndex = 0 })
        @{ id = (($i+10).ToString('x32')); title = "Synthetic manual case $index"; prerequisites = "AI generated; manual review required. Model: gpt-5.4-mini-2026-03-17.`nSynthetic manual review only."; traces = $traces;
            steps = @(@{ id = (($i+20).ToString('x32')); instruction = "Review synthetic criterion $index manually."; expected = "Synthetic criterion $index is satisfied."; traces = $traces; action = $null }) }
    })
    Save-Evidence 'cases' ([ordered]@{ schemaVersion = 1; id = $setId; projectId = 'default'; producerPluginId = 'com.projectworkbench.testing.source-acceptance'; updatedUtc = $(if ($UtcCases) { New-ActualUtcStamp } else { '2026-11-08T12:00:00Z' }); name = 'AI 测试用例（待人工审核）'; cases = $caseRows }) | Out-Null
    Save-Evidence 'sources' (Envelope $navigationId 'Figma' 'figma.rest.nodes.manual-criteria' 'SourceAcceptanceFile1@source-v1' 'Synthetic source acceptance file' $figmaRaw $figmaItems) | Out-Null
    Save-Evidence 'sources' (Envelope $decoyId 'Requirements' 'synthetic.requirements' 'synthetic-decoy' 'Synthetic decoy' ([Text.Encoding]::UTF8.GetBytes('{}')) @(@{ locator = 'decoy'; title = 'Synthetic decoy'; description = 'Never selected for AI'; acceptanceCriteria = @('Synthetic decoy only'); virtualScenario = 'TaskFormPositive' })) | Out-Null
}
function Rehash-Evidence($r, [string]$directory) {
    $base = Join-Path $directory ($r.phase + '-evidence')
    foreach ($file in $r.evidence) { $bytes = [IO.File]::ReadAllBytes((Join-Path $base $file.path)); $file.length = $bytes.Length; $file.sha256 = Hash $bytes }
}
function Change-Evidence($r, [string]$directory, [string]$Id, [scriptblock]$Change) {
    $file = @($r.evidence | Where-Object id -CEQ $Id)[0]
    $path = Join-Path (Join-Path $directory ($r.phase + '-evidence')) $file.path
    $data = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -AsHashtable
    & $Change $data
    Write-Json $path $data; Rehash-Evidence $r $directory
}

function Complete([string]$Path, [string]$Phase = 'stop-figma', [long]$ObservedBirth = $birth, [string[]]$Leases = @($ownedRoot), [bool]$Exited = $true, $ExitCode = 0, [bool]$Terminated = $false) {
    if ($Phase -ceq 'verify' -and -not $PSBoundParameters.ContainsKey('ObservedBirth')) { $ObservedBirth = $birth + 1 }
    $seedArgs = @{}
    if ($Phase -ceq 'verify') { $seedArgs.ExpectedSeed = $script:comparisonSeed }
    $result = Complete-SourceAcceptanceProbeReport @seedArgs -ReportPath $Path -ProcessId 501 -ProcessCreationFileTimeUtc $ObservedBirth -ProcessExitFileTimeUtc ($ObservedBirth + 10000) `
        -HasExited $Exited -ExitCode $ExitCode -ExpectedPhase $Phase -ExpectedRoot $ownedRoot -LeaseRoots $Leases `
        -ExpectedPackageHash $hash -ExpectedSourceCommit $commit -ExpectedHostExeHash $hash -ExpectedHostDllHash $hash -Terminated $Terminated
    if ($Phase -ceq 'seed') { $script:comparisonSeed = Get-Content $Path -Raw | ConvertFrom-Json -AsHashtable }
    return $result
}
function Exercise([string]$Name, [scriptblock]$Mutation, [string]$Phase = 'stop-figma', [scriptblock]$Invoke = { param($p, $phase) Complete $p $phase }) {
    $directory = Join-Path $temp $Name; New-Item $directory -ItemType Directory | Out-Null
    $path = Join-Path $directory "$Phase.json"; $report = New-Native $Phase
    & $Mutation $report $directory
    Write-Json $path $report
    Assert-Refused { & $Invoke $path $Phase }
    $runner = [IO.Path]::ChangeExtension($path, 'runner.json')
    if (-not (Test-Path -LiteralPath $runner)) { throw 'Refusal omitted its runner receipt.' }
    $saved = (Get-FileHash $runner).Hash
    Assert-Refused { Complete $path $Phase }
    if ((Get-FileHash $runner).Hash -cne $saved) { throw 'An earlier runner receipt was overwritten.' }
}
# Exercise each actual DateTimeOffset producer consumer independently, then all three together.
$utcFailures = [Collections.Generic.List[string]]::new()
foreach ($consumer in @('completedUtc','createdUtc','updatedUtc','all')) {
    $directory = Join-Path $temp ('utc-wire-' + $consumer); New-Item $directory -ItemType Directory | Out-Null
    $phase = if ($consumer -ceq 'completedUtc') { 'stop-figma' } else { 'seed' }
    $r = New-Native $phase -UtcCompletion:($consumer -cin @('completedUtc','all'))
    Add-RecordFixture $r $directory -UtcSources:($consumer -cin @('createdUtc','all')) -UtcCases:($consumer -cin @('updatedUtc','all'))
    $path = Join-Path $directory ($phase + '.json'); Write-Json $path $r
    $before = @{ $path = (Get-FileHash -LiteralPath $path).Hash }
    foreach ($file in $r.evidence) { $p = Join-Path (Join-Path $directory ($phase + '-evidence')) $file.path; $before[$p] = (Get-FileHash -LiteralPath $p).Hash }
    try {
        Complete $path $phase -Leases $r.roots | Out-Null
        $script:cases++; Write-Host "Actual STJ UTC interop passed: $consumer"
    } catch {
        $utcFailures.Add($consumer); Write-Host "Actual STJ UTC interop failed: $consumer"
    } finally {
        foreach ($p in $before.Keys) { if ((Get-FileHash -LiteralPath $p).Hash -cne $before[$p]) { throw 'UTC validation changed original producer bytes.' } }
    }
}
if ($utcFailures.Count -ne 0) { throw ('Actual STJ UTC producer timestamps rejected: ' + ($utcFailures -join ',')) }
# Original explicit-Z timestamps remain valid through the same parser/predicate.
$module = Get-Module SourceAcceptanceProbe
$parsedZ = & $module { ConvertFrom-SourceAcceptanceJson ([Text.Encoding]::UTF8.GetBytes('{"stamp":"2026-11-08T12:00:00Z"}')) }
if (-not (& $module { param($v) Test-SourceAcceptanceUtc $v } $parsedZ.stamp)) { throw 'Explicit Z UTC was rejected.' }; $script:cases++
foreach ($consumer in @('completedUtc','createdUtc','updatedUtc')) {
    foreach ($bad in @('positive-offset','negative-offset','no-offset','wrong-type','null','missing','invalid-date','invalid-text')) {
        $directory = Join-Path $temp ('utc-refused-' + $consumer + '-' + $bad); New-Item $directory -ItemType Directory | Out-Null
        $phase = if ($consumer -ceq 'completedUtc') { 'stop-figma' } else { 'seed' }
        $r = New-Native $phase; Add-RecordFixture $r $directory
        $value = switch ($bad) {
            'positive-offset' { '2026-11-08T12:00:00+01:00' }
            'negative-offset' { '2026-11-08T12:00:00-07:30' }
            'no-offset' { '2026-11-08T12:00:00' }
            'wrong-type' { 0 }
            'null' { $null }
            'invalid-date' { '2026-02-30T12:00:00+00:00' }
            default { 'invalid-date-text' }
        }
        if ($consumer -ceq 'completedUtc') {
            if ($bad -ceq 'missing') { $r.Remove('completedUtc') } else { $r.completedUtc = $value }
        } else {
            $id = if ($consumer -ceq 'createdUtc') { '5'*32 } else { $r.records.caseSetId }
            Change-Evidence $r $directory $id { param($data)
                if ($bad -ceq 'missing') { $data.Remove($consumer) } else { $data[$consumer] = $value }
            }
        }
        $path = Join-Path $directory ($phase + '.json'); Write-Json $path $r
        $originalHash = (Get-FileHash -LiteralPath $path).Hash
        Assert-Refused { Complete $path $phase -Leases $r.roots }
        if ((Get-FileHash -LiteralPath $path).Hash -cne $originalHash) { throw 'UTC refusal rewrote the native report.' }
    }
}
Write-Host "UTC producer/parser focused coverage passed: $script:cases cases; synthetic fixture root: $temp"
if ($UtcInteropOnly) { return }
$expectedSeed = @('source-installed-same-page','source-passive-form','source-synthetic-dpapi','source-figma-happy','source-ai-selected-manual',
    'source-figma-http-failure','source-figma-malformed','source-ai-malformed','source-ai-partial-receipt','source-held-navigation',
    'source-figma-cancel-retained','source-ai-cancel-retained','source-project-switch-figma','source-project-switch-ai','source-durable-seed')
if ((@(Get-SourceAcceptanceRequiredChecks seed) -join '|') -cne ($expectedSeed -join '|') -or
    @(Get-SourceAcceptanceRequiredChecks verify).Count -ne 5 -or @(Get-SourceAcceptanceRequiredChecks stop-figma).Count -ne 8 -or
    @(Get-SourceAcceptanceRequiredChecks stop-ai).Count -ne 8) { throw 'Required four-phase/36-check contract differs.' }
# A complete source-only Stop report needs no virtual run/BMP/CSV (production validator remains separate).
$ok = Join-Path $temp 'ok.json'; Write-Json $ok (New-Native 'stop-figma')
Complete $ok | Out-Null; $script:cases++
foreach ($mutation in @(
    @{ n = 'missing-hold-binding'; m = { param($r) $r.Remove('holds') } },
    @{ n = 'wrong-hold-run'; m = { param($r) $r.holds[0].descriptor.runId = 'f' * 32 } },
    @{ n = 'wrong-hold-instance'; m = { param($r) $r.holds[0].descriptor.instanceId = 'f' * 32 } },
    @{ n = 'wrong-hold-root'; m = { param($r) $r.holds[0].root += '/foreign' } },
    @{ n = 'wrong-hold-nonce'; m = { param($r) $r.holds[0].descriptor.releaseId = 'foreign' } },
    @{ n = 'extra-hold'; m = { param($r) $r.holds += $r.holds[0] } },
    @{ n = 'missing-check'; m = { param($r) $r.checks = @($r.checks | Select-Object -Skip 1) } },
    @{ n = 'duplicate-check'; m = { param($r) $r.checks[1] = $r.checks[0] } },
    @{ n = 'extra-check'; m = { param($r) $r.checks += 'invented' } },
    @{ n = 'array-phase'; m = { param($r) $r.phase = @('stop-figma') } },
    @{ n = 'invalid-completed-time'; m = { param($r) $r.completedUtc = 0 } },
    @{ n = 'wrong-birth'; m = { param($r) $r.processCreationFileTimeUtc++ } },
    @{ n = 'string-birth'; m = { param($r) $r.processCreationFileTimeUtc = [string]$birth } },
    @{ n = 'wrong-pid'; m = { param($r) $r.processId++ } },
    @{ n = 'wrong-package'; m = { param($r) $r.packageSha256 = 'e' * 64 } },
    @{ n = 'wrong-source'; m = { param($r) $r.sourceCommit = 'e' * 40 } },
    @{ n = 'wrong-host'; m = { param($r) $r.hostDllSha256 = 'e' * 64 } },
    @{ n = 'wrong-phase'; m = { param($r) $r.phase = 'stop-ai' } },
    @{ n = 'wrong-root'; m = { param($r) $r.userRoot += '/other' } },
    @{ n = 'not-closed'; m = { param($r) $r.actualWindowClosed = $false } },
    @{ n = 'not-retired'; m = { param($r) $r.rootsRetired = $false } },
    @{ n = 'extra-root'; m = { param($r) $r.roots += (Join-Path $ownedRoot '.workspaces/projects/eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee') } },
    @{ n = 'wrong-root-layout'; m = { param($r) $r.roots += (Join-Path $ownedRoot '../foreign') } },
    @{ n = 'unknown-field'; m = { param($r) $r.secret = 'synthetic-private-body' } },
    @{ n = 'no-observation'; m = { param($r) $r.observations = @() } },
    @{ n = 'wrong-instance'; m = { param($r) $r.observations[0].instanceId = 'foreign' } },
    @{ n = 'unexpected-transport'; m = { param($r) $r.observations[0].figma.unexpected = 1 } },
    @{ n = 'oversize-evidence'; m = { param($r) $r.evidence = @(@{ projectId = 'default'; area = 'sources'; id = 'e' * 32; path = 'default/sources/' + ('e' * 32) + '.json'; length = 2097153; sha256 = $hash }) } },
    @{ n = 'traversal'; m = { param($r) $r.evidence = @(@{ projectId = 'default'; area = 'sources'; id = 'e' * 32; path = '../secret.bin'; length = 1; sha256 = $hash }) } }
)) { Exercise $mutation.n $mutation.m }
Exercise 'no-exit' {} 'stop-figma' { param($p) Complete $p -Exited $false }
Exercise 'bad-exit' {} 'stop-figma' { param($p) Complete $p -ExitCode 1 }
Exercise 'terminated' {} 'stop-figma' { param($p) Complete $p -Terminated $true }
Exercise 'no-leases' {} 'stop-figma' { param($p) Complete $p -Leases @() }
foreach ($phase in @('seed','verify','stop-ai')) { Exercise ('incomplete-records-' + $phase) {} $phase }
foreach ($case in @('duplicate-json','case-duplicate-json','trailing-json','oversized-json','linked-report','linked-parent')) {
    $dir = Join-Path $temp $case; New-Item $dir -ItemType Directory | Out-Null
    $path = Join-Path $dir 'stop-figma.json'
    $valid = ConvertTo-Json -InputObject (New-Native 'stop-figma') -Depth 30 -Compress
    switch ($case) {
        'duplicate-json' { [IO.File]::WriteAllText($path, $valid.Replace('"schemaVersion":1','"schemaVersion":1,"schemaVersion":1')) }
        'case-duplicate-json' { [IO.File]::WriteAllText($path, $valid.Replace('"schemaVersion":1','"schemaVersion":1,"SchemaVersion":1')) }
        'trailing-json' { [IO.File]::WriteAllText($path, $valid + '{}') }
        'oversized-json' { [IO.File]::WriteAllText($path, ' ' * 262145) }
        'linked-report' { $real = Join-Path $dir 'real.json'; [IO.File]::WriteAllText($real,$valid); New-Item -ItemType SymbolicLink -Path $path -Target $real | Out-Null }
        'linked-parent' { [IO.File]::WriteAllText($path,$valid); $alias = $dir + '-alias'; New-Item -ItemType SymbolicLink -Path $alias -Target $dir | Out-Null; $path = Join-Path $alias 'stop-figma.json' }
    }
    Assert-Refused { Complete $path }
}
# Complete seeded evidence validates real envelope bytes and selected manual trace semantics.
foreach ($phase in @('seed','verify','stop-ai')) {
    $dir = Join-Path $temp ('valid-' + $phase); New-Item $dir -ItemType Directory | Out-Null
    $path = Join-Path $dir ($phase + '.json'); $r = New-Native $phase; Add-RecordFixture $r $dir; Write-Json $path $r
    Complete $path $phase -Leases $r.roots | Out-Null; $script:cases++
}
foreach ($item in @(
    @{ n='array-figma-title'; mutate={ param($r,$d) Change-Evidence $r $d ('5'*32) { param($v) $v.title = @('Synthetic source acceptance file') } } },
    @{ n='invalid-source-time'; mutate={ param($r,$d) Change-Evidence $r $d ('5'*32) { param($v) $v.createdUtc = 0 } } },
    @{ n='numeric-provider-version'; mutate={ param($r,$d) Change-Evidence $r $d ('5'*32) { param($v) $v.providerVersion = 1 } } },
    @{ n='numeric-envelope-id'; mutate={ param($r,$d) Change-Evidence $r $d ('5'*32) { param($v) $v.id = [System.Numerics.BigInteger]::Parse('5'*32) } } },
    @{ n='missing-envelope'; mutate={ param($r,$d) $r.evidence = @($r.evidence | Where-Object id -CNE $r.records.happyReceiptId) } },
    @{ n='wrong-file-hash'; mutate={ param($r,$d) $r.evidence[0].sha256 = 'e' * 64 } },
    @{ n='wrong-manual-action'; mutate={ param($r,$d) Change-Evidence $r $d $r.records.caseSetId { param($v) $v.cases[0].steps[0].action = @{ kind='Click';elementId='foreign';value=$null } } } },
    @{ n='wrong-trace-index'; mutate={ param($r,$d) Change-Evidence $r $d $r.records.caseSetId { param($v) $v.cases[0].traces[0].criterionIndex = 2; $v.cases[0].steps[0].traces[0].criterionIndex = 2 } } },
    @{ n='wrong-case-order'; mutate={ param($r,$d) Change-Evidence $r $d $r.records.caseSetId { param($v) $v.cases = @($v.cases[1],$v.cases[0],$v.cases[2]) } } },
    @{ n='wrong-source-producer'; mutate={ param($r,$d) Change-Evidence $r $d $r.records.figmaId { param($v) $v.producerPluginId = 'com.example.foreign' } } },
    @{ n='figma-virtual'; mutate={ param($r,$d) Change-Evidence $r $d ('5'*32) { param($v) $v.items[0].virtualScenario = 'TaskFormPositive' } } },
    @{ n='foreign-evidence-path'; mutate={ param($r,$d) $r.evidence[0].path = 'default/secrets/key.bin' } },
    @{ n='linked-evidence'; mutate={ param($r,$d) $file=$r.evidence[0]; $p=Join-Path (Join-Path $d 'seed-evidence') $file.path; $real=$p+'.real'; [IO.File]::Move($p,$real); New-Item -ItemType SymbolicLink -Path $p -Target $real | Out-Null } },
    @{ n='wrong-lease-for-roster'; mutate={ param($r,$d) $r.roots = @($ownedRoot) } }
)) {
    $dir = Join-Path $temp ('semantic-' + $item.n); New-Item $dir -ItemType Directory | Out-Null
    $path = Join-Path $dir 'seed.json'; $r = New-Native seed; Add-RecordFixture $r $dir
    & $item.mutate $r $dir; Write-Json $path $r
    Assert-Refused { Complete $path seed -Leases $r.roots }
}
# A fully valid replacement navigation source is still forbidden across seed/verify durable binding.
$dir = Join-Path $temp 'restart-durable-changed'; New-Item $dir -ItemType Directory | Out-Null
$r = New-Native verify; Add-RecordFixture $r $dir
Change-Evidence $r $dir ('5'*32) { param($v) $v.createdUtc = '2026-11-08T13:00:00Z' }
$path = Join-Path $dir 'verify.json'; Write-Json $path $r
Assert-Refused { Complete $path verify -Leases $r.roots }
# Direct independent body guards: both hashes stay internally consistent, so only fixed selected-input/response/trace checks refuse.
$module = Get-Module SourceAcceptanceProbe
foreach ($kind in @('selected-input','observed-response','original-traces','partial-as-success')) {
    $dir = Join-Path $temp ('body-' + $kind); New-Item $dir -ItemType Directory | Out-Null
    $r = New-Native seed; Add-RecordFixture $r $dir
    $ff = @($r.evidence | Where-Object id -CEQ $r.records.figmaId)[0]; $rf = @($r.evidence | Where-Object id -CEQ $r.records.happyReceiptId)[0]
    $figma = Get-Content (Join-Path (Join-Path $dir 'seed-evidence') $ff.path) -Raw | ConvertFrom-Json -AsHashtable
    $source = Get-Content (Join-Path (Join-Path $dir 'seed-evidence') $rf.path) -Raw | ConvertFrom-Json -AsHashtable
    $raw = [Convert]::FromBase64String($source.originalBytes); $receipt = [Text.Encoding]::UTF8.GetString($raw) | ConvertFrom-Json -AsHashtable
    switch ($kind) {
        'selected-input' { $bytes = [Text.Encoding]::UTF8.GetBytes('{}'); $receipt.inputUtf8Base64 = [Convert]::ToBase64String($bytes); $receipt.inputSha256 = Hash $bytes }
        'observed-response' { $bytes = [Text.Encoding]::UTF8.GetBytes('{}'); $receipt.responseUtf8Base64 = [Convert]::ToBase64String($bytes); $receipt.responseSha256 = Hash $bytes }
        'original-traces' { $receipt.originalTraces = @($receipt.originalTraces[2],$receipt.originalTraces[0],$receipt.originalTraces[1]) }
    }
    $raw = [Text.Encoding]::UTF8.GetBytes((ConvertTo-Json -InputObject $receipt -Depth 20 -Compress))
    $source.originalBytes = [Convert]::ToBase64String($raw); $source.originalSha256 = Hash $raw
    Assert-Refused { & $module { param($s,$b,$f,$rev,$partial) Assert-SourceAcceptanceReceipt $s $b $f $rev $partial } $source $raw $figma $ff.sha256 ($kind -ceq 'partial-as-success') }
}
# Missing native facts cannot be supplied by the launcher; malformed sidecars never echo private text.
foreach ($key in @('processCreationFileTimeUtc','actualWindowClosed','sourceCommit','records','evidence')) {
    Exercise ('absent-' + $key) { param($r) $r.Remove($key) } | Out-Null
}
$huge = Join-Path $temp 'oversized-envelope.json'; [IO.File]::WriteAllBytes($huge,[byte[]]::new(2097153))
Assert-Refused { & $module { param($p) Read-SourceAcceptanceBytes $p 2097152 } $huge }
# Four phases accept numeric PID reuse only when the retained process birth differs; fail fast otherwise.
$rows = [Collections.Generic.List[string]]::new()
$phaseArgs = @{ SeedRoot=$ownedRoot; StopFigmaRoot=(Join-Path (Split-Path $ownedRoot) ('e'*32)); StopAiRoot=(Join-Path (Split-Path $ownedRoot) ('f'*32)); ExpectedPackageHash=$hash; ExpectedSourceCommit=$commit; ExpectedHostExeHash=$hash; ExpectedHostDllHash=$hash }
$completed = @(Invoke-SourceAcceptanceProbePhases @phaseArgs -OutputDirectory (Join-Path $temp 'phases-good') -Observe {
    param($phase,$root,$report,$observation)
    $rows.Add($phase); $observedBirth=$birth+$rows.Count
    $observation.ProcessId=501; $observation.ProcessCreationFileTimeUtc=$observedBirth; $observation.ProcessExitFileTimeUtc=$observedBirth+10000
    $observation.HasExited=$true; $observation.ExitCode=0
    $r=New-Native $phase $root; $r.processCreationFileTimeUtc=$observedBirth; Add-RecordFixture $r (Split-Path $report -Parent)
    $observation.LeaseRoots=$r.roots; Write-Json $report $r
})
if ($rows.Count -ne 4 -or $completed.Count -ne 4) { throw 'Four process birth tuples were not accepted.' }; $script:cases++
$failedRows = [Collections.Generic.List[string]]::new()
Assert-Refused { Invoke-SourceAcceptanceProbePhases @phaseArgs -OutputDirectory (Join-Path $temp 'phases-failed') -Observe {
    param($phase,$root,$report,$observation)
    $failedRows.Add($phase); $observation.ProcessId=501; $observation.ProcessCreationFileTimeUtc=$birth; $observation.ProcessExitFileTimeUtc=$birth+10000
    $observation.HasExited=$true; $observation.ExitCode=1; $observation.LeaseRoots=@($root)
    Write-Json $report (New-Native $phase $root)
} }
if ($failedRows.Count -ne 1) { throw 'A failed source phase launched a subsequent process.' }
Write-Host "Source report/identity/body/preservation regressions passed: $script:cases cases. Synthetic report fixtures only; no native execution."
