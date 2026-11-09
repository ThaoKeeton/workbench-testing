Set-StrictMode -Version Latest

function Read-OwnedLabProbeText {
    param([string]$Path, [int]$ByteLimit)
    $stream = [IO.File]::OpenRead($Path)
    try {
        # One extra byte detects truncation without loading an unbounded log.
        $buffer = [byte[]]::new($ByteLimit + 1)
        $read = 0
        while ($read -lt $buffer.Length) {
            $count = $stream.Read($buffer, $read, $buffer.Length - $read)
            if ($count -eq 0) { break }
            $read += $count
        }
    }
    finally { $stream.Dispose() }
    $length = [Math]::Min($read, $ByteLimit)
    $invalidUtf8 = $false
    try { $text = [Text.UTF8Encoding]::new($false, $true).GetString($buffer, 0, $length) }
    catch [Text.DecoderFallbackException] {
        $invalidUtf8 = $true
        $text = [Text.Encoding]::UTF8.GetString($buffer, 0, $length)
    }
    return @{ text = $text; byteLimit = $ByteLimit; bytesRead = $length; truncated = $read -gt $ByteLimit; invalidUtf8 = $invalidUtf8 }
}

function Complete-OwnedLabProbeProcessOutput {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][Diagnostics.Process]$Process,
        [Parameter(Mandatory)][string]$StandardOutputPath,
        [Parameter(Mandatory)][string]$StandardErrorPath
    )
    if (-not $Process.HasExited) { throw 'OwnedLabProbeProcessStillRunning' }
    $failed = $Process.ExitCode -ne 0
    try {
        # The caller already observed exit; now join Start-Process's async log copies.
        $Process.WaitForExit()
        $console = Read-OwnedLabProbeText -Path $StandardOutputPath -ByteLimit 65536
        if ($console.truncated -or [Text.Encoding]::UTF8.GetByteCount($console.text) -gt 65536) { throw 'OwnedLabProbeConsoleOversize' }
        Write-Host $console.text -ErrorAction Stop
    }
    catch {
        if (-not $failed) { throw }
        # Reporting must never replace an already observed nonzero child exit.
        try { Write-Host '{"diagnostic":"OwnedLabProbeConsoleUnavailable","stream":"stdout"}' -ErrorAction Stop } catch { }
    }
    if ($failed) {
        try {
            $stderr = Read-OwnedLabProbeText -Path $StandardErrorPath -ByteLimit 16384
            $diagnostic = [ordered]@{ diagnostic = 'OwnedLabProbeStandardError'; available = $true; byteLimit = $stderr.byteLimit; bytesRead = $stderr.bytesRead; truncated = $stderr.truncated; invalidUtf8 = $stderr.invalidUtf8; text = $stderr.text }
            # JSON framing escapes control characters; only this owned test log is read.
            Write-Host ($diagnostic | ConvertTo-Json -Compress) -ErrorAction Stop
        }
        catch {
            try { Write-Host '{"diagnostic":"OwnedLabProbeStandardError","available":false}' -ErrorAction Stop } catch { }
        }
        throw 'OwnedLabProbeProcessFailed'
    }
}

Export-ModuleMember -Function Complete-OwnedLabProbeProcessOutput
