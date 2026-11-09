[CmdletBinding()]
param([Parameter(Mandatory)][string]$BuildDirectory, [Parameter(Mandatory)][string]$HostDirectory,
      [Parameter(Mandatory)][string]$PluginCommit, [Parameter(Mandatory)][string]$HostCommit)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
Import-Module (Join-Path $repo 'scripts/TestingPluginDelivery.psm1') -Force
function Refuses([scriptblock]$Action, [string]$Expected) {
    try { & $Action | Out-Null }
    catch {
        if ($_.Exception.GetBaseException().Message -cne $Expected) { throw "Expected $Expected; observed $($_.Exception.GetBaseException().Message)" }
        return
    }
    throw "Expected $Expected; invalid managed closure was accepted."
}
$payload = @{}
foreach ($name in @('Workbench.Testing.dll','Workbench.Testing.Domain.dll','Workbench.Testing.Windows.dll','Workbench.Testing.deps.json')) {
    $payload[$name] = [IO.File]::ReadAllBytes((Join-Path $BuildDirectory $name))
}
$facts = [WorkbenchTestingManagedAudit]::Audit($payload, $HostDirectory, $PluginCommit, $HostCommit, 'Production') | ConvertFrom-Json -AsHashtable
if ($facts.schemaVersion -ne 1 -or @($facts.assemblies).Count -ne 3 -or @($facts.deps).Count -ne 1 -or @($facts.hostBindings).Count -lt 2) { throw 'Managed closure facts incomplete.' }
Refuses { [WorkbenchTestingManagedAudit]::Audit($payload, $HostDirectory, ('0' * 40), $HostCommit, 'Production') } 'TestingManagedIdentityInvalid'
Refuses { [WorkbenchTestingManagedAudit]::Audit($payload, $HostDirectory, $PluginCommit, ('0' * 40), 'Production') } 'TestingManagedHostBindingInvalid'
foreach ($mutation in @('extra-sdk-asset','foreign-managed-runtime-target','missing-private-edge')) {
    $changed = @{}; foreach ($key in $payload.Keys) { $changed[$key] = $payload[$key].Clone() }
    $deps = [Text.Encoding]::UTF8.GetString($changed['Workbench.Testing.deps.json']) | ConvertFrom-Json -AsHashtable
    $target = '.NETCoreApp,Version=v10.0'
    switch ($mutation) {
        'extra-sdk-asset' { $deps.targets[$target]['Workbench.Sdk/0.4.0'].runtime['lib/net10.0/Foreign.dll'] = @{} }
        'foreign-managed-runtime-target' { $deps.targets[$target]['Workbench.Sdk/0.4.0'].runtimeTargets = @{ 'runtimes/win-x64/lib/net10.0/Workbench.Core.dll' = @{ rid='win-x64'; assetType='runtime' } } }
        'missing-private-edge' { $deps.targets[$target]['Workbench.Testing/0.2.0'].dependencies.Remove('Workbench.Testing.Domain') }
    }
    $changed['Workbench.Testing.deps.json'] = [Text.UTF8Encoding]::new($false).GetBytes(($deps | ConvertTo-Json -Depth 50 -Compress))
    Refuses { [WorkbenchTestingManagedAudit]::Audit($changed, $HostDirectory, $PluginCommit, $HostCommit, 'Production') } 'TestingManagedDepsInvalid'
}
Write-Host 'Managed closure verified with independent plugin/host commits; three dependency mutations refused.'
