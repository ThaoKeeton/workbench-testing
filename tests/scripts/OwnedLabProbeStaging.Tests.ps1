[CmdletBinding()]
param([string]$ProbeDirectory)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot '../../scripts/OwnedLabProbeStaging.psm1') -Force
$temp = Join-Path ([IO.Path]::GetTempPath()) ('owned-lab-staging-regression-' + [Guid]::NewGuid().ToString('N'))
$utf8 = [Text.UTF8Encoding]::new($false)
function Write-Bytes([string]$Path, [byte[]]$Bytes) {
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path)) | Out-Null
    [IO.File]::WriteAllBytes($Path, $Bytes)
}
function Same-Bytes([string]$Path, [byte[]]$Expected) {
    if (-not [IO.File]::Exists($Path) -or [Convert]::ToHexString([IO.File]::ReadAllBytes($Path)) -cne [Convert]::ToHexString($Expected)) {
        throw "Staging changed or omitted expected bytes: $Path"
    }
}
function Refuses([scriptblock]$Action, [string]$Expected) {
    try { & $Action | Out-Null }
    catch {
        if ($_.Exception.Message -cne $Expected) { throw "Expected $Expected; observed $($_.Exception.Message)" }
        Write-Host "Exact refusal: $Expected"; return
    }
    throw "Expected $Expected; conflicting fixture stage was accepted."
}
try {
    $build = Join-Path $temp 'compiled'
    # These are inert synthetic bytes, never native images or a production delivery.
    $baseNames = @('UiaBlock','GdiBlock','Hang','Crash','EarlyExit','DpiFailure','BadFrame','ChangedIdentity','StaleReady','NestedJob','SealedResume','InvalidClosedZeroExit')
    $descriptor = $utf8.GetBytes('{"Available":true,"ImageSha":"1111111111111111111111111111111111111111111111111111111111111111","NativeSourceSha":"2222222222222222222222222222222222222222222222222222222222222222","SourceCommit":"3333333333333333333333333333333333333333","Msvc":"synthetic-staging-only","Sdk":"synthetic-staging-only","Profile":"AcceptanceFixture"}')
    $images = @{}
    for ($i = 0; $i -lt $baseNames.Count; $i++) {
        $name = $baseNames[$i]; $images[$name] = [byte[]]@(0x4d,0x5a,0xff,$i,0,0x7f)
        Write-Bytes (Join-Path $build "fixtures/$name/Workbench.OwnedLab.exe") $images[$name]
        Write-Bytes (Join-Path $build "fixtures/$name/descriptor.json") $descriptor
    }
    $stage = Join-Path $temp 'probe'
    if ($ProbeDirectory) {
        Copy-Item -LiteralPath ([IO.Path]::GetFullPath($ProbeDirectory)) -Destination $stage -Recurse
    }
    $shipping = @{}
    foreach ($path in @('Fixtures/figma.synthetic.json','Fixtures/requirements.synthetic.json','Workbench.Testing.deps.json','Workbench.Testing.dll','Workbench.Testing.Domain.dll','Workbench.Testing.Windows.dll','Workbench.OwnedLab.exe','plugin.json')) {
        $shipping[$path] = $utf8.GetBytes("shipping sentinel, preserved exactly: $path`n")
        # Keep actual reference assemblies when testing the separately compiled path resolver.
        if (-not $ProbeDirectory -or $path.StartsWith('Fixtures/') -or -not [IO.File]::Exists((Join-Path $stage $path))) { Write-Bytes (Join-Path $stage $path) $shipping[$path] }
        else { $shipping[$path] = [IO.File]::ReadAllBytes((Join-Path $stage $path)) }
    }
    # Windows resolves Fixtures/fixtures to the same directory. On Linux use a real alias
    # to that directory so the regression cannot pass just because the filesystem is case-sensitive.
    if (-not $IsWindows) { [IO.Directory]::CreateSymbolicLink((Join-Path $stage 'fixtures'), (Join-Path $stage 'Fixtures')) | Out-Null }
    $nativeRoot = Copy-OwnedLabProbeNativeFixtures -BuildDirectory $build -StageDirectory $stage
    if ($nativeRoot -cne (Join-Path $stage 'native-fixtures')) { throw 'Native fixtures did not use their separate stage root.' }
    foreach ($path in $shipping.Keys) { Same-Bytes (Join-Path $stage $path) $shipping[$path] }
    $expectedPaths = @()
    foreach ($name in $baseNames + @('PartialHeader','TruncatedInit','ExtraInit','OversizeInit','CrossSessionAction','OutOfOrderAction')) {
        $expectedPaths += "$name/Workbench.OwnedLab.exe", "$name/descriptor.json"
        $image = if ($images.ContainsKey($name)) { $images[$name] } else { $images['NestedJob'] }
        Same-Bytes (Join-Path $nativeRoot "$name/Workbench.OwnedLab.exe") $image
        Same-Bytes (Join-Path $nativeRoot "$name/descriptor.json") $descriptor
    }
    foreach ($name in @('Missing','FakePe')) {
        $expectedPaths += "$name/descriptor.json"
        Same-Bytes (Join-Path $nativeRoot "$name/descriptor.json") $descriptor
    }
    if (Test-Path -LiteralPath (Join-Path $nativeRoot 'Missing/Workbench.OwnedLab.exe')) { throw 'Missing fixture unexpectedly has a helper.' }
    $expectedPaths += 'FakePe/Workbench.OwnedLab.exe','WrongHash/Workbench.OwnedLab.exe','WrongHash/descriptor.json'
    Same-Bytes (Join-Path $nativeRoot 'FakePe/Workbench.OwnedLab.exe') ([byte[]]@(0x4d,0x5a,0,0))
    Same-Bytes (Join-Path $nativeRoot 'WrongHash/Workbench.OwnedLab.exe') $images['NestedJob']
    $wrong = Get-Content -LiteralPath (Join-Path $nativeRoot 'WrongHash/descriptor.json') -Raw | ConvertFrom-Json -AsHashtable
    if ($wrong.Count -ne 7 -or $wrong.ImageSha -cne ('0' * 64) -or $wrong.NativeSourceSha -cne ('2' * 64) -or $wrong.SourceCommit -cne ('3' * 40) -or $wrong.Available -isnot [bool] -or -not $wrong.Available -or $wrong.Profile -cne 'AcceptanceFixture' -or $wrong.Msvc -cne 'synthetic-staging-only' -or $wrong.Sdk -cne 'synthetic-staging-only') { throw 'WrongHash fixture changed fields beyond its image hash.' }
    $actualPaths = @(Get-ChildItem -LiteralPath $nativeRoot -Recurse -File | ForEach-Object { [IO.Path]::GetRelativePath($nativeRoot,$_.FullName).Replace('\','/') })
    if ($actualPaths.Count -ne 41 -or @(Compare-Object $expectedPaths $actualPaths -CaseSensitive).Count -ne 0) { throw 'Native fixture files are missing, extra or nested under an unintended container.' }
    if (Test-Path -LiteralPath (Join-Path $stage 'Fixtures/NestedJob')) { throw 'Native fixtures were merged into shipping Fixtures.' }
    Write-Host 'Shipping Fixtures/fixtures alias preserved; all eight shipping payloads unchanged; 41 exact native fixture files at the expected paths.'

    # All case variants must refuse on every platform before creating or changing anything.
    foreach ($rootName in @('native-fixtures','NATIVE-FIXTURES','NaTiVe-FiXtUrEs')) {
        $conflictStage = Join-Path $temp $rootName
        $sentinel = Join-Path $conflictStage "$rootName/keep.bin"
        Write-Bytes $sentinel ([byte[]]@(0,0xff,0x7f,0x41))
        Refuses { Copy-OwnedLabProbeNativeFixtures -BuildDirectory $build -StageDirectory $conflictStage } 'OwnedLabProbeNativeFixtureStageExists'
        Same-Bytes $sentinel ([byte[]]@(0,0xff,0x7f,0x41))
        if (@(Get-ChildItem -LiteralPath $conflictStage -Recurse -File).Count -ne 1) { throw 'A refused conflicting stage was modified.' }
    }
    Write-Bytes (Join-Path $build 'fixtures/pArTiAlHeAdEr/keep.bin') ([byte[]]@(0x23,0,0xff))
    $inputConflictStage = Join-Path $temp 'input-conflict'
    [IO.Directory]::CreateDirectory($inputConflictStage) | Out-Null
    Refuses { Copy-OwnedLabProbeNativeFixtures -BuildDirectory $build -StageDirectory $inputConflictStage } 'OwnedLabProbeNativeFixtureInputConflict'
    if (@(Get-ChildItem -LiteralPath $inputConflictStage -Force).Count -ne 0) { throw 'A refused conflicting input changed its destination.' }
    Same-Bytes (Join-Path $build 'fixtures/pArTiAlHeAdEr/keep.bin') ([byte[]]@(0x23,0,0xff))
    if ($ProbeDirectory) {
        # Reparse is made by the Windows-only probe script; a real link suffices for this data-only path check.
        if ($IsWindows) { New-Item -ItemType Junction -Path (Join-Path $nativeRoot 'Reparse') -Target (Join-Path $nativeRoot 'NestedJob') | Out-Null }
        else { [IO.Directory]::CreateSymbolicLink((Join-Path $nativeRoot 'Reparse'), (Join-Path $nativeRoot 'NestedJob')) | Out-Null }
        & dotnet (Join-Path $stage 'Workbench.OwnedLab.WindowsProbe.dll') --fixture-staging-check
        if ($LASTEXITCODE -ne 0) { throw 'Separately compiled fixture launcher did not resolve the staged files.' }
    }
    Write-Host 'Owned fixture staging filesystem regressions passed; inert data only, no native process or UIA proof.'
}
finally {
    if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Recurse -Force }
}
