Set-StrictMode -Version Latest
function Get-TestingPluginPackageName { return 'workbench-testing-plugin-0.2.0.zip' }
function Get-TestingRequiredChecks {
    param([string]$Phase)
    if ($Phase -ceq 'seed') {
        return @('independent-zip-native-page','packaged-requirements-figma-io','traceable-manual-mapped-generation',
            'immediate-metadata-csv-bytes','immediate-step-mapping-invalidation','actual-file-share-save-failure-close-correction',
            'conflict-preserve-copy-reload-bytes','positive-actual-journal-bmp-native-decoder','negative-empty-title-run',
            'labeled-deliberate-assertion-failure','cancel-real-owned-store-io-before-first-step','native-project-isolation-empty-read',
            'synthetic-inflight-journal-seed-no-action','immediate-final-input-before-leave')
    }
    return @('separate-process-context-interrupted-synthetic-seed','restart-final-native-input-persisted',
        'reopened-exact-observed-text-bmp-native-decoder','linked-new-session-retry-frozen-step-one','restart-project-roster-preserved')
}
function Test-TestingProbeJsonNames {
    param([System.Text.Json.JsonElement]$Element)
    if ($Element.ValueKind -eq [System.Text.Json.JsonValueKind]::Object) {
        $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($property in $Element.EnumerateObject()) {
            if (-not $names.Add($property.Name) -or -not (Test-TestingProbeJsonNames $property.Value)) { return $false }
        }
    }
    elseif ($Element.ValueKind -eq [System.Text.Json.JsonValueKind]::Array) {
        foreach ($value in $Element.EnumerateArray()) { if (-not (Test-TestingProbeJsonNames $value)) { return $false } }
    }
    return $true
}
function Read-TestingProbeReportData {
    param([string]$Path)
    # Reports are isolated opt-in probe data, but are still untrusted. Never follow links or read unbounded bytes.
    $stream = $null; $document = $null; $status = 'unreadable'
    try {
        $full = [IO.Path]::GetFullPath($Path); $leaf = $null
        for ($part = $full; $part; $part = [IO.Path]::GetDirectoryName($part)) {
            $item = $null
            try { $item = Get-Item -LiteralPath $part -Force -ErrorAction Stop }
            catch [Management.Automation.ItemNotFoundException] { }
            if ($null -ne $item) {
                if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { return @{ Status = 'unsafe-path'; Data = $null } }
                if ($part -ceq $full) { $leaf = $item }
            }
        }
        if ($null -eq $leaf) { return @{ Status = 'missing'; Data = $null } }
        if ($leaf -isnot [IO.FileInfo]) { return @{ Status = 'invalid'; Data = $null } }
        if ($leaf.Length -gt 65536) { return @{ Status = 'oversized'; Data = $null } }
        $stream = [IO.File]::Open($full, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        if ($stream.Length -gt 65536) { return @{ Status = 'oversized'; Data = $null } }
        $buffer = [byte[]]::new(65537); $count = 0
        while ($count -lt $buffer.Length) {
            $read = $stream.Read($buffer, $count, $buffer.Length - $count)
            if ($read -eq 0) { break }
            $count += $read
        }
        if ($count -gt 65536) { return @{ Status = 'oversized'; Data = $null } }
        $status = 'invalid'
        $text = [Text.UTF8Encoding]::new($false, $true).GetString($buffer, 0, $count).TrimStart([char]0xfeff)
        $options = [System.Text.Json.JsonDocumentOptions]::new(); $options.MaxDepth = 16
        $document = [System.Text.Json.JsonDocument]::Parse($text, $options)
        if ($document.RootElement.ValueKind -ne [System.Text.Json.JsonValueKind]::Object -or -not (Test-TestingProbeJsonNames $document.RootElement)) { return @{ Status = 'invalid'; Data = $null } }
        $data = ConvertFrom-Json -InputObject $text -AsHashtable -NoEnumerate -Depth 16
        return @{ Status = 'available'; Data = $data }
    }
    catch { return @{ Status = $status; Data = $null } }
    finally {
        if ($null -ne $document) { $document.Dispose() }
        if ($null -ne $stream) { $stream.Dispose() }
    }
}
function Test-TestingProbeInteger {
    param([AllowNull()][object]$Value, [int]$Minimum, [int]$Maximum)
    return (($Value -is [int] -or $Value -is [long]) -and $Value -ge $Minimum -and $Value -le $Maximum)
}
function Get-TestingProbeErrorCategory {
    param([AllowNull()][object]$Value, [string]$Prefix)
    if ($Value -isnot [string]) { return $null }
    foreach ($category in @('Canceled','InvalidData','Storage','PluginFailure')) {
        if ($Value -ceq "$Prefix ($category).") {
            if ($category -ceq 'PluginFailure') { return 'Unexpected' }
            return $category
        }
    }
    return $null
}
function ConvertTo-TestingProbeNativeDiagnostic {
    param([Collections.IDictionary]$Data, [string]$Phase, [int]$ProcessId, [bool]$Sidecar)
    $failure = @{ category = 'Unavailable'; sourceFile = $null; sourceLine = $null }
    # The ordinary host failure report has no native identity/checks. Only its closed stage/error categories are useful.
    if (-not $Sidecar -and -not $Data.Contains('schemaVersion')) {
        $category = Get-TestingProbeErrorCategory $Data['error'] 'HostOperationFailed'
        if ($Data['succeeded'] -isnot [bool] -or $Data['succeeded'] -or $null -eq $category -or
            $Data['stage'] -isnot [string] -or $Data['stage'] -cnotin @('initialize-or-native-smoke','unhandled-ui-exception','launch-callback-failed','shutdown','testing-closed-lease')) { throw 'TestingDiagnosticInvalid' }
        $failure.category = $category
        return @{ ProcessId = $null; Checks = @(); Failure = $failure; Stage = $Data['stage'] }
    }
    $phaseKey = if ($Sidecar) { 'phase' } else { 'probe' }
    if (-not (Test-TestingProbeInteger $Data['schemaVersion'] 1 1) -or $Data['succeeded'] -isnot [bool] -or
        ($Sidecar -and $Data['succeeded']) -or $Data[$phaseKey] -isnot [string] -or $Data[$phaseKey] -cne $Phase -or
        -not (Test-TestingProbeInteger $Data['processId'] 1 ([int]::MaxValue)) -or $Data['processId'] -ne $ProcessId -or
        $Data['checks'] -isnot [array]) { throw 'TestingDiagnosticInvalid' }
    $required = @(Get-TestingRequiredChecks $Phase); $checks = @($Data['checks'])
    if ($checks.Count -gt $required.Count) { throw 'TestingDiagnosticInvalid' }
    for ($index = 0; $index -lt $checks.Count; $index++) {
        if ($checks[$index] -isnot [string] -or $checks[$index] -cne $required[$index]) { throw 'TestingDiagnosticInvalid' }
    }
    if ($Sidecar) {
        $category = Get-TestingProbeErrorCategory $Data['safeError'] 'TestingNativeProbeFailed'
        if ($null -eq $category) { throw 'TestingDiagnosticInvalid' }
        $failure.category = $category
        if ($Data.Contains('failure')) {
            $detail = $Data['failure']
            if ($detail -isnot [Collections.IDictionary] -or $detail['category'] -isnot [string] -or
                $detail['category'] -cnotin @('Assertion','Canceled','InvalidData','Storage','Unexpected')) { throw 'TestingDiagnosticInvalid' }
            $failure.category = $detail['category']
            if ($detail['category'] -ceq 'Assertion') {
                if ($null -ne $detail['sourceFile'] -or $null -ne $detail['sourceLine']) {
                    if ($detail['sourceFile'] -isnot [string] -or $detail['sourceFile'] -cnotin @('TestingNativeProbe.cs','TestingNativeProbe.Authoring.cs','TestingNativeProbe.Execution.cs') -or
                        -not (Test-TestingProbeInteger $detail['sourceLine'] 1 100000)) { throw 'TestingDiagnosticInvalid' }
                    $failure.sourceFile = $detail['sourceFile']; $failure.sourceLine = $detail['sourceLine']
                }
            }
            elseif ($null -ne $detail['sourceFile'] -or $null -ne $detail['sourceLine']) { throw 'TestingDiagnosticInvalid' }
            if ($null -ne $detail['operation']) {
                if ($detail['operation'] -isnot [string] -or $detail['operation'] -cnotin @('VerifySelectInterruptedRun','VerifyAwaitRetryReady',
                    'VerifyInvokeRetry','VerifyDiscoverRetry','VerifyAwaitRetryCompletion','VerifyValidateFrozenRetry','VerifyRoster')) { throw 'TestingDiagnosticInvalid' }
                $failure.operation = $detail['operation']
            }
            if ($null -ne $detail['testingErrorCode']) {
                if ($detail['testingErrorCode'] -isnot [string] -or $detail['testingErrorCode'] -cnotin @('TestingContextClosed','InvalidTestSource',
                    'InvalidCaseSet','InvalidRun','InvalidEvidence','RevisionConflict','StoreLimitExceeded','StoreIoFailure','UnsafePath','TestStoreCorrupt',
                    'RecoveryRequired','ManualStepUnsupported','TargetUnavailable','AssertionFailed','AdapterFailed','TestRecordNotFound','StoreBusy',
                    'InvalidId','InvalidProducer','TestingFailed')) { throw 'TestingDiagnosticInvalid' }
                $failure.testingErrorCode = $detail['testingErrorCode']
            }
        }
    }
    return @{ ProcessId = $Data['processId']; Checks = $checks; Failure = $failure; Stage = $null }
}
function Write-TestingPluginProbeFailureDiagnostic {
    param([string]$ReportPath, [int]$ProcessId, [AllowNull()][object]$ExitCode, [bool]$HasExited,
        [string]$ExpectedPhase, [bool]$LeaseAvailable, [bool]$ObservationFailed, [string]$RunnerError)
    if ($ExpectedPhase -cnotin @('seed','verify') -or $RunnerError -cnotin @('TestingReportUnreadable','TestingReportMissing','TestingReportInvalid',
        'TestingProcessStillOwned','TestingProcessFailed','TestingRootStillOwned','TestingIdentityOrRetirementMissing','TestingPackageHashMismatch',
        'TestingChecksIncomplete','TestingEvidenceInvalid','TestingObservationFailed')) { return }
    $diagnostic = [ordered]@{ kind = 'TestingPluginProbeFailure'; schemaVersion = 1; phase = $ExpectedPhase;
        processId = $ProcessId; hasExited = $HasExited; exitCode = $null; rootLeaseAvailable = $LeaseAvailable;
        observationFailed = $ObservationFailed; runnerError = $RunnerError; nativeReport = 'missing'; nativeSidecar = 'missing';
        nativeProcessId = $null; checks = @(); nativeStage = $null; failure = @{ category = 'Unavailable'; sourceFile = $null; sourceLine = $null } }
    if (Test-TestingProbeInteger $ExitCode ([int]::MinValue) ([int]::MaxValue)) { $diagnostic.exitCode = $ExitCode }
    foreach ($source in @(@{ Path = $ReportPath; Key = 'nativeReport'; Sidecar = $false }, @{ Path = ($ReportPath + '.evidence.json'); Key = 'nativeSidecar'; Sidecar = $true })) {
        $read = Read-TestingProbeReportData $source.Path
        $diagnostic[$source.Key] = $read.Status
        if ($read.Status -ceq 'available') {
            try {
                $safe = ConvertTo-TestingProbeNativeDiagnostic $read.Data $ExpectedPhase $ProcessId $source.Sidecar
                if ($null -ne $safe.ProcessId) { $diagnostic.nativeProcessId = $safe.ProcessId; $diagnostic.checks = $safe.Checks }
                if ($safe.Failure.category -cne 'Unavailable') { $diagnostic.failure = $safe.Failure }
                if ($null -ne $safe.Stage) { $diagnostic.nativeStage = $safe.Stage }
            }
            catch { $diagnostic[$source.Key] = 'invalid' }
        }
    }
    $line = $diagnostic | ConvertTo-Json -Depth 6 -Compress
    if ([Text.Encoding]::UTF8.GetByteCount($line) -le 4096) { Write-Host $line }
}
function Complete-TestingPluginProbeReport {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$ReportPath, [Parameter(Mandatory)][int]$ProcessId,
        [AllowNull()][object]$ExitCode, [Parameter(Mandatory)][bool]$HasExited,
        [Parameter(Mandatory)][ValidateSet('seed','verify')][string]$ExpectedPhase,
        [Parameter(Mandatory)][string]$ExpectedRoot, [Parameter(Mandatory)][bool]$LeaseAvailable,
        [string]$ExpectedPackageHash = '', [bool]$ObservationFailed = $false)
    $runnerPath = [IO.Path]::ChangeExtension($ReportPath, 'runner.json')
    if (Test-Path -LiteralPath $runnerPath) { throw 'TestingPhaseAlreadyCompleted' }
    $reason = $null; $native = @{}
    $read = Read-TestingProbeReportData $ReportPath
    if ($read.Status -ceq 'available') { $native = $read.Data }
    elseif ($read.Status -ceq 'missing') { $reason = 'TestingReportMissing' }
    else { $reason = 'TestingReportUnreadable' }
    if ($native -isnot [Collections.IDictionary]) { $native = @{}; $reason = 'TestingReportInvalid' }
    if (-not $HasExited) { $reason = 'TestingProcessStillOwned' }
    elseif ($null -eq $ExitCode -or $ExitCode -ne 0) { $reason = 'TestingProcessFailed' }
    elseif (-not $LeaseAvailable) { $reason = 'TestingRootStillOwned' }
    elseif ($null -eq $reason -and ($native['schemaVersion'] -ne 1 -or $native['succeeded'] -ne $true -or
        $native['probe'] -cne $ExpectedPhase -or $native['userRoot'] -cne $ExpectedRoot -or $native['processId'] -ne $ProcessId -or
        $native['actualWindowClosed'] -ne $true -or $native['rootsRetired'] -ne $true -or
        $native['syntheticInterruptionSeed'] -ne $true -or [string]::IsNullOrWhiteSpace($native['osDescription']) -or $native['processArchitecture'] -cne 'X64')) { $reason = 'TestingIdentityOrRetirementMissing' }
    if ($null -eq $reason -and $ExpectedPackageHash -ne '' -and $native['packageSha256'] -cne $ExpectedPackageHash) { $reason = 'TestingPackageHashMismatch' }
    if ($null -eq $reason) {
        $required = @(Get-TestingRequiredChecks $ExpectedPhase)
        if (@($native['checks']).Count -ne $required.Count) { $reason = 'TestingChecksIncomplete' }
        foreach ($check in $required) { if (@($native['checks'] | Where-Object { $_ -ceq $check }).Count -ne 1) { $reason = 'TestingChecksIncomplete' } }
    }
    if ($null -eq $reason) {
        try {
            $root = Join-Path (Split-Path $ReportPath -Parent) "$ExpectedPhase-evidence"
            $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
            if (@($native['evidence']).Count -eq 0) { throw 'TestingEvidenceMissing' }
            foreach ($receipt in $native['evidence']) {
                $path = [string]$receipt.path
                if ($path -notmatch '^(store/[a-zA-Z0-9/.-]+|testing-cases\.csv)$' -or $path.Contains('..') -or -not $seen.Add($path) -or $receipt.sha256 -cnotmatch '^[0-9a-f]{64}$') { throw 'TestingEvidencePathInvalid' }
                $file = Join-Path $root $path
                for ($part = $file; $part.Length -ge $root.Length; $part = Split-Path $part -Parent) {
                    if (-not (Test-Path -LiteralPath $part) -or ((Get-Item -LiteralPath $part).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'TestingEvidencePathInvalid' }
                }
                if (-not (Test-Path -LiteralPath $file -PathType Leaf) -or (Get-Item -LiteralPath $file).Length -ne $receipt.length -or (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant() -cne $receipt.sha256) { throw 'TestingEvidenceHashMismatch' }
            }
            $actual = @(Get-ChildItem -LiteralPath $root -Recurse -File | ForEach-Object { [IO.Path]::GetRelativePath($root, $_.FullName).Replace('\','/') })
            if ($actual.Count -ne $seen.Count -or @($actual | Where-Object { -not $seen.Contains($_) }).Count -ne 0 -or -not $seen.Contains('testing-cases.csv') -or @($actual | Where-Object { $_.EndsWith('/frame.bmp') }).Count -eq 0 -or @($actual | Where-Object { $_ -match '/events/[0-9]+\.json$' }).Count -eq 0) { throw 'TestingEvidenceIncomplete' }
        }
        catch { $reason = 'TestingEvidenceInvalid' }
    }
    if ($null -eq $reason -and $ObservationFailed) { $reason = 'TestingObservationFailed' }
    $result = @{ schemaVersion = 1; succeeded = ($null -eq $reason); phase = $ExpectedPhase; userRoot = $ExpectedRoot;
        processId = $ProcessId; nativeProcessId = $native['processId']; hasExited = $HasExited; exitCode = $ExitCode;
        rootLeaseAvailable = $LeaseAvailable; observationFailed = $ObservationFailed; runnerError = $reason; checks = @($native['checks']).Count; runnerCompletedUtc = [DateTimeOffset]::UtcNow }
    $bytes = [Text.Encoding]::UTF8.GetBytes(($result | ConvertTo-Json -Depth 12))
    $stream = [IO.File]::Open($runnerPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
    try { $stream.Write($bytes) } finally { $stream.Dispose() }
    if ($null -ne $reason) {
        # Console evidence is independent of artifact quota. Diagnostic failure cannot replace the runner's original failure.
        try {
            Write-TestingPluginProbeFailureDiagnostic -ReportPath $ReportPath -ProcessId $ProcessId -ExitCode $ExitCode -HasExited $HasExited `
                -ExpectedPhase $ExpectedPhase -LeaseAvailable $LeaseAvailable -ObservationFailed $ObservationFailed -RunnerError $reason
        } catch { }
        throw $reason
    }
    return $result
}
function Invoke-TestingPluginProbePhases {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$OutputDirectory, [Parameter(Mandatory)][string]$ExpectedRoot,
        [Parameter(Mandatory)][scriptblock]$Observe, [string]$ExpectedPackageHash = '')
    $completed = @()
    foreach ($phase in @('seed','verify')) {
        $report = Join-Path $OutputDirectory "$phase.json"
        if (Test-Path -LiteralPath $report) { throw 'TestingNativeReportAlreadyExists' }
        $observation = @{ ProcessId = $null; ExitCode = $null; HasExited = $false; LeaseAvailable = $false }
        $identity = @{ ReportPath = $report; ExpectedPhase = $phase; ExpectedRoot = $ExpectedRoot; ExpectedPackageHash = $ExpectedPackageHash }
        try { & $Observe $phase $report $observation }
        catch {
            if ($null -ne $observation.ProcessId) {
                try { Complete-TestingPluginProbeReport @identity @observation -ObservationFailed $true | Out-Null } catch { }
            }
            throw
        }
        $result = Complete-TestingPluginProbeReport @identity @observation
        if (@($completed | Where-Object processId -EQ $result.processId).Count -ne 0) { throw 'TestingProbeProcessReused' }
        $completed += $result
    }
    return $completed
}
Export-ModuleMember -Function Complete-TestingPluginProbeReport, Invoke-TestingPluginProbePhases, Get-TestingPluginPackageName
