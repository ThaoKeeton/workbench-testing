[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot '../../scripts/OwnedLabProbeOutput.psm1') -Force
$temp = Join-Path ([IO.Path]::GetTempPath()) ('owned probe-output-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp | Out-Null
$child = Join-Path $temp 'owned-child.ps1'
@'
param([string]$Mode, [int]$Code)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$utf8 = [Text.UTF8Encoding]::new($false)
$stdout = "synthetic completed check`n"
$stderr = "System.InvalidOperationException: Synthetic owned probe failure`n   at OwnedSyntheticProbe.Run()`n"
switch ($Mode) {
    'CrLf' { $stdout = "synthetic completed check`r`n" }
    'AlteredStdout' { $stdout = "synthetic completed altered`n" }
    'CaseChangedStdout' { $stdout = "Synthetic completed check`n" }
    'Large' { $stderr = "SYNTHETIC-BEGIN`n" + ('x' * 2000000) + 'SYNTHETIC-END' }
    'Exact' { $stderr = 'x' * 16384 }
    'Empty' { $stderr = '' }
    'ExactStdout' { $stdout = 'x' * 65536 }
    'OversizeStdout' { $stdout = 'x' * 65537 }
}
$bytes = $utf8.GetBytes($stdout)
$stream = [Console]::OpenStandardOutput(); $stream.Write($bytes, 0, $bytes.Length); $stream.Flush()
$bytes = $utf8.GetBytes($stderr)
if ($Mode -ceq 'Invalid') { $bytes = [byte[]]@(83,255,0,27,10) }
$stream = [Console]::OpenStandardError(); $stream.Write($bytes, 0, $bytes.Length); $stream.Flush()
if ($Mode -ceq 'Wait') { [Threading.Thread]::Sleep(30000) }
exit $Code
'@ | Set-Content -LiteralPath $child -Encoding utf8NoBOM

function Require([bool]$Value, [string]$Message) { if (-not $Value) { throw $Message } }
function Exercise([string]$Mode, [int]$Code = 37, [string]$Missing = '') {
    $directory = Join-Path $temp ([Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $directory | Out-Null
    $stdout = Join-Path $directory 'stdout.log'; $stderr = Join-Path $directory 'stderr.log'
    # Raw pipe copies preserve literal LF/CRLF and invalid bytes at the reporter's file boundary.
    $start = [Diagnostics.ProcessStartInfo]::new([Environment]::ProcessPath)
    $start.UseShellExecute = $false; $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    foreach ($argument in @('-NoProfile','-File',$child,$Mode,[string]$Code)) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    $stdoutFile = $null; $stderrFile = $null; $copies = @()
    try {
        $stdoutFile = [IO.File]::Create($stdout); $stderrFile = [IO.File]::Create($stderr)
        $copies += $process.StandardOutput.BaseStream.CopyToAsync($stdoutFile)
        $copies += $process.StandardError.BaseStream.CopyToAsync($stderrFile)
        $null = $process.Handle
        $birth = $process.StartTime.ToUniversalTime().ToFileTimeUtc()
        if (-not $process.WaitForExit(30000)) { $process.Kill($true); $process.WaitForExit(); throw 'Owned synthetic child timed out.' }
        [Threading.Tasks.Task]::WhenAll([Threading.Tasks.Task[]]$copies).GetAwaiter().GetResult()
        $stdoutFile.Dispose(); $stderrFile.Dispose()
        $stdoutBytes = [IO.File]::ReadAllBytes($stdout)
        $identity = [ordered]@{ processId = $process.Id; processCreationFileTimeUtc = $birth; processExitFileTimeUtc = $process.ExitTime.ToUniversalTime().ToFileTimeUtc(); exitCode = $process.ExitCode }
        $before = $identity | ConvertTo-Json -Compress
        if ($Missing -ceq 'stdout') { Move-Item -LiteralPath $stdout -Destination ($stdout + '.retained') }
        if ($Missing -ceq 'stderr') { Move-Item -LiteralPath $stderr -Destination ($stderr + '.retained') }
        $state = @{ succeeded = $false; failure = $null }
        $emitted = @(& {
            try {
                Complete-OwnedLabProbeProcessOutput -Process $process -StandardOutputPath $stdout -StandardErrorPath $stderr
                $state.succeeded = $true
            }
            catch { $state.failure = $_.Exception.Message }
        } 6>&1 | ForEach-Object { $_.ToString() })
        Require ($process.HasExited -and $identity.processId -gt 0 -and $birth -gt 0 -and $identity.processExitFileTimeUtc -ge $birth) 'Actual child identity/confirmed exit missing.'
        Require (($identity | ConvertTo-Json -Compress) -ceq $before -and $identity.exitCode -eq $Code -and $process.ExitCode -eq $Code) 'Reporting changed the observed process identity or exit code.'
        Write-Host ([ordered]@{ syntheticChild = $true; mode = $Mode; missingLog = $Missing; process = $identity; reportingSucceeded = $state.succeeded; failure = $state.failure; emittedUtf8Bytes = [Text.Encoding]::UTF8.GetByteCount(($emitted -join "`n")) } | ConvertTo-Json -Depth 4 -Compress)
        return @{ emitted = $emitted; state = $state; identity = $identity; stdoutBytes = $stdoutBytes }
    }
    finally {
        if (-not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }
        if ($copies.Count -ne 0) { [Threading.Tasks.Task]::WhenAll([Threading.Tasks.Task[]]$copies).GetAwaiter().GetResult() }
        if ($null -ne $stdoutFile) { $stdoutFile.Dispose() }
        if ($null -ne $stderrFile) { $stderrFile.Dispose() }
        $process.Dispose()
    }
}
function FailureDiagnostic($Result) {
    Require (-not $Result.state.succeeded -and $Result.state.failure -ceq 'OwnedLabProbeProcessFailed') 'Reporting must preserve the original nonzero failure.'
    $records = @($Result.emitted | Where-Object { $_.StartsWith('{') } | ForEach-Object { $_ | ConvertFrom-Json -AsHashtable } | Where-Object diagnostic -CEQ 'OwnedLabProbeStandardError')
    Require ($records.Count -eq 1) 'Nonzero child stderr diagnostic was not emitted exactly once before the process failure.'
    return $records[0]
}
function RequireExpectedStdout($Result, [string]$Expected) {
    Require ($Result.emitted -ccontains $Expected) 'Completed stdout observations were lost.'
}
try {
    # A completion reporter must refuse a still-running owned child before any exit wait.
    $running = Start-Process -FilePath ([Environment]::ProcessPath) -ArgumentList @('-NoProfile','-File',('"' + $child + '"'),'Wait',37) -PassThru -RedirectStandardOutput (Join-Path $temp 'running.stdout.log') -RedirectStandardError (Join-Path $temp 'running.stderr.log')
    try {
        $null = $running.Handle
        $birth = $running.StartTime.ToUniversalTime().ToFileTimeUtc()
        $failure = $null
        try { Complete-OwnedLabProbeProcessOutput -Process $running -StandardOutputPath (Join-Path $temp 'running.stdout.log') -StandardErrorPath (Join-Path $temp 'running.stderr.log') }
        catch { $failure = $_.Exception.Message }
        Require ($failure -ceq 'OwnedLabProbeProcessStillRunning' -and -not $running.HasExited) 'Completion reporting waited on an unobserved running child or accepted it.'
    }
    finally {
        if (-not $running.HasExited) { $running.Kill($true) }
        $running.WaitForExit()
        Write-Host ([ordered]@{ syntheticChild = $true; mode = 'StillRunningRefused'; processId = $running.Id; processCreationFileTimeUtc = $birth; processExitFileTimeUtc = $running.ExitTime.ToUniversalTime().ToFileTimeUtc(); exitCode = $running.ExitCode; reporterFailure = $failure; cleanupJoined = $true } | ConvertTo-Json -Compress)
        $running.Dispose()
    }

    # Catches the real wrapper regression: stderr is redirected, then lost behind the nonzero throw.
    $small = Exercise Small
    $diagnostic = FailureDiagnostic $small
    Require ([Convert]::ToHexString($small.stdoutBytes) -ceq '73796E74686574696320636F6D706C6574656420636865636B0A') 'LF child stdout bytes changed before reporting.'
    RequireExpectedStdout $small "synthetic completed check`n"
    Require ($diagnostic.available -and -not $diagnostic.truncated -and -not $diagnostic.invalidUtf8 -and $diagnostic.text -ceq "System.InvalidOperationException: Synthetic owned probe failure`n   at OwnedSyntheticProbe.Run()`n") 'Actual ordinary exception type/message/stack missing.'
    $crlf = Exercise CrLf
    $null = FailureDiagnostic $crlf
    Require ([Convert]::ToHexString($crlf.stdoutBytes) -ceq '73796E74686574696320636F6D706C6574656420636865636B0D0A') 'CRLF child stdout bytes changed before reporting.'
    RequireExpectedStdout $crlf "synthetic completed check`r`n"
    Write-Host ($small.emitted -join "`n")
    Write-Host 'Nonzero real owned child: stdout, stderr exception, original failure and actual identity preserved.'

    # Catches an unbounded/full-file diagnostic read or an off-by-one truncation claim.
    $large = Exercise Large; $diagnostic = FailureDiagnostic $large
    Require ($diagnostic.available -and $diagnostic.truncated -and $diagnostic.bytesRead -eq 16384 -and $diagnostic.byteLimit -eq 16384 -and $diagnostic.text.StartsWith("SYNTHETIC-BEGIN`n") -and -not $diagnostic.text.Contains('SYNTHETIC-END')) 'Oversized stderr was not bounded to its leading 16 KiB.'
    Require ([Text.Encoding]::UTF8.GetByteCount(($large.emitted -join "`n")) -lt 100000) 'Bounded diagnostic output expanded without a finite limit.'
    $diagnostic = FailureDiagnostic (Exercise Exact)
    Require ($diagnostic.available -and -not $diagnostic.truncated -and $diagnostic.bytesRead -eq 16384 -and $diagnostic.text.Length -eq 16384) 'Exact-limit stderr was incorrectly truncated.'

    # Catches decoding/control characters masking the nonzero failure or escaping JSON framing.
    $invalid = Exercise Invalid; $diagnostic = FailureDiagnostic $invalid
    Require ($diagnostic.available -and $diagnostic.invalidUtf8 -and $diagnostic.bytesRead -eq 5 -and $diagnostic.text -ceq ("S" + [char]0xfffd + [char]0 + [char]27 + "`n")) 'Invalid UTF-8 stderr was not safely represented as diagnostic text.'
    Require (-not ($invalid.emitted -join '').Contains([char]0) -and -not ($invalid.emitted -join '').Contains([char]27)) 'Diagnostic console output contains raw control characters.'
    $diagnostic = FailureDiagnostic (Exercise Empty)
    Require ($diagnostic.available -and $diagnostic.bytesRead -eq 0 -and -not $diagnostic.truncated -and $diagnostic.text -ceq '') 'Empty stderr changed the original failure.'

    # Catches diagnostic IO/oversize errors replacing the original process-failed error.
    $diagnostic = FailureDiagnostic (Exercise Small 37 stderr)
    Require (-not $diagnostic.available) 'Unreadable stderr was reported as available.'
    $missingStdout = Exercise Small 37 stdout
    $null = FailureDiagnostic $missingStdout
    $consoleUnavailable = @($missingStdout.emitted | Where-Object { $_.StartsWith('{') } | ForEach-Object { $_ | ConvertFrom-Json -AsHashtable } | Where-Object diagnostic -CEQ 'OwnedLabProbeConsoleUnavailable')
    Require ($consoleUnavailable.Count -eq 1 -and $consoleUnavailable[0].stream -ceq 'stdout') 'Missing stdout lost its framed unavailable diagnostic.'
    $stdoutFailure = $null
    try { RequireExpectedStdout $missingStdout "synthetic completed check`n" }
    catch { $stdoutFailure = $_.Exception.Message }
    Require ($stdoutFailure -ceq 'Completed stdout observations were lost.') 'The exact stdout assertion accepted a missing log.'
    $alteredStdout = Exercise AlteredStdout
    $null = FailureDiagnostic $alteredStdout
    $stdoutFailure = $null
    try { RequireExpectedStdout $alteredStdout "synthetic completed check`n" }
    catch { $stdoutFailure = $_.Exception.Message }
    Require ($stdoutFailure -ceq 'Completed stdout observations were lost.') 'The exact stdout assertion accepted modified output.'
    $caseChangedStdout = Exercise CaseChangedStdout
    $null = FailureDiagnostic $caseChangedStdout
    $stdoutFailure = $null
    try { RequireExpectedStdout $caseChangedStdout "synthetic completed check`n" }
    catch { $stdoutFailure = $_.Exception.Message }
    Require ($stdoutFailure -ceq 'Completed stdout observations were lost.') 'The exact stdout assertion accepted changed letter casing.'
    $null = FailureDiagnostic (Exercise OversizeStdout)
    $oversizeSuccess = Exercise OversizeStdout 0
    Require (-not $oversizeSuccess.state.succeeded -and $oversizeSuccess.state.failure -ceq 'OwnedLabProbeConsoleOversize') 'A zero-exit child bypassed the existing stdout limit.'
    $exactStdout = Exercise ExactStdout 0
    Require ($exactStdout.state.succeeded -and $null -eq $exactStdout.state.failure -and $exactStdout.stdoutBytes.Length -eq 65536 -and $exactStdout.emitted.Count -eq 1 -and $exactStdout.emitted[0] -ceq ('x' * 65536)) 'An exact-limit zero-exit stdout was changed or rejected.'
    $success = Exercise Small 0
    Require ($success.state.succeeded -and $null -eq $success.state.failure -and $success.emitted.Count -eq 1) 'Successful reporting emitted failure diagnostics or lost stdout.'
    RequireExpectedStdout $success "synthetic completed check`n"
    Write-Host 'Owned child reporting regressions passed: 15 real processes, running-child refusal, joined redirected logs, exact LF/CRLF stdout, bounded/truncated/exact/invalid/empty/unreadable text, stdout guards and original failure/identity. Synthetic script proof only; no Windows native execution.'
}
finally { Remove-Item -LiteralPath $temp -Recurse -Force }
