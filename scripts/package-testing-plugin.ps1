[CmdletBinding()]
param([ValidateSet('Debug','Release')][string]$Configuration = 'Release', [switch]$ReferenceOnly,
      [string]$OutputDirectory = (Join-Path $PSScriptRoot '../artifacts/native/testing-plugin'))
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows -and -not $ReferenceOnly) { throw 'Native testing packaging requires actual Windows MSVC; explicit -ReferenceOnly compiles without a ZIP.' }
$repo = Split-Path $PSScriptRoot -Parent
$project = Join-Path $repo 'plugins/Workbench.Testing/Workbench.Testing.csproj'
$build = Join-Path $repo $(if ($ReferenceOnly) { 'artifacts/testing-reference-build' } else { 'artifacts/testing-package-build' })
$arguments = @('build', $project, '-c', $Configuration, '--nologo', '-o', $build)
if ($ReferenceOnly) { $arguments += @('-p:OwnedLabReferenceOnly=true','-p:UseWinUI=false','-p:AppxGeneratePriEnabled=false','-p:WindowsAppSDKSelfContained=false') }
else {
    $native = Join-Path $repo 'artifacts/native/owned-lab'
    if (-not (Test-Path -LiteralPath $native)) { & (Join-Path $PSScriptRoot 'build-owned-lab.ps1') -Configuration $Configuration -OutputDirectory $native }
    foreach ($file in @('Workbench.OwnedLab.exe','OwnedNativeBuild.g.cs','descriptor.json','native-build.json')) { if (-not (Test-Path -LiteralPath (Join-Path $native $file) -PathType Leaf)) { throw 'TestingOwnedHelperMissing' } }
    $arguments += @("-p:OwnedLabDescriptorPath=$(Join-Path $native 'OwnedNativeBuild.g.cs')", "-p:OwnedLabDescriptorJsonPath=$(Join-Path $native 'descriptor.json')")
}
& dotnet @arguments
if ($LASTEXITCODE -ne 0) { throw "Testing plugin build failed ($LASTEXITCODE)." }
if ($ReferenceOnly) { Write-Host 'ReferenceOnly compilation completed with unavailable descriptor; no production ZIP was created.'; return }
Copy-Item -LiteralPath (Join-Path $native 'Workbench.OwnedLab.exe') $build
Copy-Item -LiteralPath (Join-Path $native 'native-build.json') (Join-Path $build 'OwnedLab.native-build.json')
Import-Module (Join-Path $PSScriptRoot 'TestingPluginDelivery.psm1') -Force
New-TestingPluginDelivery -BuildDirectory $build -OutputDirectory $OutputDirectory -ReferenceOnly:$ReferenceOnly
