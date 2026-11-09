[CmdletBinding()]
param([ValidateSet('Debug','Release')][string]$Configuration = 'Release', [switch]$ReferenceOnly,
      [string]$OutputDirectory = (Join-Path $PSScriptRoot '../artifacts/native/source-acceptance-fixture'),
      [string]$ProductionDirectory = (Join-Path $PSScriptRoot '../artifacts/native/source-paired-production'))
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
& (Join-Path $PSScriptRoot 'assert-powershell-runtime.ps1')
if (-not $IsWindows -and -not $ReferenceOnly) { throw 'Native source fixture packaging requires actual Windows MSVC.' }
$repo = Split-Path $PSScriptRoot -Parent
$module = Import-Module (Join-Path $PSScriptRoot 'TestingPluginDelivery.psm1') -Force -PassThru
& $module { param($a,$b) Assert-TestingDistinctDeliveryDirectories $a $b } $OutputDirectory $ProductionDirectory
$build = Join-Path $repo $(if ($ReferenceOnly) { 'artifacts/testing-source-reference-build' } else { 'artifacts/testing-source-package-build' })
foreach ($path in $(if ($ReferenceOnly) { @($build) } else { @($build,$OutputDirectory,$ProductionDirectory) })) {
    for ($part = [IO.Path]::GetFullPath($path); $part; $part = [IO.Path]::GetDirectoryName($part)) {
        $item = Get-Item -LiteralPath $part -Force -ErrorAction SilentlyContinue
        if ($null -ne $item -and ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'TestingDeliveryLinkRefused' }
    }
    if ((Test-Path -LiteralPath $path) -and (-not (Test-Path -LiteralPath $path -PathType Container) -or @(Get-ChildItem -LiteralPath $path -Force).Count -gt 0)) { throw 'TestingDeliveryAlreadyExists' }
}
$project = Join-Path $repo 'tests/Workbench.Testing.SourceAcceptance/Workbench.Testing.SourceAcceptance.csproj'
$arguments = @('build',$project,'-c',$Configuration,'--nologo','-t:Rebuild','-o',$build)
if ($ReferenceOnly) { $arguments += @('-p:OwnedLabReferenceOnly=true','-p:UseWinUI=false','-p:AppxGeneratePriEnabled=false','-p:WindowsAppSDKSelfContained=false') }
else {
    $native = Join-Path $repo 'artifacts/native/owned-lab'
    if (-not (Test-Path -LiteralPath $native)) { & (Join-Path $PSScriptRoot 'build-owned-lab.ps1') -Configuration $Configuration -OutputDirectory $native }
    foreach ($file in @('Workbench.OwnedLab.exe','OwnedNativeBuild.g.cs','descriptor.json','native-build.json')) { if (-not (Test-Path -LiteralPath (Join-Path $native $file) -PathType Leaf)) { throw 'TestingOwnedHelperMissing' } }
    $arguments += @("-p:OwnedLabDescriptorPath=$(Join-Path $native 'OwnedNativeBuild.g.cs')", "-p:OwnedLabDescriptorJsonPath=$(Join-Path $native 'descriptor.json')")
}
& dotnet @arguments
if ($LASTEXITCODE -ne 0) { throw "Source fixture build failed ($LASTEXITCODE)." }
if ($ReferenceOnly) { Write-Host 'Source fixture reference compilation completed; no native ZIP was created.'; return }
Copy-Item -LiteralPath (Join-Path $native 'Workbench.OwnedLab.exe') $build
Copy-Item -LiteralPath (Join-Path $native 'native-build.json') (Join-Path $build 'OwnedLab.native-build.json')
New-TestingPluginDelivery -BuildDirectory $build -OutputDirectory $ProductionDirectory
New-TestingSourceFixtureDelivery -BuildDirectory $build -OutputDirectory $OutputDirectory -ProductionDirectory $ProductionDirectory
