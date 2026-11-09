[CmdletBinding()]
param([ValidateSet('Debug','Release')][string]$Configuration = 'Release',
      [Parameter(Mandatory)][string]$OutputDirectory, [switch]$BuildAcceptanceFixtures)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
function New-OwnedLabToolchainStartInfo {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$CommandInterpreter, [Parameter(Mandatory)][string]$DeveloperScript,
          [Parameter(Mandatory)][string]$WorkingDirectory)
    $bootstrap = Join-Path $WorkingDirectory 'owned-lab-vsdev.cmd'
    $batch = "@echo off`r`ncall `"%PWB_OWNED_VSDEVCMD%`" -no_logo -arch=x64 -host_arch=x64 >nul`r`nif errorlevel 1 exit /b %errorlevel%`r`nset`r`n"
    [IO.File]::WriteAllText($bootstrap, $batch, [Text.Encoding]::ASCII)
    $info = [Diagnostics.ProcessStartInfo]::new()
    $info.FileName = $CommandInterpreter
    # cmd.exe uses its own command parser. Keep the raw command constant; paths cross only in
    # child environment variables, so PowerShell's native argument re-quoting cannot alter them.
    $info.Arguments = '/d /q /c call "%PWB_OWNED_TOOLCHAIN_BOOTSTRAP%"'
    $info.WorkingDirectory = $WorkingDirectory
    $info.UseShellExecute = $false; $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true; $info.RedirectStandardError = $true
    $info.Environment['PWB_OWNED_TOOLCHAIN_BOOTSTRAP'] = $bootstrap
    $info.Environment['PWB_OWNED_VSDEVCMD'] = $DeveloperScript
    return $info
}
if (-not $IsWindows -or [Runtime.InteropServices.RuntimeInformation]::OSArchitecture -ne 'X64') { throw 'OwnedLabNativeBuildUnavailable: actual Windows MSVC x64 and Windows SDK are required.' }
$repo = Split-Path $PSScriptRoot -Parent
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (-not $output.StartsWith(([IO.Path]::GetFullPath($repo) + [IO.Path]::DirectorySeparatorChar), [StringComparison]::OrdinalIgnoreCase) -or $output -notlike '*artifacts*') { throw 'OwnedLabBuildRequiresOwnedArtifactDirectory' }
for ($part = $output; $part; $part = [IO.Path]::GetDirectoryName($part)) {
    if ((Test-Path -LiteralPath $part) -and ((Get-Item -LiteralPath $part -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'OwnedLabBuildLinkRefused' }
}
if ((Test-Path -LiteralPath $output) -and @(Get-ChildItem -LiteralPath $output -Force).Count -ne 0) { throw 'OwnedLabBuildOutputAlreadyExists' }
New-Item $output -ItemType Directory -Force | Out-Null
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
if (-not (Test-Path $vswhere -PathType Leaf)) { throw 'OwnedLabMsvcUnavailable' }
$installation = (& $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath | Select-Object -First 1)
if (-not $installation) { throw 'OwnedLabMsvcUnavailable' }
$developer = Join-Path $installation 'Common7/Tools/VsDevCmd.bat'
if (-not (Test-Path -LiteralPath $developer -PathType Leaf)) { throw 'OwnedLabMsvcUnavailable' }
$loader = [Diagnostics.Process]::new()
$loader.StartInfo = New-OwnedLabToolchainStartInfo -CommandInterpreter $env:ComSpec -DeveloperScript $developer -WorkingDirectory $output
try {
    if (-not $loader.Start()) { throw 'OwnedLabToolchainEnvironmentFailed' }
    $stdout = $loader.StandardOutput.ReadToEndAsync(); $stderr = $loader.StandardError.ReadToEndAsync()
    $loader.WaitForExit()
    $environmentText = $stdout.GetAwaiter().GetResult(); $null = $stderr.GetAwaiter().GetResult()
    if ($loader.ExitCode -ne 0) { throw 'OwnedLabToolchainEnvironmentFailed' }
    $environmentLines = $environmentText -split '\r?\n'
}
finally { $loader.Dispose() }
foreach ($line in $environmentLines) { if ($line -match '^([^=]+)=(.*)$') { [Environment]::SetEnvironmentVariable($Matches[1], $Matches[2], 'Process') } }
$compiler = (Get-Command cl.exe -ErrorAction Stop).Source
$resource = (Get-Command rc.exe -ErrorAction Stop).Source
$linker = (Get-Command link.exe -ErrorAction Stop).Source
$dumpbin = (Get-Command dumpbin.exe -ErrorAction Stop).Source
if ($compiler -notmatch '\\Hostx64\\x64\\cl.exe$' -or -not $env:WindowsSDKVersion -or -not $env:WindowsSdkDir) { throw 'OwnedLabActualX64ToolchainRequired' }
$msvc = [Diagnostics.FileVersionInfo]::GetVersionInfo($compiler).ProductVersion
$sdk = $env:WindowsSDKVersion.TrimEnd('\')
Write-Host "Owned toolchain loaded: MSVC $msvc; SDK $sdk; compiler $compiler"
$commit = (& git -C $repo rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $commit -cnotmatch '^[0-9a-f]{40}$') { throw 'OwnedLabSourceIdentityUnavailable' }
$native = Join-Path $repo 'native/Workbench.OwnedLab'
function Get-NativeSourceRecords {
    param([string[]]$Extra = @())
    return @( @((Get-ChildItem -LiteralPath $native -File).FullName) + @($Extra | Where-Object { $_ }) | Sort-Object -Unique | ForEach-Object {
        $item = Get-Item -LiteralPath $_
        [ordered]@{ path = [IO.Path]::GetRelativePath($repo,$item.FullName).Replace('\','/'); length = $item.Length; sha256 = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
    })
}
function Build-Image {
    param([string]$Directory, [string]$Profile, [string]$Macro = '', [string]$ReplacementBroker = '')
    New-Item (Join-Path $Directory 'obj') -ItemType Directory -Force | Out-Null
    $sources = @('Main','Protocol','TargetProcess','Automation','Capture','Target') | ForEach-Object { Join-Path $native "$_.cpp" }
    $sources += if ($ReplacementBroker) { $ReplacementBroker } else { Join-Path $native 'Broker.cpp' }
    $objects = @()
    foreach ($source in $sources) {
        $object = Join-Path $Directory ('obj/' + [IO.Path]::GetFileNameWithoutExtension($source) + '.obj')
        $arguments = @('/nologo','/c','/std:c++20','/MT','/EHsc','/W4','/permissive-','/Brepro','/DUNICODE','/D_UNICODE','/D_WIN32_WINNT=0x0A00','/DWINVER=0x0A00',"/I$native", "/Fo$object")
        $arguments += if ($Configuration -eq 'Release') { '/O2' } else { '/Od' }
        if ($Macro) { $arguments += "/D$Macro" }
        & $compiler @arguments $source | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "OwnedLabCompileFailed: $Profile/$Macro/$([IO.Path]::GetFileName($source))" }
        $objects += $object
    }
    $res = Join-Path $Directory 'obj/BuildInfo.res'
    & $resource /nologo "/fo$res" (Join-Path $native 'BuildInfo.rc') | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'OwnedLabResourceCompileFailed' }
    $image = Join-Path $Directory 'Workbench.OwnedLab.exe'
    & $linker /NOLOGO /MACHINE:X64 /SUBSYSTEM:WINDOWS /Brepro "/OUT:$image" @objects $res kernel32.lib user32.lib gdi32.lib ole32.lib oleaut32.lib uiautomationcore.lib bcrypt.lib shell32.lib | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'OwnedLabLinkFailed' }
    $imports = (& $dumpbin /DEPENDENTS $image | Out-String)
    if ($LASTEXITCODE -ne 0 -or $imports -match '(?i)(VCRUNTIME|MSVCP[0-9]|ucrtbase|api-ms-win-crt)') { throw 'OwnedLabStaticCrtVerificationFailed' }
    $imports | Set-Content (Join-Path $Directory 'imports.txt') -Encoding utf8
    $records = @(Get-NativeSourceRecords -Extra $(if ($ReplacementBroker) { @($ReplacementBroker) } else { @() }))
    $sourceText = ($records | ForEach-Object { "$($_.path) $($_.length) $($_.sha256)`n" }) -join ''
    $sourceText += "profile=$Profile`nmacro=$Macro`n"
    $sourceSha = [Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData([Text.UTF8Encoding]::new($false).GetBytes($sourceText)))
    $descriptor = [ordered]@{ Available = $true; ImageSha = (Get-FileHash $image -Algorithm SHA256).Hash.ToLowerInvariant(); NativeSourceSha = $sourceSha; SourceCommit = $commit; Msvc = $msvc; Sdk = $sdk; Profile = $Profile }
    $descriptor | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $Directory 'descriptor.json') -Encoding utf8
    @{ descriptor = $descriptor; sources = $records; macro = $Macro; compiler = $compiler; resourceCompiler = $resource; linker = $linker; staticCrt = $true } | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $Directory 'native-build.json') -Encoding utf8
    return $descriptor
}
$production = Build-Image -Directory $output -Profile Production
$literal = @($production.ImageSha,$production.NativeSourceSha,$production.SourceCommit,$production.Msvc,$production.Sdk,$production.Profile) | ForEach-Object { (ConvertTo-Json -InputObject ([string]$_) -Compress) }
@"
namespace Workbench.Testing.Windows;
internal static class OwnedNativeBuild
{
    internal static OwnedDriverDescriptor Descriptor { get; } = new(true, $($literal -join ', '));
}
"@ | Set-Content (Join-Path $output 'OwnedNativeBuild.g.cs') -Encoding utf8
if ($BuildAcceptanceFixtures) {
    foreach ($name in @('UiaBlock','GdiBlock','Hang','Crash','EarlyExit','DpiFailure','BadFrame','ChangedIdentity','StaleReady','NestedJob','SealedResume','InvalidClosedZeroExit')) {
        $macro = switch ($name) { 'UiaBlock' {'OWNED_FIXTURE_UIA_BLOCK'} 'GdiBlock' {'OWNED_FIXTURE_GDI_BLOCK'} 'EarlyExit' {'OWNED_FIXTURE_EARLY_EXIT'} 'DpiFailure' {'OWNED_FIXTURE_DPI_FAILURE'} 'BadFrame' {'OWNED_FIXTURE_BAD_FRAME'} 'ChangedIdentity' {'OWNED_FIXTURE_CHANGED_IDENTITY'} 'StaleReady' {'OWNED_FIXTURE_STALE_READY'} 'InvalidClosedZeroExit' {'OWNED_FIXTURE_INVALID_CLOSED_ZERO_EXIT'} default {''} }
        $replacement = if ($name -cin @('Hang','Crash')) { Join-Path $repo "tests/Workbench.OwnedLab.NativeFixtures/${name}Broker.cpp" } else { '' }
        Build-Image -Directory (Join-Path $output "fixtures/$name") -Profile AcceptanceFixture -Macro $macro -ReplacementBroker $replacement | Out-Null
    }
}
Write-Host ($production | ConvertTo-Json -Compress)
