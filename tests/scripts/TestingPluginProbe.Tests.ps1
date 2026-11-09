$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot '../../scripts/TestingPluginProbe.psm1') -Force
if ((Get-TestingPluginPackageName) -cne 'workbench-testing-plugin-0.2.0.zip') { throw 'Testing probe package identity is stale.' }
$temp = Join-Path ([IO.Path]::GetTempPath()) ('testing-report-regression-' + [Guid]::NewGuid().ToString('N'))
New-Item $temp -ItemType Directory | Out-Null
try {
    # Synthetic diagnostic fixtures only. No native process, provider or user data is exercised.
    # Removing the failure emission, trusting a report string, or continuing verify must fail these tests.
    $privateText = "synthetic-private-token-body-path`n::error::synthetic-workflow`n##vso[task.logissue type=error]synthetic`e[31m"
    function New-SyntheticSidecar {
        return @{ schemaVersion = 1; succeeded = $false; processId = 501; phase = 'seed';
            checks = @('independent-zip-native-page','packaged-requirements-figma-io','traceable-manual-mapped-generation');
            safeError = 'TestingNativeProbeFailed (PluginFailure).'; errorHResult = -2146233079;
            failure = @{ category = 'Assertion'; sourceFile = 'TestingNativeProbe.Authoring.cs'; sourceLine = 43 };
            unknown = $privateText; stdout = $privateText; stderr = $privateText; token = $privateText; body = $privateText;
            evidence = @(@{ path = $privateText; body = $privateText }) }
    }
    function Invoke-SyntheticDiagnosticFailure {
        param([string]$Name, [scriptblock]$WriteFixture, [string]$WantedSidecar = 'available',
            [string]$WantedReport = 'available', [bool]$ObservationThrows = $false, [bool]$LinkedParent = $false)
        $output = Join-Path $temp ('diagnostic-' + $Name)
        New-Item $output -ItemType Directory | Out-Null
        if ($LinkedParent) {
            $alias = $output + '-alias'
            New-Item -ItemType SymbolicLink -Path $alias -Target $output | Out-Null
            $output = $alias
        }
        $launches = [Collections.Generic.List[string]]::new()
        $console = [Collections.Generic.List[string]]::new()
        $before = @{}; $caught = $null
        try {
            Invoke-TestingPluginProbePhases -OutputDirectory $output -ExpectedRoot $temp -Observe {
                param($phase, $report, $observation)
                $launches.Add($phase)
                $observation.ProcessId = 501; $observation.HasExited = $true; $observation.ExitCode = 1; $observation.LeaseAvailable = $true
                @{ succeeded = $false; stage = 'initialize-or-native-smoke'; error = 'HostOperationFailed (PluginFailure).';
                    userRoot = $privateText; nativeMessage = $privateText; unknown = $privateText } | ConvertTo-Json | Set-Content -LiteralPath $report
                & $WriteFixture $report
                foreach ($file in @($report, ($report + '.evidence.json'))) {
                    if (Test-Path -LiteralPath $file -PathType Leaf) { $before[$file] = (Get-FileHash -LiteralPath $file).Hash }
                }
                if ($ObservationThrows) { throw 'Synthetic original observation failure' }
            } 6>&1 | ForEach-Object {
                if ($_ -isnot [Management.Automation.InformationRecord]) { throw 'Unexpected success output on failure.' }
                $console.Add([string]$_.MessageData)
            }
        }
        catch { $caught = $_ }
        $wantedError = if ($ObservationThrows) { 'Synthetic original observation failure' } else { 'TestingProcessFailed' }
        if ($null -eq $caught -or $caught.Exception.Message -cne $wantedError) { throw "Original failure was replaced: $Name" }
        if ($launches.Count -ne 1 -or (Test-Path (Join-Path $output 'verify.json')) -or (Test-Path (Join-Path $output 'verify.runner.json'))) { throw "Diagnostic failure launched verify: $Name" }
        if ($console.Count -ne 1) { throw "Expected one bounded diagnostic console line: $Name; actual=$($console.Count)" }
        $line = $console[0]
        if ([Text.Encoding]::UTF8.GetByteCount($line) -gt 4096 -or $line.Contains("`n") -or $line.Contains("`r") -or $line.Contains("`e") -or
            $line.Contains('synthetic-private') -or $line.Contains('::error::') -or $line.Contains('##vso') -or $line.Contains($temp)) { throw "Unsafe diagnostic console contents: $Name" }
        $diagnostic = $line | ConvertFrom-Json -AsHashtable
        if ($diagnostic.kind -cne 'TestingPluginProbeFailure' -or $diagnostic.phase -cne 'seed' -or $diagnostic.processId -ne 501 -or
            $diagnostic.hasExited -ne $true -or $diagnostic.exitCode -ne 1 -or $diagnostic.rootLeaseAvailable -ne $true -or
            $diagnostic.observationFailed -ne $ObservationThrows -or $diagnostic.runnerError -cne 'TestingProcessFailed' -or
            $diagnostic.nativeSidecar -cne $WantedSidecar -or $diagnostic.nativeReport -cne $WantedReport) { throw "Diagnostic lost actual observations or safe report status: $Name" }
        foreach ($file in $before.Keys) {
            if ((Get-FileHash -LiteralPath $file).Hash -cne $before[$file]) { throw "Diagnostic mutated native bytes: $Name" }
        }
        $runnerPath = Join-Path $output 'seed.runner.json'
        $runner = Get-Content -LiteralPath $runnerPath -Raw | ConvertFrom-Json
        if ($runner.succeeded -or $runner.runnerError -cne 'TestingProcessFailed') { throw "Diagnostic replaced runner failure evidence: $Name" }
        $firstHash = (Get-FileHash -LiteralPath $runnerPath).Hash
        try { Complete-TestingPluginProbeReport -ReportPath (Join-Path $output 'seed.json') -ProcessId 501 -HasExited $true -ExitCode 0 -ExpectedPhase seed -ExpectedRoot $temp -LeaseAvailable $true | Out-Null } catch { }
        if ((Get-FileHash -LiteralPath $runnerPath).Hash -cne $firstHash) { throw "Diagnostic overwrote first runner evidence: $Name" }
        return $diagnostic
    }
    # Synthetic retry checkpoint/code projection only; these are not observations from PID 7436.
    foreach ($operation in @('VerifySelectInterruptedRun','VerifyAwaitRetryReady','VerifyInvokeRetry','VerifyDiscoverRetry',
        'VerifyAwaitRetryCompletion','VerifyValidateFrozenRetry','VerifyRoster')) {
        $projected = Invoke-SyntheticDiagnosticFailure ('retry-operation-' + $operation) {
            param($report)
            $fixture = New-SyntheticSidecar
            $fixture.failure = @{ category = 'Unexpected'; operation = $operation }
            $fixture | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath ($report + '.evidence.json')
        }
        if ($projected.failure['operation'] -cne $operation -or $projected.failure.category -cne 'Unexpected' -or
            $null -ne $projected.failure['testingErrorCode'] -or $null -ne $projected.failure.sourceFile) { throw "Safe synthetic retry checkpoint was lost: $operation" }
    }
    foreach ($testingCode in @('TestingContextClosed','InvalidTestSource','InvalidCaseSet','InvalidRun','InvalidEvidence','RevisionConflict',
        'StoreLimitExceeded','StoreIoFailure','UnsafePath','TestStoreCorrupt','RecoveryRequired','ManualStepUnsupported','TargetUnavailable',
        'AssertionFailed','AdapterFailed','TestRecordNotFound','StoreBusy','InvalidId','InvalidProducer','TestingFailed')) {
        $projected = Invoke-SyntheticDiagnosticFailure ('testing-code-' + $testingCode) {
            param($report)
            $fixture = New-SyntheticSidecar
            $fixture.failure = @{ category = 'Unexpected'; operation = 'VerifyAwaitRetryCompletion'; testingErrorCode = $testingCode }
            $fixture | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath ($report + '.evidence.json')
        }
        if ($projected.failure['operation'] -cne 'VerifyAwaitRetryCompletion' -or $projected.failure['testingErrorCode'] -cne $testingCode -or
            $projected.failure.category -cne 'Unexpected' -or $null -ne $projected.failure.sourceFile) { throw "Safe synthetic testing code was lost: $testingCode" }
    }
    $optionalNull = Invoke-SyntheticDiagnosticFailure 'optional-retry-fields-null' {
        param($report)
        $fixture = New-SyntheticSidecar
        $fixture.failure.operation = $null; $fixture.failure.testingErrorCode = $null
        $fixture | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath ($report + '.evidence.json')
    }
    if ($null -ne $optionalNull.failure['operation'] -or $null -ne $optionalNull.failure['testingErrorCode'] -or
        $optionalNull.failure.category -cne 'Assertion') { throw 'Null optional fields changed legacy assertion semantics.' }
    foreach ($field in @('operation','testingErrorCode')) {
        $badValues = @('Unknown','verifyinvokeretry','storebusy','StoreBusy ','ProjectOperationFailed','Canceled',
            '/synthetic/private/path','synthetic private body',"`e[31m", "`n::error::synthetic", '##vso[task.logissue type=error]synthetic',
            $privateText, 1, 1.5, $true, @('StoreBusy'), @{ code = 'StoreBusy' })
        for ($badIndex = 0; $badIndex -lt $badValues.Count; $badIndex++) {
            $rejected = Invoke-SyntheticDiagnosticFailure ("retry-invalid-$field-$badIndex") {
                param($report)
                $fixture = New-SyntheticSidecar
                $fixture.failure = @{ category = 'Unexpected'; operation = 'VerifyInvokeRetry'; testingErrorCode = 'StoreBusy' }
                $fixture.failure[$field] = $badValues[$badIndex]
                $fixture | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath ($report + '.evidence.json')
            } -WantedSidecar invalid
            if ($rejected.checks.Count -ne 0 -or $null -ne $rejected.nativeProcessId -or
                $null -ne $rejected.failure['operation'] -or $null -ne $rejected.failure['testingErrorCode']) { throw "Rejected synthetic retry detail leaked: $field/$badIndex" }
        }
    }
    $safe = Invoke-SyntheticDiagnosticFailure 'safe-prefix' {
        param($report)
        New-SyntheticSidecar | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath ($report + '.evidence.json')
    }
    if ($safe.nativeProcessId -ne 501 -or ($safe.checks -join ',') -cne 'independent-zip-native-page,packaged-requirements-figma-io,traceable-manual-mapped-generation' -or
        $safe.failure.category -cne 'Assertion' -or $safe.failure.sourceFile -cne 'TestingNativeProbe.Authoring.cs' -or $safe.failure.sourceLine -ne 43) { throw 'Safe cumulative native checks/assertion location were lost.' }
    Write-Host ('Synthetic console diagnostic (not native evidence): ' + ($safe | ConvertTo-Json -Compress -Depth 6))
    $bounded = Invoke-SyntheticDiagnosticFailure 'exact-byte-bound' {
        param($report)
        $fixture = New-SyntheticSidecar; $fixture.unknown = ''
        $size = [Text.Encoding]::UTF8.GetByteCount(($fixture | ConvertTo-Json -Compress -Depth 10))
        $fixture.unknown = 'x' * (65536 - $size)
        $bytes = [Text.Encoding]::UTF8.GetBytes(($fixture | ConvertTo-Json -Compress -Depth 10))
        if ($bytes.Length -ne 65536) { throw 'Synthetic byte-bound fixture is incorrect.' }
        [IO.File]::WriteAllBytes(($report + '.evidence.json'), $bytes)
    }
    if ($bounded.failure.category -cne 'Assertion' -or $bounded.checks.Count -ne 3) { throw 'Exactly bounded native detail was discarded or its unknown padding leaked.' }
    $noLocation = Invoke-SyntheticDiagnosticFailure 'assertion-no-location' {
        param($report)
        $fixture = New-SyntheticSidecar; $fixture.failure.sourceFile = $null; $fixture.failure.sourceLine = $null
        $fixture | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath ($report + '.evidence.json')
    }
    if ($noLocation.failure.category -cne 'Assertion' -or $null -ne $noLocation.failure.sourceFile -or $null -ne $noLocation.failure.sourceLine) { throw 'Unavailable first-party location was fabricated.' }
    $legacy = Invoke-SyntheticDiagnosticFailure 'legacy-safe-category' {
        param($report)
        $fixture = New-SyntheticSidecar; $fixture.Remove('failure'); $fixture.safeError = 'TestingNativeProbeFailed (Storage).'
        $fixture | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath ($report + '.evidence.json')
    }
    if ($legacy.failure.category -cne 'Storage' -or $null -ne $legacy.failure.sourceFile) { throw 'Legacy native error category did not remain safe and useful.' }
    $missing = Invoke-SyntheticDiagnosticFailure 'missing' { } -WantedSidecar missing
    if ($missing.checks.Count -ne 0 -or $null -ne $missing.nativeProcessId -or $missing.failure.category -cne 'Unexpected') { throw 'Missing sidecar fabricated native detail.' }
    $linkedParent = Invoke-SyntheticDiagnosticFailure 'parent-link' {
        param($report)
        New-SyntheticSidecar | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath ($report + '.evidence.json')
    } -WantedSidecar unsafe-path -WantedReport unsafe-path -LinkedParent $true
    if ($linkedParent.checks.Count -ne 0 -or $linkedParent.failure.category -cne 'Unavailable') { throw 'Symlinked parent was followed for native diagnostics.' }
    foreach ($bad in @('malformed','invalid-utf8','oversized','file-link','deep-json','duplicate-field','wrong-pid','wrong-phase','string-schema','string-pid','fractional-pid',
        'string-success','true-success','unknown-check','string-checks','duplicate-checks','reordered-checks','nonprefix-checks','unknown-category','unsafe-source','string-line','negative-line','unknown-safe-error')) {
        $status = switch ($bad) { 'oversized' {'oversized'} 'file-link' {'unsafe-path'} default {'invalid'} }
        $rejected = Invoke-SyntheticDiagnosticFailure $bad {
            param($report)
            $sidecar = $report + '.evidence.json'; $fixture = New-SyntheticSidecar
            switch ($bad) {
                'malformed' { [IO.File]::WriteAllText($sidecar, '{"safeError":"' + $privateText); return }
                'oversized' { [IO.File]::WriteAllText($sidecar, '{"body":"' + ('x' * 65536) + '"}'); return }
                'file-link' {
                    $target = Join-Path $temp 'link-target.json'; $fixture | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $target
                    New-Item -ItemType SymbolicLink -Path $sidecar -Target $target | Out-Null; return
                }
                'invalid-utf8' {
                    $fixture.unknown = 'synthetic-byte-marker'; $json = $fixture | ConvertTo-Json -Compress -Depth 10
                    $bytes = [Text.Encoding]::UTF8.GetBytes($json); $bytes[$json.IndexOf('synthetic-byte-marker')] = 255
                    [IO.File]::WriteAllBytes($sidecar, $bytes); return
                }
                'deep-json' {
                    $json = $fixture | ConvertTo-Json -Compress -Depth 10
                    [IO.File]::WriteAllText($sidecar, $json.Substring(0, $json.Length - 1) + ',"deep":' + ('[' * 20) + '1' + (']' * 20) + '}'); return
                }
                'duplicate-field' {
                    $json = $fixture | ConvertTo-Json -Compress -Depth 10
                    [IO.File]::WriteAllText($sidecar, $json.Substring(0, $json.Length - 1) + ',"schemaVersion":1}'); return
                }
                'wrong-pid' { $fixture.processId = 502 }
                'wrong-phase' { $fixture.phase = 'verify' }
                'string-schema' { $fixture.schemaVersion = '1' }
                'string-pid' { $fixture.processId = '501' }
                'fractional-pid' { $fixture.processId = 501.5 }
                'string-success' { $fixture.succeeded = 'false' }
                'true-success' { $fixture.succeeded = $true }
                'unknown-check' { $fixture.checks = @($privateText) }
                'string-checks' { $fixture.checks = 'independent-zip-native-page' }
                'duplicate-checks' { $fixture.checks = @('independent-zip-native-page','independent-zip-native-page') }
                'reordered-checks' { $fixture.checks = @('packaged-requirements-figma-io','independent-zip-native-page') }
                'nonprefix-checks' { $fixture.checks = @('traceable-manual-mapped-generation') }
                'unknown-category' { $fixture.failure.category = $privateText }
                'unsafe-source' { $fixture.failure.sourceFile = $privateText }
                'string-line' { $fixture.failure.sourceLine = '43' }
                'negative-line' { $fixture.failure.sourceLine = -1 }
                'unknown-safe-error' { $fixture.Remove('failure'); $fixture.safeError = $privateText }
            }
            $fixture | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $sidecar
        } -WantedSidecar $status
        if ($rejected.checks.Count -ne 0 -or $null -ne $rejected.nativeProcessId -or $null -ne $rejected.failure.sourceFile) { throw "Rejected report leaked native data: $bad" }
    }
    foreach ($badReport in @('missing','malformed','oversized','file-link','hostile-error')) {
        $status = switch ($badReport) { 'missing' {'missing'} 'oversized' {'oversized'} 'file-link' {'unsafe-path'} default {'invalid'} }
        $rejected = Invoke-SyntheticDiagnosticFailure ('main-' + $badReport) {
            param($report)
            switch ($badReport) {
                'missing' { Remove-Item -LiteralPath $report }
                'malformed' { [IO.File]::WriteAllText($report, '{"error":') }
                'oversized' { [IO.File]::WriteAllText($report, '{"body":"' + ('x' * 65536) + '"}') }
                'file-link' {
                    $target = Join-Path $temp 'main-link-target.json'; Move-Item -LiteralPath $report -Destination $target
                    New-Item -ItemType SymbolicLink -Path $report -Target $target | Out-Null
                }
                'hostile-error' { @{succeeded=$false;stage='initialize-or-native-smoke';error=$privateText} | ConvertTo-Json | Set-Content -LiteralPath $report }
            }
        } -WantedSidecar missing -WantedReport $status
        if ($rejected.failure.category -cne 'Unavailable') { throw "Rejected main report fabricated a failure category: $badReport" }
    }
    $original = Invoke-SyntheticDiagnosticFailure 'observation-throw' {
        param($report)
        New-SyntheticSidecar | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath ($report + '.evidence.json')
    } -ObservationThrows $true
    # A broken diagnostic sink must not replace the fixed runner throw or the original observation throw.
    try {
        & (Get-Module TestingPluginProbe) { function script:Write-TestingPluginProbeFailureDiagnostic { throw 'Synthetic diagnostic sink failure' } }
        foreach ($observeThrows in @($false,$true)) {
            $output = Join-Path $temp ('diagnostic-sink-' + $observeThrows); New-Item $output -ItemType Directory | Out-Null
            $caught = $null
            try {
                Invoke-TestingPluginProbePhases -OutputDirectory $output -ExpectedRoot $temp -Observe {
                    param($phase,$report,$observation)
                    $observation.ProcessId = 501; $observation.HasExited = $true; $observation.ExitCode = 1; $observation.LeaseAvailable = $true
                    if ($observeThrows) { throw 'Synthetic original observation failure' }
                } | Out-Null
            } catch { $caught = $_ }
            $wantedError = if ($observeThrows) { 'Synthetic original observation failure' } else { 'TestingProcessFailed' }
            if ($null -eq $caught -or $caught.Exception.Message -cne $wantedError -or (Test-Path (Join-Path $output 'verify.runner.json')) -or
                (Get-Content (Join-Path $output 'seed.runner.json') -Raw | ConvertFrom-Json).runnerError -cne 'TestingProcessFailed') { throw 'Diagnostic sink failure replaced original failure or runner evidence.' }
        }
    } finally { Import-Module (Join-Path $PSScriptRoot '../../scripts/TestingPluginProbe.psm1') -Force }
    Write-Host 'Testing diagnostic regressions passed: safe cumulative assertions, legacy category, rejected hostile/missing/malformed/oversized/symlink reports, first evidence and original exceptions. Synthetic fixtures only.'
    # Synthetic runner metadata tests the actual orchestration; these are NOT native EXE evidence.
    foreach ($case in @('missing','wrong-pid','still-live','lease-held','incomplete','collection')) {
        $output = Join-Path $temp $case; New-Item $output -ItemType Directory | Out-Null
        $launches = [Collections.Generic.List[string]]::new()
        $original = $null
        $failed = $false
        try {
            Invoke-TestingPluginProbePhases -OutputDirectory $output -ExpectedRoot $temp -Observe {
                param($phase, $report, $observation)
                $launches.Add($phase)
                $observation.ProcessId = 202; $observation.HasExited = $true; $observation.ExitCode = 0; $observation.LeaseAvailable = $true
                if ($case -eq 'still-live') { $observation.HasExited = $false }
                if ($case -eq 'lease-held') { $observation.LeaseAvailable = $false }
                if ($case -ne 'missing') {
                    @{ schemaVersion = 1; succeeded = $true; probe = $phase; userRoot = $temp; processId = $(if ($case -eq 'wrong-pid') {101} else {202});
                       actualWindowClosed = $true; rootsRetired = $true; checks = @('independent-zip-native-page'); osDescription = 'Synthetic'; processArchitecture = 'X64'; syntheticInterruptionSeed = $true } | ConvertTo-Json -Depth 10 | Set-Content $report
                }
                if ($case -eq 'collection') { throw 'Synthetic collector failure' }
            } | Out-Null
        }
        catch { $failed = $true }
        $runner = Get-Content (Join-Path $output 'seed.runner.json') -Raw | ConvertFrom-Json
        if (-not $failed -or $runner.succeeded -or $runner.processId -ne 202 -or $launches.Count -ne 1 -or (Test-Path (Join-Path $output 'verify.runner.json'))) { throw "Failed phase launched successor or lost observation: $case" }
        if ($case -eq 'wrong-pid') {
            $native = Get-Content (Join-Path $output 'seed.json') -Raw | ConvertFrom-Json
            if ($native.processId -ne 101 -or -not $native.succeeded) { throw 'Native report was overwritten by the runner.' }
        }
        $firstBytes = [IO.File]::ReadAllBytes((Join-Path $output 'seed.runner.json'))
        try { Complete-TestingPluginProbeReport -ReportPath (Join-Path $output 'seed.json') -ProcessId 202 -HasExited $true -ExitCode 0 -ExpectedPhase seed -ExpectedRoot $temp -LeaseAvailable $true } catch { }
        if (-not [Linq.Enumerable]::SequenceEqual([byte[]]$firstBytes, [byte[]][IO.File]::ReadAllBytes((Join-Path $output 'seed.runner.json')))) { throw 'A phase was completed twice and replaced its first failure.' }
    }
    $seedChecks = @('independent-zip-native-page','packaged-requirements-figma-io','traceable-manual-mapped-generation',
        'immediate-metadata-csv-bytes','immediate-step-mapping-invalidation','actual-file-share-save-failure-close-correction',
        'conflict-preserve-copy-reload-bytes','positive-actual-journal-bmp-native-decoder','negative-empty-title-run',
        'labeled-deliberate-assertion-failure','cancel-real-owned-store-io-before-first-step','native-project-isolation-empty-read',
        'synthetic-inflight-journal-seed-no-action','immediate-final-input-before-leave')
    $verifyChecks = @('separate-process-context-interrupted-synthetic-seed','restart-final-native-input-persisted',
        'reopened-exact-observed-text-bmp-native-decoder','linked-new-session-retry-frozen-step-one','restart-project-roster-preserved')
    foreach ($mode in @('accepted','corrupt-receipt','duplicate-check','same-pid')) {
        $output = Join-Path $temp $mode; New-Item $output -ItemType Directory | Out-Null
        $launches = [Collections.Generic.List[string]]::new(); $failed = $false
        try {
            $results = @(Invoke-TestingPluginProbePhases -OutputDirectory $output -ExpectedRoot $temp -Observe {
                param($phase,$report,$observation)
                $launches.Add($phase)
                $observation.ProcessId = if ($phase -eq 'seed' -or $mode -eq 'same-pid') {303} else {404}
                $observation.HasExited = $true; $observation.ExitCode = 0; $observation.LeaseAvailable = $true
                $receipts = @()
                foreach ($file in @('testing-cases.csv','store/runs/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/events/0000000000.json','store/runs/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/evidence/bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb/frame.bmp')) {
                    $path = Join-Path (Join-Path $output "$phase-evidence") $file
                    New-Item (Split-Path $path -Parent) -ItemType Directory -Force | Out-Null
                    [IO.File]::WriteAllBytes($path, [byte[]]@(1,2,3,4))
                    $receipts += @{path=$file;length=4;sha256=(Get-FileHash $path -Algorithm SHA256).Hash.ToLowerInvariant()}
                }
                if ($mode -eq 'corrupt-receipt') { $receipts[0].sha256 = '0' * 64 }
                $checks = if ($phase -eq 'seed') {$seedChecks} else {$verifyChecks}
                if ($mode -eq 'duplicate-check') { $checks = @($checks) + @($checks[0]) }
                @{schemaVersion=1;succeeded=$true;probe=$phase;userRoot=$temp;processId=$observation.ProcessId;
                  actualWindowClosed=$true;rootsRetired=$true;checks=$checks;evidence=$receipts;
                  osDescription='Synthetic runner metadata, no native execution';processArchitecture='X64';syntheticInterruptionSeed=$true} | ConvertTo-Json -Depth 15 | Set-Content $report
            })
        }
        catch { $failed = $true }
        if ($mode -eq 'accepted') {
            if ($failed -or $results.Count -ne 2 -or $results[0].checks -ne 14 -or $results[1].checks -ne 5 -or $launches.Count -ne 2) { throw 'Complete evidence did not admit the two distinct phases.' }
        }
        elseif (-not $failed -or ($mode -ne 'same-pid' -and $launches.Count -ne 1)) { throw "Invalid evidence/PID admitted a successor: $mode" }
    }
    Write-Host 'Testing probe orchestration regressions passed: 6 initial failure modes, 2-phase positive metadata/hashed files, corrupt receipt, duplicate check and repeated PID. Synthetic script fixtures only.'

}
finally { Remove-Item $temp -Recurse -Force }
