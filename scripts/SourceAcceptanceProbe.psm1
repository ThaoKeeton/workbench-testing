Set-StrictMode -Version Latest
$script:SourceFixtureId = 'com.projectworkbench.testing.source-acceptance'
$script:SourceModel = 'gpt-5.4-mini-2026-03-17'
function Get-SourceAcceptanceRequiredChecks {
    param([Parameter(Mandatory)][ValidateSet('seed','verify','stop-figma','stop-ai', IgnoreCase=$false)][string]$Phase)
    switch -CaseSensitive ($Phase) {
        seed { return @('source-installed-same-page','source-passive-form','source-synthetic-dpapi','source-figma-happy','source-ai-selected-manual',
            'source-figma-http-failure','source-figma-malformed','source-ai-malformed','source-ai-partial-receipt','source-held-navigation',
            'source-figma-cancel-retained','source-ai-cancel-retained','source-project-switch-figma','source-project-switch-ai','source-durable-seed') }
        verify { return @('source-restart-instance-reset','source-restart-records','source-restart-dpapi','source-restart-manual-ui','source-no-passive-publication') }
        default { return @('source-held-entered','source-release-descriptor-bound','source-stop-revokes-commands','source-stop-retains-work',
            'source-stop-refuses-new-instance','source-explicit-release','source-stop-joined','source-canceled-no-publication') }
    }
}
function Assert-SourceAcceptanceNoLinks([string]$Path) {
    for ($part = [IO.Path]::GetFullPath($Path); $part; $part = [IO.Path]::GetDirectoryName($part)) {
        $item = Get-Item -LiteralPath $part -Force -ErrorAction SilentlyContinue
        if ($null -ne $item -and ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'SourceUnsafePath' }
    }
}
function Read-SourceAcceptanceBytes([string]$Path, [int]$Maximum) {
    Assert-SourceAcceptanceNoLinks $Path
    $item = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    if ($item -isnot [IO.FileInfo] -or $item.Length -lt 1 -or $item.Length -gt $Maximum -or ($item.Attributes -band [IO.FileAttributes]::Device) -ne 0) { throw 'SourceFileBound' }
    $stream = $null
    try {
        $stream = [IO.File]::Open($Path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
        if ($stream.Length -lt 1 -or $stream.Length -gt $Maximum) { throw 'SourceFileBound' }
        $bytes = [byte[]]::new([int]$stream.Length); $stream.ReadExactly($bytes)
        if ($stream.ReadByte() -ne -1) { throw 'SourceFileChanged' }
        return ,$bytes
    } finally { if ($null -ne $stream) { $stream.Dispose() } }
}
function Test-SourceAcceptanceJsonNames([System.Text.Json.JsonElement]$Element) {
    if ($Element.ValueKind -eq [System.Text.Json.JsonValueKind]::Object) {
        $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($field in $Element.EnumerateObject()) { if (-not $names.Add($field.Name) -or -not (Test-SourceAcceptanceJsonNames $field.Value)) { return $false } }
    } elseif ($Element.ValueKind -eq [System.Text.Json.JsonValueKind]::Array) {
        foreach ($value in $Element.EnumerateArray()) { if (-not (Test-SourceAcceptanceJsonNames $value)) { return $false } }
    }
    return $true
}
function ConvertFrom-SourceAcceptanceJson([byte[]]$Bytes) {
    $document = $null
    try {
        $text = [Text.UTF8Encoding]::new($false,$true).GetString($Bytes).TrimStart([char]0xfeff)
        $options = [System.Text.Json.JsonDocumentOptions]::new(); $options.MaxDepth = 32
        $document = [System.Text.Json.JsonDocument]::Parse($text,$options)
        if ($document.RootElement.ValueKind -ne [System.Text.Json.JsonValueKind]::Object -or -not (Test-SourceAcceptanceJsonNames $document.RootElement)) { throw 'SourceJsonInvalid' }
        return ConvertFrom-Json -InputObject $text -AsHashtable -NoEnumerate -Depth 32 -DateKind String
    } finally { if ($null -ne $document) { $document.Dispose() } }
}
function Read-SourceAcceptanceJson([string]$Path, [int]$Maximum = 262144) { return ConvertFrom-SourceAcceptanceJson (Read-SourceAcceptanceBytes $Path $Maximum) }
function Assert-SourceAcceptanceKeys($Data, [string[]]$Names) {
    if ($Data -isnot [Collections.IDictionary] -or $Data.Count -ne $Names.Count) { throw 'SourceFieldsInvalid' }
    foreach ($key in $Data.Keys) { if ($key -cnotin $Names) { throw 'SourceFieldsInvalid' } }
}
function Assert-SourceAcceptanceStrings($Data, [string[]]$Names) {
    foreach ($name in $Names) { if ($Data[$name] -isnot [string]) { throw 'SourceStringTypeInvalid' } }
}
function Test-SourceAcceptanceUtc($Value) {
    # Keep the original JSON offset: STJ DateTimeOffset UTC emits +00:00; no Local/unspecified conversion.
    if ($Value -is [datetime]) { return $Value.Kind -eq [DateTimeKind]::Utc -and $Value -gt [datetime]'2000-01-01T00:00:00Z' }
    if ($Value -is [DateTimeOffset]) { return $Value.Offset -eq [TimeSpan]::Zero -and $Value -gt [DateTimeOffset]'2000-01-01T00:00:00Z' }
    if ($Value -isnot [string] -or $Value -cnotmatch '^20[0-9]{2}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(\.[0-9]{1,7})?(Z|\+00:00)$') { return $false }
    $parsed=[DateTimeOffset]::MinValue
    return [DateTimeOffset]::TryParse($Value,[Globalization.CultureInfo]::InvariantCulture,[Globalization.DateTimeStyles]::RoundtripKind,[ref]$parsed) -and $parsed.Offset -eq [TimeSpan]::Zero
}
function Test-SourceAcceptanceInteger($Value, [long]$Minimum = 0, [long]$Maximum = [long]::MaxValue) {
    return ($Value -is [int] -or $Value -is [long]) -and $Value -ge $Minimum -and $Value -le $Maximum
}
function Test-SourceAcceptanceHex($Value, [int]$Length) { return $Value -is [string] -and $Value -cmatch ('^[0-9a-f]{' + $Length + '}$') }
function Get-SourceAcceptanceHash([byte[]]$Bytes) { return [Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData($Bytes)) }
function Write-SourceAcceptanceNewJson([string]$Path, $Data) {
    Assert-SourceAcceptanceNoLinks $Path
    $parent = Split-Path $Path -Parent; [IO.Directory]::CreateDirectory($parent) | Out-Null
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes((ConvertTo-Json -InputObject $Data -Depth 32))
    $file = [IO.File]::Open($Path,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
    try { $file.Write($bytes); $file.Flush($true) } finally { $file.Dispose() }
}
function Assert-SourceAcceptanceRoot([string]$Root) {
    $parent = [IO.Path]::TrimEndingDirectorySeparator((Join-Path ([IO.Path]::GetTempPath()) 'ProjectWorkbench-source-acceptance-probe'))
    if ([IO.Path]::GetFullPath($Root) -cne $Root -or [IO.Path]::GetDirectoryName($Root) -ine $parent -or
        -not (Test-SourceAcceptanceHex ([IO.Path]::GetFileName($Root)) 32)) { throw 'SourceRootInvalid' }
    Assert-SourceAcceptanceNoLinks $Root
}
function Assert-SourceAcceptanceCounter($Counter) {
    Assert-SourceAcceptanceKeys $Counter @('entered','accepted','settled','unexpected','limitExceeded','cancellationObserved')
    foreach ($name in @('entered','accepted','settled','unexpected')) { if (-not (Test-SourceAcceptanceInteger $Counter[$name] 0 32)) { throw 'SourceCountersInvalid' } }
    if ($Counter.entered -lt $Counter.accepted -or $Counter.accepted -lt $Counter.settled -or $Counter.unexpected -ne 0 -or
        $Counter.limitExceeded -isnot [bool] -or $Counter.limitExceeded -or $Counter.cancellationObserved -isnot [bool]) { throw 'SourceCountersInvalid' }
}
function Get-SourceAcceptanceExpectedInput([string]$Id, [string]$Revision) {
    return '{"sources":[{"snapshotId":"' + $Id + '","snapshotRevision":"' + $Revision + '","kind":"Figma","providerId":"figma.rest.nodes.manual-criteria","providerVersion":"1","documentId":"SourceAcceptanceFile1@source-v1","title":"Synthetic source acceptance file","items":[{"locator":"2:3","title":"Synthetic heading","description":"Observed type: TEXT\nObserved characters: Synthetic heading text","criteria":[{"criterionIndex":0,"text":"Synthetic heading is visible."},{"criterionIndex":1,"text":"Synthetic navigation is reviewed manually."}]},{"locator":"1:2","title":"Synthetic confirmation","description":"Observed type: TEXT\nObserved characters: Synthetic confirmation text","criteria":[{"criterionIndex":2,"text":"Synthetic confirmation is reviewed manually."}]}]}]}'
}
function Get-SourceAcceptanceExpectedResponse([bool]$Partial) {
    $prerequisites = if ($Partial) { 'p' * 4096 } else { 'Synthetic manual review only.' }
    $cases = foreach ($index in @(2,0,1)) { '{"criterionIndex":' + $index + ',"title":"Synthetic manual case ' + $index + '","prerequisites":"' + $prerequisites + '","steps":[{"instruction":"Review synthetic criterion ' + $index + ' manually.","expected":"Synthetic criterion ' + $index + ' is satisfied."}]}' }
    $generated = '{"cases":[' + ($cases -join ',') + ']}'
    # Same wire encoder, independently fixed oracle; never derives expected data from observed response/fixture state.
    $quoted = [System.Text.Json.JsonSerializer]::Serialize[string]($generated,[System.Text.Json.JsonSerializerOptions]::new())
    return ,[Text.Encoding]::UTF8.GetBytes(('{"id":"resp_source_acceptance_1","model":"' + $script:SourceModel + '","status":"completed","output":[{"type":"message","role":"assistant","status":"completed","content":[{"type":"output_text","text":' + $quoted + '}]}]}'))
}
function Assert-SourceAcceptanceTrace($Trace, [string]$Id, [string]$Revision, [string]$Locator, [int]$Criterion) {
    Assert-SourceAcceptanceKeys $Trace @('snapshotId','snapshotRevision','locator','criterionIndex')
    Assert-SourceAcceptanceStrings $Trace @('snapshotId','snapshotRevision','locator')
    if ($Trace.snapshotId -cne $Id -or $Trace.snapshotRevision -cne $Revision -or $Trace.locator -cne $Locator -or
        -not (Test-SourceAcceptanceInteger $Trace.criterionIndex $Criterion $Criterion)) { throw 'SourceTraceInvalid' }
}
function Assert-SourceAcceptanceSource($Source, [string]$Id, [string]$ProjectId) {
    Assert-SourceAcceptanceKeys $Source @('schemaVersion','id','projectId','producerPluginId','createdUtc','kind','providerId','providerVersion','documentId','title','originalBytes','originalSha256','items')
    Assert-SourceAcceptanceStrings $Source @('id','projectId','producerPluginId','kind','providerId','providerVersion','documentId','title','originalBytes','originalSha256')
    if (-not (Test-SourceAcceptanceUtc $Source.createdUtc)) { throw 'SourceTimestampInvalid' }
    if (-not (Test-SourceAcceptanceInteger $Source.schemaVersion 1 1) -or $Source.id -cne $Id -or $Source.projectId -cne $ProjectId -or
        $Source.producerPluginId -cne $script:SourceFixtureId -or $Source.originalBytes -isnot [string] -or
        -not (Test-SourceAcceptanceHex $Source.originalSha256 64) -or $Source.items -isnot [array] -or $Source.items.Count -lt 1) { throw 'SourceEnvelopeInvalid' }
    $original = [Convert]::FromBase64String($Source.originalBytes)
    if ($original.Length -gt 1048576 -or (Get-SourceAcceptanceHash $original) -cne $Source.originalSha256) { throw 'SourceOriginalHashInvalid' }
    foreach ($item in $Source.items) {
        Assert-SourceAcceptanceKeys $item @('locator','title','description','acceptanceCriteria','virtualScenario')
        Assert-SourceAcceptanceStrings $item @('locator','title','description')
        if ($null -ne $item.virtualScenario -and $item.virtualScenario -isnot [string]) { throw 'SourceVirtualScenarioInvalid' }
        foreach ($criterion in $item.acceptanceCriteria) { if ($criterion -isnot [string] -or $criterion.Length -lt 1 -or $criterion.Length -gt 4096) { throw 'SourceCriteriaInvalid' } }
        if ($item.acceptanceCriteria -isnot [array] -or $item.acceptanceCriteria.Count -lt 1) { throw 'SourceCriteriaInvalid' }
    }
    return ,$original
}
function Assert-SourceAcceptanceFigma($Source, [byte[]]$Original) {
    $figma = '{"name":"Synthetic source acceptance file","version":"source-v1","nodes":{"2:3":{"document":{"id":"2:3","name":"Synthetic heading","type":"TEXT","characters":"Synthetic heading text"}},"1:2":{"document":{"id":"1:2","name":"Synthetic confirmation","type":"TEXT","characters":"Synthetic confirmation text"}}}}'
    if ($Source.kind -cne 'Figma' -or $Source.providerId -cne 'figma.rest.nodes.manual-criteria' -or $Source.providerVersion -cne '1' -or
        $Source.documentId -cne 'SourceAcceptanceFile1@source-v1' -or $Source.title -cne 'Synthetic source acceptance file' -or $Source.items.Count -ne 2 -or
        [Text.UTF8Encoding]::new($false,$true).GetString($Original) -cne $figma) { throw 'SourceFigmaInvalid' }
    $titles = @('Synthetic heading','Synthetic confirmation'); $locators = @('2:3','1:2')
    $criteria = @(@('Synthetic heading is visible.','Synthetic navigation is reviewed manually.'), @('Synthetic confirmation is reviewed manually.'))
    for ($i = 0; $i -lt 2; $i++) {
        $item = $Source.items[$i]
        if ($item.locator -cne $locators[$i] -or $item.title -cne $titles[$i] -or $item.description -cne ("Observed type: TEXT`nObserved characters: " + $titles[$i] + ' text') -or
            $null -ne $item.virtualScenario -or ($item.acceptanceCriteria -join "`n") -cne ($criteria[$i] -join "`n")) { throw 'SourceFigmaCriteriaInvalid' }
    }
}
function Assert-SourceAcceptanceReceipt($Source, [byte[]]$Original, $Figma, [string]$FigmaRevision, [bool]$Partial) {
    if ($Source.kind -cne 'Requirements' -or $Source.providerId -cne 'openai.responses.test-cases' -or $Source.providerVersion -cne '1' -or
        $Source.documentId -cne 'resp_source_acceptance_1' -or $Source.title -cne 'AI generation receipt (manual review required)' -or $Source.items.Count -ne 3) { throw 'SourceReceiptInvalid' }
    $receipt = ConvertFrom-SourceAcceptanceJson $Original
    Assert-SourceAcceptanceKeys $receipt @('schemaVersion','requestedModel','observedModel','responseId','inputUtf8Base64','inputSha256','responseUtf8Base64','responseSha256','originalTraces')
    Assert-SourceAcceptanceStrings $receipt @('requestedModel','observedModel','responseId','inputUtf8Base64','inputSha256','responseUtf8Base64','responseSha256')
    if (-not (Test-SourceAcceptanceInteger $receipt.schemaVersion 1 1) -or $receipt.requestedModel -cne $script:SourceModel -or $receipt.observedModel -cne $script:SourceModel -or
        $receipt.responseId -cne 'resp_source_acceptance_1' -or $receipt.originalTraces -isnot [array] -or $receipt.originalTraces.Count -ne 3) { throw 'SourceReceiptIdentityInvalid' }
    $inputBytes = [Convert]::FromBase64String($receipt.inputUtf8Base64); $responseBytes = [Convert]::FromBase64String($receipt.responseUtf8Base64)
    $expectedInput = [Text.Encoding]::UTF8.GetBytes((Get-SourceAcceptanceExpectedInput $Figma.id $FigmaRevision)); $expectedResponse = Get-SourceAcceptanceExpectedResponse $Partial
    if (-not [Linq.Enumerable]::SequenceEqual[byte]($inputBytes,$expectedInput) -or -not [Linq.Enumerable]::SequenceEqual[byte]($responseBytes,$expectedResponse) -or
        $receipt.inputSha256 -cne (Get-SourceAcceptanceHash $inputBytes) -or $receipt.responseSha256 -cne (Get-SourceAcceptanceHash $responseBytes)) { throw 'SourceReceiptBodyInvalid' }
    Assert-SourceAcceptanceTrace $receipt.originalTraces[0] $Figma.id $FigmaRevision '2:3' 0
    Assert-SourceAcceptanceTrace $receipt.originalTraces[1] $Figma.id $FigmaRevision '2:3' 1
    Assert-SourceAcceptanceTrace $receipt.originalTraces[2] $Figma.id $FigmaRevision '1:2' 0
    $order = @(2,0,1)
    for ($i = 0; $i -lt 3; $i++) {
        $item = $Source.items[$i]; $index = $order[$i]
        if ($item.locator -cne ('case-' + ($i + 1)) -or $item.title -cne "Synthetic manual case $index" -or $item.description -cne 'AI generated; manual review required.' -or
            $null -ne $item.virtualScenario -or $item.acceptanceCriteria.Count -ne 1 -or $item.acceptanceCriteria[0] -cne "Synthetic criterion $index is satisfied.") { throw 'SourceReceiptItemsInvalid' }
    }
}
function Assert-SourceAcceptanceCases($Set, [string]$Id, [string]$ProjectId, $Figma, [string]$FigmaRevision, $Receipt, [string]$ReceiptRevision) {
    Assert-SourceAcceptanceKeys $Set @('schemaVersion','id','projectId','producerPluginId','updatedUtc','name','cases')
    Assert-SourceAcceptanceStrings $Set @('id','projectId','producerPluginId','name')
    if (-not (Test-SourceAcceptanceUtc $Set.updatedUtc)) { throw 'SourceTimestampInvalid' }
    if (-not (Test-SourceAcceptanceInteger $Set.schemaVersion 1 1) -or $Set.id -cne $Id -or $Set.projectId -cne $ProjectId -or $Set.producerPluginId -cne $script:SourceFixtureId -or
        $Set.name -cne 'AI 测试用例（待人工审核）' -or $Set.cases -isnot [array] -or $Set.cases.Count -ne 3) { throw 'SourceCasesInvalid' }
    $order = @(2,0,1); $ids = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    for ($i = 0; $i -lt 3; $i++) {
        $case = $Set.cases[$i]; $index = $order[$i]
        Assert-SourceAcceptanceKeys $case @('id','title','prerequisites','traces','steps')
        Assert-SourceAcceptanceStrings $case @('id','title','prerequisites')
        if (-not (Test-SourceAcceptanceHex $case.id 32) -or -not $ids.Add($case.id) -or $case.title -cne "Synthetic manual case $index" -or
            $case.prerequisites -cne ("AI generated; manual review required. Model: " + $script:SourceModel + ".`nSynthetic manual review only.") -or
            $case.traces -isnot [array] -or $case.traces.Count -ne 2 -or $case.steps -isnot [array] -or $case.steps.Count -ne 1) { throw 'SourceManualCaseInvalid' }
        $step = $case.steps[0]; Assert-SourceAcceptanceKeys $step @('id','instruction','expected','traces','action')
        Assert-SourceAcceptanceStrings $step @('id','instruction','expected')
        if (-not (Test-SourceAcceptanceHex $step.id 32) -or -not $ids.Add($step.id) -or $null -ne $step.action -or $step.instruction -cne "Review synthetic criterion $index manually." -or
            $step.expected -cne "Synthetic criterion $index is satisfied." -or $step.traces -isnot [array] -or $step.traces.Count -ne 2) { throw 'SourceManualStepInvalid' }
        $locator = if ($index -eq 2) { '1:2' } else { '2:3' }; $local = if ($index -eq 1) { 1 } else { 0 }
        foreach ($traces in @($case.traces,$step.traces)) {
            Assert-SourceAcceptanceTrace $traces[0] $Figma.id $FigmaRevision $locator $local
            Assert-SourceAcceptanceTrace $traces[1] $Receipt.id $ReceiptRevision ('case-' + ($i + 1)) 0
        }
    }
}
function Assert-SourceAcceptanceEvidence($Native, [string]$ReportPath) {
    $records = $Native.records; Assert-SourceAcceptanceKeys $records @('figmaId','happyReceiptId','partialReceiptId','caseSetId','seedInstanceId','secondaryId')
    if (-not (Test-SourceAcceptanceHex $records.seedInstanceId 32) -or $Native.evidence -isnot [array] -or $Native.evidence.Count -gt 192) { throw 'SourceEvidenceInvalid' }
    $projects = @('default')
    if ($Native.phase -cin @('seed','verify')) {
        foreach ($key in @('figmaId','happyReceiptId','partialReceiptId','caseSetId','secondaryId')) { if (-not (Test-SourceAcceptanceHex $records[$key] 32)) { throw 'SourceRecordsIncomplete' } }
        if (@($records.figmaId,$records.happyReceiptId,$records.partialReceiptId,$records.caseSetId | Sort-Object -Unique).Count -ne 4) { throw 'SourceRecordsIncomplete' }
        $projects += $records.secondaryId
    } elseif ($Native.phase -ceq 'stop-ai') {
        if (-not (Test-SourceAcceptanceHex $records.figmaId 32)) { throw 'SourceRecordsIncomplete' }
        foreach ($key in @('happyReceiptId','partialReceiptId','caseSetId','secondaryId')) { if ($null -ne $records[$key]) { throw 'SourceUnexpectedRecord' } }
    } else { foreach ($key in @('figmaId','happyReceiptId','partialReceiptId','caseSetId','secondaryId')) { if ($null -ne $records[$key]) { throw 'SourceUnexpectedRecord' } } }
    $base = Join-Path (Split-Path $ReportPath -Parent) ($Native.phase + '-evidence'); $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $sources = @{}; $cases = @{}; $revisions = @{}
    foreach ($file in $Native.evidence) {
        Assert-SourceAcceptanceKeys $file @('projectId','area','id','path','length','sha256')
        Assert-SourceAcceptanceStrings $file @('projectId','area','id','path','sha256')
        if ($file.projectId -cnotin $projects -or $file.projectId -cne 'default' -or $file.area -cnotin @('sources','cases') -or -not (Test-SourceAcceptanceHex $file.id 32) -or
            $file.path -isnot [string] -or $file.path -cne ($file.projectId + '/' + $file.area + '/' + $file.id + '.json') -or -not $seen.Add($file.path) -or
            -not (Test-SourceAcceptanceInteger $file.length 1 2097152) -or -not (Test-SourceAcceptanceHex $file.sha256 64)) { throw 'SourceEvidencePathInvalid' }
        $bytes = Read-SourceAcceptanceBytes (Join-Path $base $file.path) 2097152
        if ($bytes.Length -ne $file.length -or (Get-SourceAcceptanceHash $bytes) -cne $file.sha256) { throw 'SourceEvidenceHashInvalid' }
        $text = [Text.UTF8Encoding]::new($false,$true).GetString($bytes)
        foreach ($secret in @('source-acceptance-figma-token','source-acceptance-openai-key')) { if ($text.Contains($secret,[StringComparison]::Ordinal)) { throw 'SourceEvidenceSecret' } }
        $data = ConvertFrom-SourceAcceptanceJson $bytes
        if ($file.area -ceq 'sources') { $sources[$file.id] = @{ data = $data; original = (Assert-SourceAcceptanceSource $data $file.id $file.projectId) } }
        else { $cases[$file.id] = $data }
        $revisions[$file.id] = $file.sha256
    }
    $wantedSources = if ($Native.phase -cin @('seed','verify')) { 5 } elseif ($Native.phase -ceq 'stop-ai') { 1 } else { 0 }
    $wantedCases = if ($Native.phase -cin @('seed','verify')) { 1 } else { 0 }
    if ($sources.Count -ne $wantedSources -or $cases.Count -ne $wantedCases) { throw 'SourceRecordsIncomplete' }
    if ($null -ne $records.figmaId) {
        if (-not $sources.ContainsKey($records.figmaId)) { throw 'SourceRecordsIncomplete' }
        Assert-SourceAcceptanceFigma $sources[$records.figmaId].data $sources[$records.figmaId].original
    }
    if ($Native.phase -cin @('seed','verify')) {
        foreach ($id in @($records.happyReceiptId,$records.partialReceiptId)) { if (-not $sources.ContainsKey($id)) { throw 'SourceRecordsIncomplete' } }
        if (-not $cases.ContainsKey($records.caseSetId)) { throw 'SourceRecordsIncomplete' }
        $figma = $sources[$records.figmaId].data
        Assert-SourceAcceptanceReceipt $sources[$records.happyReceiptId].data $sources[$records.happyReceiptId].original $figma $revisions[$records.figmaId] $false
        Assert-SourceAcceptanceReceipt $sources[$records.partialReceiptId].data $sources[$records.partialReceiptId].original $figma $revisions[$records.figmaId] $true
        Assert-SourceAcceptanceCases $cases[$records.caseSetId] $records.caseSetId 'default' $figma $revisions[$records.figmaId] $sources[$records.happyReceiptId].data $revisions[$records.happyReceiptId]
        $other = @($sources.Keys | Where-Object { $_ -cnotin @($records.figmaId,$records.happyReceiptId,$records.partialReceiptId) })
        $navigation = @($other | Where-Object { $sources[$_].data.providerId -ceq 'figma.rest.nodes.manual-criteria' })
        $decoy = @($other | Where-Object { $sources[$_].data.providerId -cne 'figma.rest.nodes.manual-criteria' })
        if ($navigation.Count -ne 1 -or $decoy.Count -ne 1) { throw 'SourceRecordsIncomplete' }
        Assert-SourceAcceptanceFigma $sources[$navigation[0]].data $sources[$navigation[0]].original
        if ($sources[$decoy[0]].data.kind -cne 'Requirements') { throw 'SourceDecoyInvalid' }
    }
}
function Assert-SourceAcceptanceHolds($Native) {
    [string[]]$providers = @(if ($Native.phase -ceq 'seed') { @('figma','figma','ai','figma','ai') } elseif ($Native.phase -ceq 'stop-ai') { @('ai') } elseif ($Native.phase -ceq 'stop-figma') { @('figma') } else { @() })
    if ($Native.holds -isnot [array] -or $Native.holds.Count -ne $providers.Count) { throw 'SourceHeldBindingIncomplete' }
    $nonces = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $tickets = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    for ($i=0; $i -lt $providers.Count; $i++) {
        $hold=$Native.holds[$i]; Assert-SourceAcceptanceKeys $hold @('root','runtimeGeneration','descriptor')
        Assert-SourceAcceptanceStrings $hold @('root')
        $d=$hold.descriptor
        Assert-SourceAcceptanceKeys $d @('protocolVersion','fixtureId','pluginId','runId','projectId','instanceId','generation','provider','scenario','releaseId')
        Assert-SourceAcceptanceStrings $d @('fixtureId','pluginId','runId','projectId','instanceId','provider','scenario','releaseId')
        if ($hold.root -cne $Native.userRoot -or -not (Test-SourceAcceptanceInteger $hold.runtimeGeneration 1) -or
            -not (Test-SourceAcceptanceInteger $d.protocolVersion 1 1) -or $d.fixtureId -cne $script:SourceFixtureId -or $d.pluginId -cne $script:SourceFixtureId -or
            $d.runId -cne [IO.Path]::GetFileName($Native.userRoot) -or $d.projectId -cne 'default' -or -not (Test-SourceAcceptanceHex $d.instanceId 32) -or
            -not (Test-SourceAcceptanceInteger $d.generation 1 32) -or $d.provider -cne $providers[$i] -or $d.scenario -cne 'hold' -or
            -not (Test-SourceAcceptanceHex $d.releaseId 32) -or -not $nonces.Add($d.releaseId) -or -not $tickets.Add($d.instanceId + ':' + $d.generation)) { throw 'SourceHeldBindingInvalid' }
        if ($Native.phase -cin @('stop-figma','stop-ai')) {
            $row=$Native.observations[0]
            if ($row.instanceId -cne $d.instanceId -or $row.generation -ne $d.generation -or $row.runtimeGeneration -ne $hold.runtimeGeneration) { throw 'SourceHeldBindingInvalid' }
        }
    }
}
function Assert-SourceAcceptanceNative($Native, [string]$ReportPath, [string]$Phase, [string]$Root, [int]$ProcessId, [long]$Birth,
    [string]$PackageHash, [string]$SourceCommit, [string]$HostExeHash, [string]$HostDllHash, [string[]]$LeaseRoots) {
    Assert-SourceAcceptanceKeys $Native @('schemaVersion','kind','succeeded','phase','userRoot','sourceCommit','packageSha256','hostExeSha256','hostDllSha256',
        'processId','processCreationFileTimeUtc','actualWindowClosed','rootsRetired','roots','osDescription','processArchitecture','checks','observations','holds','records','evidence','completedUtc')
    Assert-SourceAcceptanceStrings $Native @('kind','phase','userRoot','sourceCommit','packageSha256','hostExeSha256','hostDllSha256','osDescription','processArchitecture')
    if (-not (Test-SourceAcceptanceUtc $Native.completedUtc)) { throw 'SourceTimestampInvalid' }
    if (-not (Test-SourceAcceptanceInteger $Native.schemaVersion 1 1) -or $Native.kind -cne 'SourceAcceptanceNative' -or $Native.succeeded -isnot [bool] -or -not $Native.succeeded -or
        $Native.phase -cne $Phase -or $Native.userRoot -cne $Root -or -not (Test-SourceAcceptanceInteger $Native.processId 1 ([int]::MaxValue)) -or $Native.processId -ne $ProcessId -or
        -not (Test-SourceAcceptanceInteger $Native.processCreationFileTimeUtc 1) -or $Native.processCreationFileTimeUtc -ne $Birth -or
        $Native.actualWindowClosed -isnot [bool] -or -not $Native.actualWindowClosed -or $Native.rootsRetired -isnot [bool] -or -not $Native.rootsRetired -or
        $Native.osDescription -isnot [string] -or $Native.osDescription.Length -lt 1 -or $Native.osDescription.Length -gt 256 -or $Native.processArchitecture -cne 'X64') { throw 'SourceNativeIdentityOrRetirementMissing' }
    foreach ($pair in @(@('packageSha256',$PackageHash),@('hostExeSha256',$HostExeHash),@('hostDllSha256',$HostDllHash),@('sourceCommit',$SourceCommit))) {
        if ($Native[$pair[0]] -isnot [string] -or $Native[$pair[0]] -cne $pair[1]) { throw 'SourceProvenanceMismatch' }
    }
    Assert-SourceAcceptanceRoot $Root
    if ($Native.roots -isnot [array] -or $Native.roots.Count -lt 1 -or $Native.roots.Count -gt 2) { throw 'SourceLeaseRootsInvalid' }
    $roots = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($owned in $Native.roots) {
        if ($owned -isnot [string] -or -not $roots.Add($owned)) { throw 'SourceLeaseRootsInvalid' }
        if ($owned -cne $Root -and ([IO.Path]::GetDirectoryName($owned) -cne (Join-Path $Root '.workspaces/projects') -or -not (Test-SourceAcceptanceHex ([IO.Path]::GetFileName($owned)) 32))) { throw 'SourceLeaseRootsInvalid' }
        Assert-SourceAcceptanceNoLinks $owned
    }
    if (-not $roots.Contains($Root) -or $roots.Count -ne $LeaseRoots.Count -or -not $roots.SetEquals($LeaseRoots)) { throw 'SourceRootStillOwned' }
    $required = @(Get-SourceAcceptanceRequiredChecks $Phase)
    if ($Native.checks -isnot [array] -or $Native.checks.Count -ne $required.Count -or $Native.observations -isnot [array] -or $Native.observations.Count -ne $required.Count) { throw 'SourceChecksIncomplete' }
    for ($i = 0; $i -lt $required.Count; $i++) {
        if ($Native.checks[$i] -isnot [string] -or $Native.checks[$i] -cne $required[$i]) { throw 'SourceChecksIncomplete' }
        $row = $Native.observations[$i]
        Assert-SourceAcceptanceKeys $row @('check','projectId','instanceId','generation','runtimeGeneration','figma','ai','sourceIds','caseSetIds','runCount')
        Assert-SourceAcceptanceStrings $row @('check','projectId','instanceId')
        if ($row.check -cne $required[$i] -or $row.projectId -cne 'default' -or -not (Test-SourceAcceptanceHex $row.instanceId 32) -or
            -not (Test-SourceAcceptanceInteger $row.generation 0 32) -or -not (Test-SourceAcceptanceInteger $row.runtimeGeneration 1) -or
            -not (Test-SourceAcceptanceInteger $row.runCount 0 0)) { throw 'SourceObservationInvalid' }
        Assert-SourceAcceptanceCounter $row.figma; Assert-SourceAcceptanceCounter $row.ai
        if ($row.figma.accepted + $row.ai.accepted -gt 32) { throw 'SourceCountersInvalid' }
        foreach ($ids in @($row.sourceIds,$row.caseSetIds)) {
            if ($ids -isnot [array] -or $ids.Count -gt 64) { throw 'SourceObservationInvalid' }
            $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
            foreach ($id in $ids) { if (-not (Test-SourceAcceptanceHex $id 32) -or -not $seen.Add($id)) { throw 'SourceObservationInvalid' } }
        }
    }
    Assert-SourceAcceptanceHolds $Native
    Assert-SourceAcceptanceEvidence $Native $ReportPath
    $expectedRoots = @($Root)
    if ($Phase -cin @('seed','verify')) { $expectedRoots += (Join-Path $Root ('.workspaces/projects/' + $Native.records.secondaryId)) }
    if (-not $roots.SetEquals([string[]]$expectedRoots)) { throw 'SourceLeaseRootsInvalid' }
    if ($Phase -ceq 'verify' -and $Native.observations[0].instanceId -ceq $Native.records.seedInstanceId) { throw 'SourceRestartInstanceReused' }
}
function Assert-SourceAcceptanceRestart($Seed, $Native) {
    if ($Seed -isnot [Collections.IDictionary] -or $Seed.phase -cne 'seed' -or $Native.phase -cne 'verify' -or $Seed.userRoot -cne $Native.userRoot -or
        ($Seed.processId -eq $Native.processId -and $Seed.processCreationFileTimeUtc -eq $Native.processCreationFileTimeUtc)) { throw 'SourceRestartDurableMismatch' }
    foreach ($key in @('figmaId','happyReceiptId','partialReceiptId','caseSetId','seedInstanceId','secondaryId')) {
        if ($Seed.records[$key] -cne $Native.records[$key]) { throw 'SourceRestartDurableMismatch' }
    }
    if ($Seed.evidence.Count -ne $Native.evidence.Count) { throw 'SourceRestartDurableMismatch' }
    foreach ($file in $Seed.evidence) {
        $match = @($Native.evidence | Where-Object path -CEQ $file.path)
        if ($match.Count -ne 1 -or $match[0].length -ne $file.length -or $match[0].sha256 -cne $file.sha256) { throw 'SourceRestartDurableMismatch' }
    }
}
function Complete-SourceAcceptanceProbeReport {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$ReportPath, [Parameter(Mandatory)][int]$ProcessId,
        [Parameter(Mandatory)][long]$ProcessCreationFileTimeUtc, [AllowNull()][object]$ProcessExitFileTimeUtc,
        [AllowNull()][object]$ExitCode, [Parameter(Mandatory)][bool]$HasExited,
        [Parameter(Mandatory)][ValidateSet('seed','verify','stop-figma','stop-ai', IgnoreCase=$false)][string]$ExpectedPhase,
        [Parameter(Mandatory)][string]$ExpectedRoot, [AllowEmptyCollection()][string[]]$LeaseRoots = @(),
        [Parameter(Mandatory)][string]$ExpectedPackageHash, [Parameter(Mandatory)][string]$ExpectedSourceCommit,
        [Parameter(Mandatory)][string]$ExpectedHostExeHash, [Parameter(Mandatory)][string]$ExpectedHostDllHash,
        [bool]$ObservationFailed = $false, [bool]$Terminated = $false, [AllowNull()][Collections.IDictionary]$ExpectedSeed = $null)
    $runnerPath = [IO.Path]::ChangeExtension($ReportPath,'runner.json'); Assert-SourceAcceptanceNoLinks $runnerPath
    if (Test-Path -LiteralPath $runnerPath) { throw 'SourcePhaseAlreadyCompleted' }
    $reason = $null; $nativeStatus = 'unavailable'; $native = $null
    try {
        if ($ObservationFailed) { throw 'SourceObservationFailed' }
        if ($Terminated) { throw 'SourceProcessTerminated' }
        if (-not $HasExited) { throw 'SourceProcessStillOwned' }
        if (-not (Test-SourceAcceptanceInteger $ExitCode 0 0) -or -not (Test-SourceAcceptanceInteger $ProcessCreationFileTimeUtc 1) -or
            -not (Test-SourceAcceptanceInteger $ProcessExitFileTimeUtc $ProcessCreationFileTimeUtc)) { throw 'SourceProcessFailed' }
        foreach ($expected in @($ExpectedPackageHash,$ExpectedHostExeHash,$ExpectedHostDllHash)) { if (-not (Test-SourceAcceptanceHex $expected 64)) { throw 'SourceExpectedIdentityInvalid' } }
        if (-not (Test-SourceAcceptanceHex $ExpectedSourceCommit 40)) { throw 'SourceExpectedIdentityInvalid' }
        $native = Read-SourceAcceptanceJson $ReportPath; $nativeStatus = 'available'
        Assert-SourceAcceptanceNative $native $ReportPath $ExpectedPhase $ExpectedRoot $ProcessId $ProcessCreationFileTimeUtc $ExpectedPackageHash $ExpectedSourceCommit $ExpectedHostExeHash $ExpectedHostDllHash $LeaseRoots
        if ($ExpectedPhase -ceq 'verify') { Assert-SourceAcceptanceRestart $ExpectedSeed $native }
    } catch {
        $allowed = @('SourceObservationFailed','SourceProcessTerminated','SourceProcessStillOwned','SourceProcessFailed','SourceNativeIdentityOrRetirementMissing',
            'SourceProvenanceMismatch','SourceRootStillOwned','SourceChecksIncomplete','SourceExpectedIdentityInvalid','SourceRestartDurableMismatch')
        $reason = if ($_.Exception.Message -cin $allowed) { $_.Exception.Message } else { 'SourceReportInvalid' }
    }
    $runner = [ordered]@{ schemaVersion = 1; kind = 'SourceAcceptanceRunner'; succeeded = $null -eq $reason; phase = $ExpectedPhase;
        processId = $ProcessId; processCreationFileTimeUtc = $ProcessCreationFileTimeUtc; processExitFileTimeUtc = $ProcessExitFileTimeUtc;
        hasExited = $HasExited; exitCode = $ExitCode; terminated = $Terminated; observationFailed = $ObservationFailed; leaseRoots = @($LeaseRoots);
        nativeReport = $nativeStatus; runnerError = $reason; sourceCommit = $ExpectedSourceCommit; packageSha256 = $ExpectedPackageHash;
        hostExeSha256 = $ExpectedHostExeHash; hostDllSha256 = $ExpectedHostDllHash; nativeReportSha256 = $null }
    if ($null -eq $reason) { $runner.nativeReportSha256 = Get-SourceAcceptanceHash (Read-SourceAcceptanceBytes $ReportPath 262144) }
    Write-SourceAcceptanceNewJson $runnerPath $runner
    if ($null -ne $reason) {
        # Safe fixed vocabulary only. Never echo report fields, paths, exception text, provider body or credentials.
        Write-Host (ConvertTo-Json -InputObject @{ kind = 'SourceAcceptanceFailure'; schemaVersion = 1; phase = $ExpectedPhase;
            processId = $ProcessId; processCreationFileTimeUtc = $ProcessCreationFileTimeUtc; hasExited = $HasExited; terminated = $Terminated; reason = $reason } -Compress)
        throw $reason
    }
    return $runner
}
function Invoke-SourceAcceptanceProbePhases {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$OutputDirectory, [Parameter(Mandatory)][string]$SeedRoot,
        [Parameter(Mandatory)][string]$StopFigmaRoot, [Parameter(Mandatory)][string]$StopAiRoot,
        [Parameter(Mandatory)][string]$ExpectedPackageHash, [Parameter(Mandatory)][string]$ExpectedSourceCommit,
        [Parameter(Mandatory)][string]$ExpectedHostExeHash, [Parameter(Mandatory)][string]$ExpectedHostDllHash,
        [Parameter(Mandatory)][scriptblock]$Observe)
    foreach ($root in @($SeedRoot,$StopFigmaRoot,$StopAiRoot)) { Assert-SourceAcceptanceRoot $root }
    if (@($SeedRoot,$StopFigmaRoot,$StopAiRoot | Sort-Object -Unique).Count -ne 3) { throw 'SourceRootsMustDiffer' }
    Assert-SourceAcceptanceNoLinks $OutputDirectory; [IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
    $tuples = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal); $seed = $null
    foreach ($phase in @('seed','verify','stop-figma','stop-ai')) {
        $root = switch -CaseSensitive ($phase) { 'stop-figma' { $StopFigmaRoot } 'stop-ai' { $StopAiRoot } default { $SeedRoot } }
        $report = Join-Path $OutputDirectory "$phase.json"
        if (Test-Path -LiteralPath $report) { throw 'SourcePhaseAlreadyExists' }
        $observation = @{ ProcessId = 0; ProcessCreationFileTimeUtc = [long]0; ProcessExitFileTimeUtc = $null; HasExited = $false; ExitCode = $null;
            LeaseRoots = @(); Terminated = $false }; $errorRecord = $null
        try { & $Observe $phase $root $report $observation }
        catch { $errorRecord = $_ }
        $tuple = [string]$observation.ProcessId + ':' + [string]$observation.ProcessCreationFileTimeUtc
        if (-not $tuples.Add($tuple)) { $errorRecord = 'SourceProcessIdentityReused' }
        try {
            Complete-SourceAcceptanceProbeReport -ReportPath $report -ProcessId $observation.ProcessId -ProcessCreationFileTimeUtc $observation.ProcessCreationFileTimeUtc `
                -ProcessExitFileTimeUtc $observation.ProcessExitFileTimeUtc -ExitCode $observation.ExitCode -HasExited $observation.HasExited `
                -ExpectedPhase $phase -ExpectedRoot $root -LeaseRoots $observation.LeaseRoots -ExpectedPackageHash $ExpectedPackageHash `
                -ExpectedSourceCommit $ExpectedSourceCommit -ExpectedHostExeHash $ExpectedHostExeHash -ExpectedHostDllHash $ExpectedHostDllHash `
                -Terminated $observation.Terminated -ObservationFailed ($null -ne $errorRecord) -ExpectedSeed $seed
            if ($phase -ceq 'seed') { $seed = Read-SourceAcceptanceJson $report }
        } catch { if ($null -ne $errorRecord) { throw $errorRecord }; throw }
    }
}
Export-ModuleMember -Function Get-SourceAcceptanceRequiredChecks, Complete-SourceAcceptanceProbeReport, Invoke-SourceAcceptanceProbePhases
