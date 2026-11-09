[CmdletBinding()]
param([ValidateRange(30,600)][int]$TimeoutSeconds = 240)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows) { throw 'Testing acceptance requires the actual shipping Windows WinUI executable.' }
$repo = Split-Path $PSScriptRoot -Parent
$hostRoot = Join-Path $repo '.host/project-workbench'
$app = Join-Path $hostRoot 'artifacts/native/app/Workbench.App.exe'
$delivery = Join-Path $repo 'artifacts/native/testing-plugin'
Import-Module (Join-Path $PSScriptRoot 'TestingPluginDelivery.psm1') -Force
Test-TestingPluginDelivery -Directory $delivery -RequireCurrentSource
if (-not (Test-Path $app -PathType Leaf)) { throw 'Build the shipping host first.' }
Import-Module (Join-Path $PSScriptRoot 'TestingPluginProbe.psm1') -Force
Import-Module (Join-Path $hostRoot 'scripts/NativeGalleryReport.psm1') -Force
$identity = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ') + '-' + [Guid]::NewGuid().ToString('N')
$output = Join-Path $repo "artifacts/native/testing-probes/$identity"
New-Item $output -ItemType Directory | Out-Null
$root = Join-Path (Join-Path ([IO.Path]::GetTempPath()) 'ProjectWorkbench-testing-probe') ([Guid]::NewGuid().ToString('N'))
$hash = (Get-FileHash (Join-Path $delivery (Get-TestingPluginPackageName)) -Algorithm SHA256).Hash.ToLowerInvariant()
$galleryRows = [Collections.Generic.List[hashtable]]::new()
try {
$completed = @(Invoke-TestingPluginProbePhases -OutputDirectory $output -ExpectedRoot $root -ExpectedPackageHash $hash -Observe {
    param($phase, $report, $observation)
    $process = $null
    $galleryRow = $null
    try {
        $arguments = @('--testing-probe',$phase,'--probe-root',"`"$root`"",'--smoke-report',"`"$report`"")
        $process = Start-Process -FilePath $app -ArgumentList $arguments -PassThru -RedirectStandardOutput (Join-Path $output "$phase.stdout.log") -RedirectStandardError (Join-Path $output "$phase.stderr.log")
        $observation.ProcessId = $process.Id
        $galleryRow = @{ phase = $phase; report = $report; processId = $process.Id; birth = $null; exit = $null; exitCode = $null; hasExited = $false }
        $galleryRows.Add($galleryRow)
        if ($process.Handle -eq [IntPtr]::Zero) { throw 'Testing process handle unavailable.' }
        $galleryRow.birth = $process.StartTime.ToUniversalTime().ToFileTimeUtc()
        $observation.HasExited = $process.WaitForExit($TimeoutSeconds * 1000)
        if ($observation.HasExited) {
            $observation.ExitCode = $process.ExitCode
            $galleryRow.hasExited = $true; $galleryRow.exitCode = $process.ExitCode; $galleryRow.exit = $process.ExitTime.ToUniversalTime().ToFileTimeUtc()
            $roots = @($root)
            $projects = Join-Path $root '.workspaces/projects'
            foreach ($part in @($root, (Join-Path $root '.workspaces'), $projects)) {
                if ((Test-Path -LiteralPath $part) -and ((Get-Item -LiteralPath $part).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'TestingOwnedRootInvalid' }
            }
            if (Test-Path -LiteralPath $projects -PathType Container) {
                foreach ($project in Get-ChildItem -LiteralPath $projects -Directory) {
                    if ($project.Name -cnotmatch '^[0-9a-f]{32}$' -or ($project.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'TestingOwnedRootInvalid' }
                    $roots += $project.FullName
                }
            }
            $leases = @()
            try {
                foreach ($owned in $roots) { $leases += [IO.File]::Open((Join-Path $owned 'host.lock'), [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None) }
                $observation.LeaseAvailable = $true
            }
            finally { foreach ($lease in $leases) { $lease.Dispose() } }
        }
    }
    finally {
        if ($null -ne $process) {
            if ($process.HasExited -and $null -ne $galleryRow) {
                $galleryRow.hasExited = $true; $galleryRow.exitCode = $process.ExitCode; $galleryRow.exit = $process.ExitTime.ToUniversalTime().ToFileTimeUtc()
            }
            $process.Dispose()
        }
    }
})
} finally {
    foreach ($row in $galleryRows) {
        try {
            Write-NativeGalleryBinding -OriginalReportPath $row.report -Phase ("testing-" + $row.phase) -ExecutablePath $app `
                -SourceCommit ((& git -C $hostRoot rev-parse HEAD).Trim()) -ProcessId $row.processId `
                -ProcessCreationFileTimeUtc $row.birth -ProcessExitFileTimeUtc $row.exit -ExitCode $row.exitCode -HasExited $row.hasExited `
                -InstalledRoot (Join-Path $root 'Plugins') -PackageIds @('com.projectworkbench.testing') `
                -SourceZipRoot $delivery -SourceZipPaths @{ 'com.projectworkbench.testing' = (Join-Path $delivery (Get-TestingPluginPackageName)) } | Out-Null
        } catch { Write-Warning 'NativeGalleryBindingUnavailable' }
    }
}
$summary = @{ schemaVersion = 1; succeeded = $true; sourceCommit = (& git -C $repo rev-parse HEAD).Trim(); hostSourceCommit = (& git -C $hostRoot rev-parse HEAD).Trim(); userRoot = $root; packageSha256 = $hash; phases = $completed; limits = 'Windows Server automatic acceptance only; Windows11 human picker/IME/DPI/accessibility and real devices/providers deferred. Synthetic interrupted journal seed is not an OS crash.' }
$summary | ConvertTo-Json -Depth 20 | Set-Content (Join-Path $output 'summary.json') -Encoding utf8
Write-Host "Testing plugin actual native probe completed: $output"
