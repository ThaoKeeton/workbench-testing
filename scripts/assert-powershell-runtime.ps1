$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion -lt [Version]'7.6' -or [Environment]::Version.Major -lt 10) {
    throw 'PowerShell 7.6+ hosted on .NET 10+ is required; installing a dotnet SDK does not update the PowerShell runtime.'
}
Write-Host "PowerShell runtime verified: $($PSVersionTable.PSVersion); .NET $([Environment]::Version)"
