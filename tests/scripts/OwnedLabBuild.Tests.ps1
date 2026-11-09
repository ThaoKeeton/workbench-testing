[CmdletBinding()]
param([ValidateSet('ToolchainContract','ReferenceWorkflow')][string]$Mode = 'ToolchainContract',
      [string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repo ('artifacts/tests/owned-build-contracts/' + [Guid]::NewGuid().ToString('N')) }
New-Item $OutputDirectory -ItemType Directory -Force | Out-Null
function Require([bool]$Value, [string]$Message) { if (-not $Value) { throw $Message } }
function Join-ActualProcess([Diagnostics.ProcessStartInfo]$Info, [string]$Prefix, [switch]$EnvironmentOutput) {
    $process = [Diagnostics.Process]::new(); $process.StartInfo = $Info
    try {
        Require ($process.Start()) 'Contract process did not start.'
        $stdout = $process.StandardOutput.ReadToEndAsync(); $stderr = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        $outText = $stdout.GetAwaiter().GetResult(); $errText = $stderr.GetAwaiter().GetResult()
        if ($EnvironmentOutput) {
            # The loader's stdout is an environment block. Never persist/print inherited values.
            [IO.File]::WriteAllText((Join-Path $OutputDirectory "$Prefix.observation.log"), "Synthetic toolchain process exit=$($process.ExitCode); environment output consumed only in memory.`n")
        }
        else {
            [IO.File]::WriteAllText((Join-Path $OutputDirectory "$Prefix.stdout.log"), $outText)
            [IO.File]::WriteAllText((Join-Path $OutputDirectory "$Prefix.stderr.log"), $errText)
        }
        return @{ ExitCode = $process.ExitCode; Stdout = $outText; Stderr = $errText }
    }
    finally { $process.Dispose() }
}
if ($Mode -ceq 'ReferenceWorkflow') {
    # Read the actual checked-in step, then use GitHub's pwsh prolog/epilog and command template.
    $workflow = [IO.File]::ReadAllText((Join-Path $repo '.github/workflows/build.yml'))
    $pattern = '(?ms)^      - name: Explicit owned reference compilation and missing helper refusals\r?\n        shell: pwsh\r?\n        run: \|\r?\n(?<body>(?:          [^\r\n]*\r?\n)+)'
    $matches = [regex]::Matches($workflow, $pattern)
    Require ($matches.Count -eq 1) 'Exact owned reference workflow step was not found once.'
    $body = [regex]::Replace($matches[0].Groups['body'].Value, '(?m)^          ', '')
    $wrapper = Join-Path $OutputDirectory 'actual-workflow-step.ps1'
    $content = '$ErrorActionPreference = ''stop''' + "`n" + $body + "`n" + 'if ((Test-Path -LiteralPath variable:\LASTEXITCODE)) { exit $LASTEXITCODE }' + "`n"
    [IO.File]::WriteAllText($wrapper, $content)
    $info = [Diagnostics.ProcessStartInfo]::new(); $info.FileName = (Get-Process -Id $PID).Path; $info.UseShellExecute = $false
    $info.RedirectStandardOutput = $true; $info.RedirectStandardError = $true; $info.WorkingDirectory = $repo
    $info.ArgumentList.Add('-command'); $info.ArgumentList.Add(". '" + $wrapper.Replace("'", "''") + "'")
    $observation = Join-ActualProcess $info 'workflow'
    Require ($observation.Stdout.Contains('Reference-only unavailable descriptor and missing production helper refusals passed; no native execution or ZIP.')) 'The actual workflow step never reached its validated expected-failure marker.'
    Require ($observation.ExitCode -eq 0) "GitHub pwsh wrapper rejected the actual successful expected-failure step: exit=$($observation.ExitCode)."
    Write-Host 'Exact workflow body through GitHub pwsh command/prolog/epilog passed; actual reference builds and negative helper check, no native execution.'
    return
}
$tokens = $null; $errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $repo 'scripts/build-owned-lab.ps1'), [ref]$tokens, [ref]$errors)
Require ($errors.Count -eq 0) 'Owned build script does not parse.'
$definitions = @($ast.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq 'New-OwnedLabToolchainStartInfo' }, $true))
Require ($definitions.Count -eq 1) 'Expected one actual New-OwnedLabToolchainStartInfo function in the production build script.'
$factory = $definitions[0].Body.GetScriptBlock()
$directory = Join-Path $OutputDirectory 'toolchain path with spaces & marker'; New-Item $directory -ItemType Directory -Force | Out-Null
$developer = if ($IsWindows) { Join-Path $directory 'VS developer with spaces.bat' } else { 'C:\Program Files\Synthetic VS & tools\Common7\Tools\VsDevCmd.bat' }
if ($IsWindows) { [IO.File]::WriteAllText($developer, "@echo off`r`nset PWB_OWNED_SYNTHETIC_TOOLCHAIN_MARKER=space-path-ok`r`nexit /b 0`r`n", [Text.Encoding]::ASCII) }
$interpreter = if ($IsWindows) { $env:ComSpec } else { 'C:\Windows\System32\cmd.exe' }
$info = & $factory -CommandInterpreter $interpreter -DeveloperScript $developer -WorkingDirectory $directory
Require ($info -is [Diagnostics.ProcessStartInfo] -and -not $info.UseShellExecute -and $info.RedirectStandardOutput -and $info.RedirectStandardError) 'Toolchain subprocess must use structured redirected ProcessStartInfo.'
Require ($info.FileName -ceq $interpreter) 'Toolchain interpreter changed.'
Require ($info.Arguments -ceq '/d /q /c call "%PWB_OWNED_TOOLCHAIN_BOOTSTRAP%"' -and $info.ArgumentList.Count -eq 0) 'Fixed raw cmd argument contract changed or includes re-quoted path arguments.'
Require ($info.Environment['PWB_OWNED_VSDEVCMD'] -ceq $developer) 'Space/metacharacter developer path was changed.'
$bootstrap = $info.Environment['PWB_OWNED_TOOLCHAIN_BOOTSTRAP']
Require ([IO.Path]::GetDirectoryName($bootstrap) -ceq $directory -and (Test-Path -LiteralPath $bootstrap -PathType Leaf)) 'Bootstrap is not an actual owned output file.'
$batch = [IO.File]::ReadAllText($bootstrap)
Require ($batch.Contains('call "%PWB_OWNED_VSDEVCMD%" -no_logo -arch=x64 -host_arch=x64 >nul') -and $batch.Contains('if errorlevel 1 exit /b %errorlevel%')) 'Bootstrap lost exact toolchain flags or failure propagation.'
if ($IsWindows) {
    $observation = Join-ActualProcess $info 'synthetic-toolchain' -EnvironmentOutput
    Require ($observation.ExitCode -eq 0 -and $observation.Stdout.Contains('PWB_OWNED_SYNTHETIC_TOOLCHAIN_MARKER=space-path-ok')) 'Actual Windows cmd could not load synthetic developer batch from the space/metacharacter path.'
    Write-Host 'Actual Windows cmd space-path contract passed with synthetic developer batch; this is not MSVC/native proof.'
}
else { Write-Host 'Structured toolchain space-path/bootstrap contract passed; Windows cmd/MSVC execution unavailable on Linux.' }
