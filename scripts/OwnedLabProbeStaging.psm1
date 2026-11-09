Set-StrictMode -Version Latest
function Copy-OwnedLabProbeNativeFixtures {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$BuildDirectory, [Parameter(Mandatory)][string]$StageDirectory)
    $ErrorActionPreference = 'Stop'
    # Shipping payloads already own Fixtures. Keep native fixtures separate even on
    # case-insensitive filesystems, and refuse all existing destination spellings.
    $root = Join-Path $StageDirectory 'native-fixtures'
    if (@(Get-ChildItem -LiteralPath $StageDirectory -Force | Where-Object Name -IEQ 'native-fixtures').Count -ne 0) { throw 'OwnedLabProbeNativeFixtureStageExists' }
    $source = Join-Path $BuildDirectory 'fixtures'
    $aliases = @('PartialHeader','TruncatedInit','ExtraInit','OversizeInit','CrossSessionAction','OutOfOrderAction')
    $reserved = $aliases + @('Missing','FakePe','WrongHash','Reparse')
    if (@(Get-ChildItem -LiteralPath $source -Force | Where-Object { $_.Name -iin $reserved }).Count -ne 0) { throw 'OwnedLabProbeNativeFixtureInputConflict' }
    Copy-Item -LiteralPath $source -Destination $root -Recurse
    foreach ($name in $aliases) {
        Copy-Item -LiteralPath (Join-Path $root 'NestedJob') -Destination (Join-Path $root $name) -Recurse
    }
    foreach ($name in @('Missing','FakePe','WrongHash')) {
        $directory = Join-Path $root $name; New-Item $directory -ItemType Directory | Out-Null
        Copy-Item (Join-Path $root 'NestedJob/descriptor.json') $directory
        if ($name -ceq 'FakePe') { [IO.File]::WriteAllBytes((Join-Path $directory 'Workbench.OwnedLab.exe'),[byte[]]@(0x4d,0x5a,0,0)) }
        elseif ($name -ceq 'WrongHash') {
            Copy-Item (Join-Path $root 'NestedJob/Workbench.OwnedLab.exe') $directory
            $bad = Get-Content (Join-Path $directory 'descriptor.json') -Raw | ConvertFrom-Json -AsHashtable
            $bad.ImageSha = '0' * 64; $bad | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $directory 'descriptor.json') -Encoding utf8
        }
    }
    return $root
}
Export-ModuleMember -Function Copy-OwnedLabProbeNativeFixtures
