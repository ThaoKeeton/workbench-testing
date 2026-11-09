Set-StrictMode -Version Latest
$script:TestingPayload = @('Fixtures/figma.synthetic.json', 'Fixtures/requirements.synthetic.json', 'Workbench.Testing.deps.json', 'Workbench.Testing.dll', 'Workbench.Testing.Domain.dll', 'Workbench.Testing.Windows.dll', 'Workbench.OwnedLab.exe', 'plugin.json')
if (-not ('WorkbenchTestingArchiveAudit' -as [type])) {
    Add-Type -TypeDefinition @'
public static class WorkbenchTestingArchiveAudit {
    public static bool NativePe(byte[] image) {
        try {
            using var input = new System.IO.MemoryStream(image, false);
            using var pe = new System.Reflection.PortableExecutable.PEReader(input);
            var header = pe.PEHeaders.PEHeader;
            if (pe.HasMetadata || header == null || pe.PEHeaders.CoffHeader.Machine != System.Reflection.PortableExecutable.Machine.Amd64 ||
                header.Magic != System.Reflection.PortableExecutable.PEMagic.PE32Plus || header.Subsystem != System.Reflection.PortableExecutable.Subsystem.WindowsGui ||
                (pe.PEHeaders.CoffHeader.Characteristics & System.Reflection.PortableExecutable.Characteristics.Dll) != 0 || header.AddressOfEntryPoint == 0) return false;
            bool executable = false;
            foreach (var section in pe.PEHeaders.SectionHeaders) {
                if (section.PointerToRawData < 0 || section.SizeOfRawData < 1 || (long)section.PointerToRawData + section.SizeOfRawData > image.Length) return false;
                if ((section.SectionCharacteristics & System.Reflection.PortableExecutable.SectionCharacteristics.MemExecute) != 0 && header.AddressOfEntryPoint >= section.VirtualAddress && header.AddressOfEntryPoint < (long)section.VirtualAddress + section.SizeOfRawData) executable = true;
            }
            if (!executable || header.ImportTableDirectory.RelativeVirtualAddress == 0) return false;
            var imports = pe.GetSectionData(header.ImportTableDirectory.RelativeVirtualAddress).GetReader();
            for (int i = 0; i < 128; i++) {
                uint original = imports.ReadUInt32(), time = imports.ReadUInt32(), forward = imports.ReadUInt32(), name = imports.ReadUInt32(), first = imports.ReadUInt32();
                if ((original | time | forward | name | first) == 0) return i > 0;
                var nameReader = pe.GetSectionData(checked((int)name)).GetReader();
                var text = new System.Text.StringBuilder(); bool terminated = false;
                for (int c = 0; c < 260; c++) { byte ch = nameReader.ReadByte(); if (ch == 0) { terminated = true; break; } if (ch > 127) return false; text.Append((char)ch); }
                if (!terminated) return false; var dll = text.ToString();
                if (dll.IndexOf("vcruntime", System.StringComparison.OrdinalIgnoreCase) >= 0 || dll.IndexOf("msvcp", System.StringComparison.OrdinalIgnoreCase) >= 0 || dll.IndexOf("ucrtbase", System.StringComparison.OrdinalIgnoreCase) >= 0 || dll.IndexOf("api-ms-win-crt", System.StringComparison.OrdinalIgnoreCase) >= 0) return false;
            }
        } catch { }
        return false;
    }
    public static byte[] Descriptor(byte[] assembly) {
        using var input = new System.IO.MemoryStream(assembly, false);
        using var pe = new System.Reflection.PortableExecutable.PEReader(input);
        var metadata = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
        foreach (var typeHandle in metadata.TypeDefinitions) {
            var type = metadata.GetTypeDefinition(typeHandle);
            if (metadata.GetString(type.Name) == "NativeFixture" || metadata.GetString(type.Namespace) == "Workbench.OwnedLab.FixtureLauncher") throw new System.IO.InvalidDataException("Fixture launcher cannot ship");
        }
        foreach (var handle in metadata.ManifestResources) {
            var resource = metadata.GetManifestResource(handle);
            if (metadata.GetString(resource.Name) != "Workbench.OwnedLab.Descriptor.json") continue;
            if (!resource.Implementation.IsNil) throw new System.IO.InvalidDataException("Linked descriptor");
            var directory = pe.PEHeaders.CorHeader.ResourcesDirectory;
            var block = pe.GetSectionData(directory.RelativeVirtualAddress).GetReader(checked((int)resource.Offset), directory.Size - checked((int)resource.Offset));
            int length = block.ReadInt32();
            if (length < 1 || length > 8192 || length > block.RemainingBytes) throw new System.IO.InvalidDataException("Descriptor size");
            return block.ReadBytes(length);
        }
        throw new System.IO.InvalidDataException("Descriptor missing");
    }
    public static uint Crc(byte[] bytes) {
        uint value = 0xffffffff;
        foreach (byte b in bytes) {
            value ^= b;
            for (int bit = 0; bit < 8; bit++) value = (value >> 1) ^ ((value & 1) != 0 ? 0xedb88320u : 0u);
        }
        return ~value;
    }
}
'@
}
function Get-TestingSourceFiles {
    param([string]$Repo)
    $paths = @(& git -C $Repo ls-files --cached)
    if ($LASTEXITCODE -ne 0 -or $paths.Count -eq 0) { throw 'TestingSourceFileListUnavailable' }
    return @($paths | Sort-Object -Unique | ForEach-Object {
        $file = Get-Item -LiteralPath (Join-Path $Repo $_)
        if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'TestingSourceLinkRefused' }
        @{ path = $_; length = $file.Length; sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
    })
}
function Get-TestingPinnedHostCommit {
    $repo = Split-Path $PSScriptRoot -Parent
    $lock = Get-Content -LiteralPath (Join-Path $repo 'eng/host-sdk.lock.json') -Raw | ConvertFrom-Json -AsHashtable
    if ($lock.sourceCommit -cnotmatch '^[0-9a-f]{40}$' -or $lock.sdkVersion -cne '0.4.0') { throw 'TestingHostLockInvalid' }
    return $lock.sourceCommit
}
function Get-TestingHostAppDirectory {
    if ($env:WORKBENCH_HOST_APP_DIRECTORY) { return $env:WORKBENCH_HOST_APP_DIRECTORY }
    return Join-Path (Split-Path $PSScriptRoot -Parent) '.host/project-workbench/artifacts/native/app'
}
function Get-TestingSdkIdentity {
    $repo = Split-Path $PSScriptRoot -Parent
    $root = Join-Path $repo 'artifacts/host-sdk'
    $manifest = Get-Content -LiteralPath (Join-Path $root 'manifest.json') -Raw | ConvertFrom-Json -AsHashtable
    if ($manifest.referenceOnly -or $manifest.sourceCommit -cne (Get-TestingPinnedHostCommit) -or @($manifest.packages).Count -ne 2) { throw 'TestingProductionSdkRequired' }
    foreach ($record in $manifest.packages) {
        if ($record.path -cnotmatch '^packages/Workbench\.(WinUI\.)?Sdk\.0\.4\.0\.nupkg$') { throw 'TestingSdkPackageInvalid' }
        $file = Get-Item -LiteralPath (Join-Path $root $record.path)
        if ($file.Length -ne $record.length -or (Get-FileHash $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant() -cne $record.sha256) { throw 'TestingSdkPackageHashMismatch' }
    }
    $python = (Get-Command python3, python -ErrorAction SilentlyContinue | Select-Object -First 1).Source
    if (-not $python) { throw 'TestingSdkConsumptionAuditRequiresPython' }
    & $python (Join-Path $PSScriptRoot 'verify-sdk-consumption.py')
    if ($LASTEXITCODE -ne 0) { throw 'TestingSdkConsumptionMismatch' }
    return $manifest
}
function Get-TestingHostIdentity {
    param([string]$AppDirectory, [string]$SourceCommit)
    $dll = Join-Path $AppDirectory 'Workbench.App.dll'
    $exe = Join-Path $AppDirectory 'Workbench.App.exe'
    $version = [Diagnostics.FileVersionInfo]::GetVersionInfo($dll)
    if ($version.ProductVersion -cne "0.4.0+$SourceCommit" -or $version.FileVersion -cne '0.4.0.0' -or [Reflection.AssemblyName]::GetAssemblyName($dll).Version.ToString() -cne '1.0.0.0') { throw 'TestingHostSourceMismatch' }
    return @{ sourceCommit = $SourceCommit; productVersion = $version.ProductVersion; fileVersion = $version.FileVersion; assemblyVersion = '1.0.0.0'; executableSha256 = (Get-FileHash $exe -Algorithm SHA256).Hash.ToLowerInvariant(); dllSha256 = (Get-FileHash $dll -Algorithm SHA256).Hash.ToLowerInvariant() }
}
function Assert-TestingSourceIdentity {
    param([string]$RecordedCommit, [bool]$RecordedDirty, [string]$CurrentCommit, [bool]$CurrentDirty)
    if ($RecordedCommit -cne $CurrentCommit -or $RecordedDirty -or $CurrentDirty) { throw 'TestingCurrentSourceMismatch' }
}
function Assert-TestingSourceRecords {
    param([object[]]$Current, [object[]]$Recorded)
    if ($Current.Count -ne $Recorded.Count) { throw 'TestingSourceFileListInvalid' }
    foreach ($file in $Current) {
        $record = @($Recorded | Where-Object path -CEQ $file.path)
        if ($record.Count -ne 1 -or $record[0].length -ne $file.length -or $record[0].sha256 -cne $file.sha256) { throw 'TestingSourceHashMismatch' }
    }
}
function Assert-TestingOwnedHelper {
    param([byte[]]$Image, [Collections.IDictionary]$Descriptor)
    if ($Descriptor['Available'] -isnot [bool] -or -not $Descriptor['Available'] -or $Descriptor['Profile'] -cne 'Production' -or
        $Descriptor['ImageSha'] -cnotmatch '^[0-9a-f]{64}$' -or $Descriptor['NativeSourceSha'] -cnotmatch '^[0-9a-f]{64}$' -or $Descriptor['SourceCommit'] -cnotmatch '^[0-9a-f]{40}$' -or
        [string]::IsNullOrWhiteSpace($Descriptor['Msvc']) -or [string]::IsNullOrWhiteSpace($Descriptor['Sdk'])) { throw 'TestingOwnedHelperDescriptorInvalid' }
    if ($Image.Length -lt 256 -or $Image.Length -gt 16777216 -or -not [WorkbenchTestingArchiveAudit]::NativePe($Image)) { throw 'TestingOwnedHelperInvalid' }
    $offset = [BitConverter]::ToInt32($Image, 0x3c)
    if ($offset -lt 64 -or $offset -gt $Image.Length - 256 -or [BitConverter]::ToUInt32($Image,$offset) -ne 0x4550 -or
        [BitConverter]::ToUInt16($Image,$offset + 4) -ne 0x8664 -or [BitConverter]::ToUInt16($Image,$offset + 24) -ne 0x20b -or
        ([BitConverter]::ToUInt16($Image,$offset + 22) -band 0x2000) -ne 0) { throw 'TestingOwnedHelperInvalid' }
    if ([Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData($Image)) -cne $Descriptor['ImageSha']) { throw 'TestingOwnedHelperHashMismatch' }
}
function Read-TestingOwnedDescriptor {
    param([byte[]]$Assembly)
    try { return [Text.UTF8Encoding]::new($false,$true).GetString([WorkbenchTestingArchiveAudit]::Descriptor($Assembly)) | ConvertFrom-Json -AsHashtable }
    catch { throw 'TestingOwnedHelperDescriptorInvalid' }
}
function Assert-TestingOwnedSource {
    param([Collections.IDictionary]$Build, [Collections.IDictionary]$Descriptor)
    if ($Build['staticCrt'] -isnot [bool] -or -not $Build['staticCrt'] -or $Build['macro'] -cne '' -or $Build['sources'] -isnot [array]) { throw 'TestingOwnedHelperSourceInvalid' }
    $expected = @('Automation.cpp','Automation.h','Broker.cpp','Broker.h','BuildInfo.rc','Capture.cpp','Capture.h','Main.cpp','Main.h','Protocol.cpp','Protocol.h','Target.cpp','Target.h','TargetProcess.cpp','TargetProcess.h') | ForEach-Object { "native/Workbench.OwnedLab/$_" }
    if (@(Compare-Object $expected @($Build.sources | ForEach-Object path) -CaseSensitive).Count -ne 0 -or $Build.sources.Count -ne $expected.Count) { throw 'TestingOwnedHelperSourceInvalid' }
    $text = ''
    foreach ($record in @($Build.sources | Sort-Object path -CaseSensitive)) {
        if ($record.sha256 -cnotmatch '^[0-9a-f]{64}$' -or $record.length -isnot [long] -and $record.length -isnot [int] -or $record.length -lt 1) { throw 'TestingOwnedHelperSourceInvalid' }
        $text += "$($record.path) $($record.length) $($record.sha256)`n"
    }
    $text += "profile=Production`nmacro=`n"
    if ([Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData([Text.UTF8Encoding]::new($false).GetBytes($text))) -cne $Descriptor.NativeSourceSha) { throw 'TestingOwnedHelperSourceInvalid' }
}
function New-TestingPluginDelivery {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$BuildDirectory, [Parameter(Mandatory)][string]$OutputDirectory, [switch]$ReferenceOnly)
    $repo = Split-Path $PSScriptRoot -Parent
    if ($ReferenceOnly) { throw 'TestingDeliveryReferenceOnly' }
    foreach ($name in @('Workbench.OwnedLab.exe','Workbench.Testing.Windows.dll','OwnedLab.native-build.json')) {
        $path = Join-Path $BuildDirectory $name
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw 'TestingOwnedHelperMissing' }
        for ($part = [IO.Path]::GetFullPath($path); $part; $part = [IO.Path]::GetDirectoryName($part)) {
            if (((Get-Item -LiteralPath $part -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'TestingDeliveryLinkRefused' }
        }
    }
    $helper = Read-TestingOwnedDescriptor ([IO.File]::ReadAllBytes((Join-Path $BuildDirectory 'Workbench.Testing.Windows.dll')))
    Assert-TestingOwnedHelper -Image ([IO.File]::ReadAllBytes((Join-Path $BuildDirectory 'Workbench.OwnedLab.exe'))) -Descriptor $helper
    $nativeBuild = Get-Content (Join-Path $BuildDirectory 'OwnedLab.native-build.json') -Raw | ConvertFrom-Json -AsHashtable
    Assert-TestingOwnedSource $nativeBuild $helper
    $sourceCommit = (& git -C $repo rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'TestingSourceIdentityUnavailable' }
    if ($helper.SourceCommit -cne $sourceCommit) { throw 'TestingOwnedHelperSourceInvalid' }
    $dirty = @(& git -C $repo status --porcelain).Count -ne 0
    if ($dirty) { throw 'TestingDeliveryRequiresCleanSource' }
    $sourceFiles = @(Get-TestingSourceFiles $repo)
    Assert-TestingSourceRecords -Current @($sourceFiles | Where-Object path -CLike 'native/Workbench.OwnedLab/*') -Recorded @($nativeBuild.sources)
    $hostIdentity = Get-TestingHostIdentity -AppDirectory (Get-TestingHostAppDirectory) -SourceCommit (Get-TestingPinnedHostCommit)
    $sdkIdentity = Get-TestingSdkIdentity
    if ((Test-Path $OutputDirectory) -and @(Get-ChildItem $OutputDirectory -Force).Count -gt 0) { throw 'TestingDeliveryAlreadyExists' }
    New-Item $OutputDirectory -ItemType Directory -Force | Out-Null
    $stage = Join-Path ([IO.Path]::GetTempPath()) ('testing-package-stage-' + [Guid]::NewGuid().ToString('N'))
    try {
        New-Item (Join-Path $stage 'Fixtures') -ItemType Directory -Force | Out-Null
        foreach ($name in @('Workbench.Testing.dll', 'Workbench.Testing.Domain.dll', 'Workbench.Testing.Windows.dll', 'Workbench.OwnedLab.exe', 'Workbench.Testing.deps.json')) { Copy-Item -LiteralPath (Join-Path $BuildDirectory $name) $stage }
        Copy-Item -LiteralPath (Join-Path $repo 'plugins/Workbench.Testing/plugin.json') $stage
        foreach ($name in @('requirements.synthetic.json', 'figma.synthetic.json')) { Copy-Item -LiteralPath (Join-Path $repo "plugins/Workbench.Testing.Domain/Fixtures/$name") (Join-Path $stage 'Fixtures') }
        $payload = @($script:TestingPayload | ForEach-Object {
            $file = Get-Item -LiteralPath (Join-Path $stage $_)
            @{ path = $_; length = $file.Length; sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
        })
        $zipName = 'workbench-testing-plugin-0.2.0.zip'
        $zip = Join-Path $OutputDirectory $zipName
        [IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip)
        $metadata = @{ schemaVersion = 1; pluginId = 'com.projectworkbench.testing'; version = '0.2.0'; minimumHostVersion = '0.4.0'; sourceCommit = $sourceCommit; sourceTreeDirty = $dirty; referenceOnly = $false; helper = $helper; nativeBuild = $nativeBuild; host = $hostIdentity; sdk = $sdkIdentity; migratedFrom = (Get-Content (Join-Path $repo 'eng/migration-source.json') -Raw | ConvertFrom-Json -AsHashtable).sourceCommit; sourceFiles = $sourceFiles; payload = $payload; zip = @{ path = $zipName; length = (Get-Item $zip).Length; sha256 = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant() }; createdUtc = [DateTimeOffset]::UtcNow.ToString('O') }
        $metadata | ConvertTo-Json -Depth 20 | Set-Content (Join-Path $OutputDirectory 'provenance.json') -Encoding utf8
        @"
# Independent testing plugin 0.2.0

Install workbench-testing-plugin-0.2.0.zip using the native host's package installation action. Requires host 0.4.0 or later; the separately recorded pinned host commit and SDK package hashes identify the compatibility baseline. Trust in-process code deliberately: ALC is not a sandbox.

Plugin source commit: $sourceCommit. Pinned host source commit: $($hostIdentity.sourceCommit). Reference-only build: $([bool]$ReferenceOnly). Dirty source: $dirty. A Linux reference build does not prove native Windows execution or PRI deployment. Consult the same-run testing-plugin-probes artifact for actual Server process/window/lease evidence; Windows 11 human pickers/IME/DPI/accessibility remain deferred.

The exact eight-file ZIP contains entry, private portable domain, private Windows launcher, static-CRT x64 owned helper, entry deps, manifest and two explicitly synthetic JSON examples. The existing host controls run the SIMULATED virtual.task-form.v1 adapter. The owned Windows driver is proven separately by owned-lab-probes; its host panel is a later acceptance task. It operates only the repository-owned synthetic task form with actual UIA and target-window PrintWindow capture. No SDK/Core/WinUI/.NET/WinRT copies, credentials or business plugin payloads are included. Manual steps are never automatic passes. CSV exports add one apostrophe to every text cell; original project records remain exact.

Native controls also offer explicit Figma read-only imports with per-node manual criteria and selected-only OpenAI authoring using gpt-5.4-mini-2026-03-17. Selected source identities/versions, titles, descriptions and criteria are sent to OpenAI and may incur charges. Generated cases require human review and all generated steps remain manual. Manage figma-token and openai-api-key in the host's protected project credential UI. Unsupported secret storage disables remote actions. Unsubmitted forms last for the current project session; acknowledged records are durable. Remote cancellation waits for actual IO and cancellation callbacks. A partial AI receipt is shown explicitly and never automatically retried or deleted. Synthetic HTTP tests and reference compilation do not exercise the new controls or verify a live provider.

SHA256SUMS covers this README, provenance.json and the ZIP. Provenance binds every exact extracted payload and source file. See docs/testing-plugin.md in the source checkout for controls, lifecycle, limitations and verification commands.
"@ | Set-Content (Join-Path $OutputDirectory 'README.md') -Encoding utf8
        @($zipName, 'provenance.json', 'README.md' | ForEach-Object { (Get-FileHash (Join-Path $OutputDirectory $_) -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $_ }) | Set-Content (Join-Path $OutputDirectory 'SHA256SUMS') -Encoding utf8
    }
    finally { if (Test-Path $stage) { Remove-Item $stage -Recurse -Force } }
    Test-TestingPluginDelivery -Directory $OutputDirectory -AllowReferenceOnly:$ReferenceOnly
}
function Test-TestingPluginDelivery {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Directory, [switch]$AllowReferenceOnly, [switch]$RequireCurrentSource,
        [string]$AppDirectory = (Get-TestingHostAppDirectory))
    $repo = Split-Path $PSScriptRoot -Parent
    $files = @('README.md', 'SHA256SUMS', 'provenance.json', 'workbench-testing-plugin-0.2.0.zip')
    for ($part = [IO.Path]::GetFullPath($Directory); -not [string]::IsNullOrEmpty($part); $part = Split-Path $part -Parent) {
        if (((Get-Item -LiteralPath $part -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'TestingDeliveryLinkRefused' }
    }
    if (@(Compare-Object $files @(Get-ChildItem -LiteralPath $Directory -Force | ForEach-Object Name) -CaseSensitive).Count -ne 0) { throw 'TestingDeliveryFileListInvalid' }
    foreach ($file in Get-ChildItem -LiteralPath $Directory -Force) { if ($file.PSIsContainer -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'TestingDeliveryLinkRefused' } }
    $metadata = Get-Content -LiteralPath (Join-Path $Directory 'provenance.json') -Raw | ConvertFrom-Json -AsHashtable
    if ($metadata.schemaVersion -ne 1 -or $metadata.pluginId -cne 'com.projectworkbench.testing' -or $metadata.version -cne '0.2.0' -or $metadata.minimumHostVersion -cne '0.4.0' -or $metadata.sourceCommit -cnotmatch '^[0-9a-f]{40}$') { throw 'TestingDeliveryIdentityInvalid' }
    if ($metadata.referenceOnly) { throw 'TestingDeliveryReferenceOnly' }
    if (-not $metadata.referenceOnly -and $metadata.sourceTreeDirty) { throw 'TestingDeliveryDirtySource' }
    if (-not $metadata.referenceOnly) {
        $expectedHostCommit = Get-TestingPinnedHostCommit
        if ($metadata.host.sourceCommit -cne $expectedHostCommit -or $metadata.sdk.sourceCommit -cne $expectedHostCommit -or $metadata.sdk.referenceOnly) { throw 'TestingHostPairingMismatch' }
        $actualHost = Get-TestingHostIdentity -AppDirectory $AppDirectory -SourceCommit (Get-TestingPinnedHostCommit)
        foreach ($key in $actualHost.Keys) { if ($metadata.host[$key] -cne $actualHost[$key]) { throw 'TestingHostPairingMismatch' } }
    }
    elseif ($null -ne $metadata.host) { throw 'TestingReferenceHostClaimInvalid' }
    $sums = @(Get-Content -LiteralPath (Join-Path $Directory 'SHA256SUMS'))
    if ($sums.Count -ne 3) { throw 'TestingDeliverySumsInvalid' }
    foreach ($file in @('README.md', 'provenance.json', 'workbench-testing-plugin-0.2.0.zip')) {
        $expected = (Get-FileHash -LiteralPath (Join-Path $Directory $file) -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $file
        if (@($sums | Where-Object { $_ -ceq $expected }).Count -ne 1) { throw 'TestingDeliveryHashMismatch' }
    }
    $zipPath = Join-Path $Directory 'workbench-testing-plugin-0.2.0.zip'
    if ($metadata.zip.path -cne 'workbench-testing-plugin-0.2.0.zip' -or $metadata.zip.length -ne (Get-Item $zipPath).Length -or $metadata.zip.sha256 -cne (Get-FileHash $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()) { throw 'TestingDeliveryZipMismatch' }
    $zip = [IO.Compression.ZipFile]::OpenRead($zipPath)
    $nativePayload = @{}
    try {
        $entries = @($zip.Entries)
        if (@(Compare-Object $script:TestingPayload @($entries | ForEach-Object FullName) -CaseSensitive).Count -ne 0 -or $entries.Count -ne $script:TestingPayload.Count -or @($metadata.payload).Count -ne $script:TestingPayload.Count) { throw 'TestingPayloadFileListInvalid' }
        foreach ($name in $script:TestingPayload) {
            $entry = @($entries | Where-Object FullName -CEQ $name)
            $record = @($metadata.payload | Where-Object path -CEQ $name)
            if ($entry.Count -ne 1 -or $record.Count -ne 1 -or $entry[0].Length -gt 16777216 -or $entry[0].IsEncrypted -or (($entry[0].ExternalAttributes -shr 16) -band 0xf000) -eq 0xa000) { throw 'TestingPayloadInvalid' }
            $stream = $entry[0].Open()
            try { $bytes = [IO.MemoryStream]::new(); $stream.CopyTo($bytes); $actual = $bytes.ToArray(); $bytes.Dispose() } finally { $stream.Dispose() }
            if ([WorkbenchTestingArchiveAudit]::Crc($actual) -ne $entry[0].Crc32) { throw 'TestingPayloadCrcMismatch' }
            if ($record[0].length -ne $actual.Length -or $record[0].sha256 -cne [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($actual)).ToLowerInvariant()) { throw 'TestingPayloadHashMismatch' }
            if ($name -cin @('Workbench.Testing.Windows.dll','Workbench.OwnedLab.exe')) { $nativePayload[$name] = $actual }
            if ($name -ceq 'plugin.json') {
                $manifest = [Text.Encoding]::UTF8.GetString($actual) | ConvertFrom-Json
                if ($manifest.id -cne 'com.projectworkbench.testing' -or $manifest.version -cne '0.2.0' -or $manifest.minimumHostVersion -cne '0.4.0' -or $manifest.apiVersion -ne 1 -or $manifest.entryAssembly -cne 'Workbench.Testing.dll' -or $manifest.entryType -cne 'Workbench.Testing.TestingPlugin') { throw 'TestingManifestInvalid' }
            }
        }
    }
    finally { $zip.Dispose() }
    $helper = Read-TestingOwnedDescriptor $nativePayload['Workbench.Testing.Windows.dll']
    Assert-TestingOwnedHelper -Image $nativePayload['Workbench.OwnedLab.exe'] -Descriptor $helper
    if ($helper.SourceCommit -cne $metadata.sourceCommit) { throw 'TestingOwnedHelperSourceInvalid' }
    foreach ($key in $helper.Keys) { if ($metadata.helper[$key] -cne $helper[$key]) { throw 'TestingOwnedHelperDescriptorInvalid' } }
    Assert-TestingOwnedSource $metadata.nativeBuild $helper
    if ($RequireCurrentSource) {
        Assert-TestingSourceIdentity -RecordedCommit $metadata.sourceCommit -RecordedDirty $metadata.sourceTreeDirty -CurrentCommit (& git -C $repo rev-parse HEAD).Trim() -CurrentDirty (@(& git -C $repo status --porcelain).Count -ne 0)
        Assert-TestingSourceRecords -Current @(Get-TestingSourceFiles $repo) -Recorded @($metadata.sourceFiles)
    }
    Write-Host 'Testing delivery verified: independent eight-file ZIP, exact bytes and manifest.'
}
Export-ModuleMember -Function New-TestingPluginDelivery, Test-TestingPluginDelivery
