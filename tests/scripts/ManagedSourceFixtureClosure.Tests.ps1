[CmdletBinding()]
param([Parameter(Mandatory)][string]$BuildDirectory, [Parameter(Mandatory)][string]$HostDirectory,
      [Parameter(Mandatory)][string]$PluginCommit, [Parameter(Mandatory)][string]$HostCommit)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot '../../scripts/TestingPluginDelivery.psm1') -Force
function Refuses([scriptblock]$Action, [string]$Expected) {
    try { & $Action | Out-Null } catch {
        if ($_.Exception.GetBaseException().Message -cne $Expected) { throw "Expected $Expected; observed $($_.Exception.GetBaseException().Message)" }; return
    }
    throw "Expected $Expected; invalid fixture closure was accepted."
}
$payload = @{}
foreach ($name in @('Workbench.Testing.dll','Workbench.Testing.Domain.dll','Workbench.Testing.Windows.dll','Workbench.Testing.deps.json','Workbench.Testing.SourceAcceptance.dll','Workbench.Testing.SourceAcceptance.deps.json')) {
    $payload[$name] = [IO.File]::ReadAllBytes((Join-Path $BuildDirectory $name))
}
$facts = [WorkbenchTestingManagedAudit]::Audit($payload,$HostDirectory,$PluginCommit,$HostCommit,'SourceAcceptance') | ConvertFrom-Json -AsHashtable
if ($facts.schemaVersion -ne 1 -or @($facts.assemblies).Count -ne 4 -or @($facts.deps).Count -ne 2 -or @($facts.hostBindings).Count -lt 2) { throw 'Source fixture managed facts incomplete.' }
Refuses { [WorkbenchTestingManagedAudit]::Audit($payload,$HostDirectory,('0' * 40),$HostCommit,'SourceAcceptance') } 'TestingManagedIdentityInvalid'
Refuses { [WorkbenchTestingManagedAudit]::Audit($payload,$HostDirectory,$PluginCommit,('0' * 40),'SourceAcceptance') } 'TestingManagedHostBindingInvalid'
$missing=@{}; foreach ($key in $payload.Keys) { if ($key -cne 'Workbench.Testing.SourceAcceptance.dll') { $missing[$key]=$payload[$key] } }
Refuses { [WorkbenchTestingManagedAudit]::Audit($missing,$HostDirectory,$PluginCommit,$HostCommit,'SourceAcceptance') } 'TestingManagedPayloadInvalid'
$changed=@{}; foreach ($key in $payload.Keys) { $changed[$key]=$payload[$key].Clone() }
$deps=[Text.Encoding]::UTF8.GetString($changed['Workbench.Testing.SourceAcceptance.deps.json']) | ConvertFrom-Json -AsHashtable
$deps.targets['.NETCoreApp,Version=v10.0']['Workbench.Testing.SourceAcceptance/0.2.0'].dependencies.Remove('Workbench.Testing')
$changed['Workbench.Testing.SourceAcceptance.deps.json']=[Text.UTF8Encoding]::new($false).GetBytes(($deps | ConvertTo-Json -Depth 50 -Compress))
Refuses { [WorkbenchTestingManagedAudit]::Audit($changed,$HostDirectory,$PluginCommit,$HostCommit,'SourceAcceptance') } 'TestingManagedDepsInvalid'
Write-Host 'Source fixture managed closure verified with independent plugin/host commits; identity, payload and dependency mutations refused.'
