$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
Import-Module (Join-Path $repo 'scripts/TestingPluginDelivery.psm1') -Force
if (-not ('WorkbenchTestingManagedAudit' -as [type])) { throw 'Managed payload audit is not available.' }
function Refuses([scriptblock]$Action, [string]$Expected) {
    try { & $Action | Out-Null }
    catch {
        if ($_.Exception.GetBaseException().Message -cne $Expected) { throw "Expected $Expected; observed $($_.Exception.GetBaseException().Message)" }
        return
    }
    throw "Expected $Expected; invalid payload was accepted."
}
$utf8 = [Text.UTF8Encoding]::new($false, $true)
$manifest = [IO.File]::ReadAllBytes((Join-Path $repo 'plugins/Workbench.Testing/plugin.json'))
[WorkbenchTestingManagedAudit]::Manifest($manifest, 'Production')
$text = $utf8.GetString($manifest)
$changed = $text | ConvertFrom-Json -AsHashtable
$changed.id = 'com.example.other'
Refuses { [WorkbenchTestingManagedAudit]::Manifest($utf8.GetBytes(($changed | ConvertTo-Json -Depth 8)), 'Production') } 'TestingManifestInvalid'
$duplicate = $text.Replace('"apiVersion": 1', '"apiVersion": 1, "apiVersion": 1')
if ($duplicate -ceq $text) { throw 'Duplicate-field fixture was not constructed.' }
Refuses { [WorkbenchTestingManagedAudit]::Manifest($utf8.GetBytes($duplicate), 'Production') } 'TestingManifestInvalid'
Refuses { [WorkbenchTestingManagedAudit]::Manifest([byte[]](0xff, 0xfe), 'Production') } 'TestingManifestInvalid'
Refuses { [WorkbenchTestingManagedAudit]::Inspect([byte[]](0, 1, 2), 'Workbench.Testing', ('0' * 40), $false, 'Workbench.Testing.TestingPlugin') } 'TestingManagedPeInvalid'
Write-Host 'Strict managed manifest and invalid PE regressions passed.'
