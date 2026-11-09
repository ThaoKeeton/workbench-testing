using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Workbench.Core;

// Parse all arguments and the new-report guard before creating any owned probe root.
if (args.Length < 1 || args[0].StartsWith("--", StringComparison.Ordinal))
    throw new ArgumentException("Pass <zip> [--profile Production|SourceAcceptance] [--report <new-file>].");
var profile = "Production";
string? reportPath = null;
var seen = new HashSet<string>(StringComparer.Ordinal);
for (var i = 1; i < args.Length; i += 2)
{
    if (args[i] is not ("--profile" or "--report") || !seen.Add(args[i]) || i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
        throw new ArgumentException("Unknown, missing or duplicate package-probe argument.");
    if (args[i] == "--profile")
    {
        if (args[i + 1] is not ("Production" or "SourceAcceptance")) throw new ArgumentException("Unknown package profile.");
        profile = args[i + 1];
    }
    else reportPath = Path.GetFullPath(args[i + 1]);
}
var zipPath = Path.GetFullPath(args[0]);
RejectLinks(zipPath);
if (reportPath is not null)
{
    RejectLinks(reportPath);
    if (File.Exists(reportPath) || Directory.Exists(reportPath)) throw new IOException("Package probe report already exists.");
    var delivery = Path.GetDirectoryName(zipPath)! + Path.DirectorySeparatorChar;
    if (reportPath.StartsWith(delivery, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Package probe report must be outside the delivery directory.");
}
var sourceProfile = profile == "SourceAcceptance";
var pluginId = sourceProfile ? "com.projectworkbench.testing.source-acceptance" : "com.projectworkbench.testing";
var entryName = sourceProfile ? "Workbench.Testing.SourceAcceptance" : "Workbench.Testing";
string[] common = ["Fixtures/figma.synthetic.json", "Fixtures/requirements.synthetic.json", "Workbench.Testing.dll", "Workbench.Testing.Domain.dll", "Workbench.Testing.Windows.dll", "Workbench.OwnedLab.exe", "Workbench.Testing.deps.json", "plugin.json"];
string[] expectedPayload = sourceProfile ? [.. common, "Workbench.Testing.SourceAcceptance.dll", "Workbench.Testing.SourceAcceptance.deps.json"] : common;
Dictionary<string, byte[]> payload = new(StringComparer.Ordinal);
using (var archive = ZipFile.OpenRead(zipPath))
{
    if (archive.Entries.Count != expectedPayload.Length || !expectedPayload.ToHashSet(StringComparer.Ordinal).SetEquals(archive.Entries.Select(e => e.FullName)))
        throw new InvalidDataException("Actual ZIP differs from the exact selected profile inventory.");
    foreach (var entry in archive.Entries)
    {
        if (entry.Length is < 1 or > 16777216 || entry.IsEncrypted || ((entry.ExternalAttributes >> 16) & 0xf000) is not (0 or 0x8000) ||
            (entry.ExternalAttributes & (int)(FileAttributes.ReparsePoint | FileAttributes.Device | FileAttributes.Directory)) != 0)
            throw new InvalidDataException("Actual ZIP contains a nonregular or oversized payload.");
        using var input = entry.Open(); using var copy = new MemoryStream();
        var buffer = new byte[65536]; int read;
        while ((read = input.Read(buffer)) > 0)
        {
            if (copy.Length + read > entry.Length) throw new InvalidDataException("Actual ZIP payload length differs.");
            copy.Write(buffer, 0, read);
        }
        if (copy.Length != entry.Length) throw new InvalidDataException("Actual ZIP payload length differs.");
        payload.Add(entry.FullName, copy.ToArray());
    }
}
if (payload["plugin.json"].Length > 2097152) throw new InvalidDataException("Actual manifest exceeds its bound.");
using (var manifest = JsonDocument.Parse(payload["plugin.json"], new() { MaxDepth = 32 }))
{
    var m = manifest.RootElement;
    string[] fields = ["id", "name", "version", "apiVersion", "minimumHostVersion", "entryAssembly", "entryType", "description", "capabilities"];
    var properties = m.EnumerateObject().ToArray();
    string[] capabilities = ["native-ui", "project-testing", "local-files", "simulated-testing", "remote-figma-read", "openai-case-authoring", .. sourceProfile ? new[] { "source-acceptance-fixture" } : Array.Empty<string>()];
    if (properties.Length != fields.Length || !fields.ToHashSet(StringComparer.Ordinal).SetEquals(properties.Select(p => p.Name)) ||
        properties.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != fields.Length ||
        m.GetProperty("id").GetString() != pluginId || m.GetProperty("version").GetString() != "0.2.0" || m.GetProperty("minimumHostVersion").GetString() != "0.4.0" ||
        m.GetProperty("apiVersion").GetInt32() != 1 || m.GetProperty("entryAssembly").GetString() != entryName + ".dll" ||
        m.GetProperty("entryType").GetString() != entryName + (sourceProfile ? ".SourceAcceptancePlugin" : ".TestingPlugin") ||
        m.GetProperty("capabilities").GetArrayLength() != capabilities.Length || !capabilities.ToHashSet(StringComparer.Ordinal).SetEquals(m.GetProperty("capabilities").EnumerateArray().Select(e => e.GetString()!)))
        throw new InvalidDataException("Actual ZIP manifest differs from the selected profile.");
}
var root = Path.Combine(Path.GetTempPath(), "ProjectWorkbench-testing-package-install", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var checks = new List<string>();
await Refused(zipPath, "original-host-0.3", new(0, 3, 0));
checks.Add("original-host-0.3-refused");
var userRoot = Path.Combine(root, "host-0.4");
var options = Options(userRoot, new(0, 4, 0));
var packages = new PluginPackageStore(options);
if (!(await packages.InstallAsync(zipPath)).Succeeded) throw new InvalidOperationException("Actual selected ZIP installation at host 0.4 failed.");
var destination = Path.Combine(options.PluginsRoot, pluginId);
VerifyInstalled();
checks.Add("host-0.4-installed-byte-identical");
await using (var manager = new PluginManager(options))
{
    await manager.DiscoverAsync();
    var plugin = manager.Plugins.Single();
    if (plugin.Manifest is not { Version: "0.2.0", MinimumHostVersion: "0.4.0", ApiVersion: 1 } || plugin.Manifest.Id != pluginId ||
        plugin.Manifest.EntryAssembly != entryName + ".dll" || plugin.Manifest.EntryType != entryName + (sourceProfile ? ".SourceAcceptancePlugin" : ".TestingPlugin") ||
        plugin.State != PluginState.Disabled || plugin.IsLoaded || plugin.IsTrusted || plugin.DesiredEnabled)
        throw new InvalidOperationException("Discovery executed/trusted the plugin or changed its selected identity.");
    checks.Add("discovery-disabled-untrusted-unloaded");
    if ((await packages.InstallAsync(zipPath)).Succeeded) throw new InvalidOperationException("Duplicate install overwrote the actual package.");
    VerifyInstalled();
    // The earlier snapshot is immutable; rediscover through a fresh manager after the refused mutation.
    await using var rediscovered = new PluginManager(options);
    await rediscovered.DiscoverAsync();
    var afterDuplicate = rediscovered.Plugins.Single();
    if (afterDuplicate.Manifest?.Id != pluginId || afterDuplicate.State != PluginState.Disabled || afterDuplicate.IsLoaded || afterDuplicate.IsTrusted || afterDuplicate.DesiredEnabled)
        throw new InvalidOperationException("Duplicate install changed freshly discovered state.");
    checks.Add("duplicate-refused-bytes-unchanged");
}
await Refused(Mutate("wrong-api", m => m["apiVersion"] = 2), "wrong-api", new(0, 4, 0));
checks.Add("wrong-api-refused");
await Refused(Mutate("above-host-minimum", m => m["minimumHostVersion"] = "0.4.1"), "above-host-minimum", new(0, 4, 0));
checks.Add("above-host-minimum-refused");
var zipHash = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(zipPath)));
var receipt = JsonSerializer.SerializeToUtf8Bytes(new
{
    schemaVersion = 1, profile, pluginId, version = "0.2.0", apiVersion = 1, minimumHostVersion = "0.4.0", zipSha256 = zipHash,
    activationAttempted = false, probeRoot = root, checks,
    installedPayload = expectedPayload.Order(StringComparer.Ordinal).Select(name => new { path = name, length = payload[name].Length, sha256 = Convert.ToHexStringLower(SHA256.HashData(payload[name])) }).ToArray()
}, new JsonSerializerOptions { WriteIndented = true });
if (receipt.Length > 65536) throw new InvalidDataException("Package probe receipt exceeds its bound.");
if (reportPath is not null)
{
    RejectLinks(reportPath);
    Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
    using var output = new FileStream(reportPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
    output.Write(receipt); output.Flush(flushToDisk: true);
}
Console.WriteLine($"Actual {profile} {expectedPayload.Length}-file ZIP installed byte-identically; disabled/untrusted/unloaded discovery, duplicate retention, original host 0.3, wrong API and above-host minimum refusals passed. SHA256={zipHash}. No activation was attempted.");
Console.WriteLine(System.Text.Encoding.UTF8.GetString(receipt));

PluginManagerOptions Options(string directory, Version version) => new() { UserRoot = directory, PluginsRoot = Path.Combine(directory, "Plugins"), HostVersion = version };
async Task Refused(string path, string label, Version version)
{
    var rejectedOptions = Options(Path.Combine(root, label), version);
    var rejected = new PluginPackageStore(rejectedOptions);
    if ((await rejected.InstallAsync(path)).Succeeded || Directory.Exists(Path.Combine(rejectedOptions.PluginsRoot, pluginId)))
        throw new InvalidOperationException($"Package compatibility refusal failed: {label}.");
}
void VerifyInstalled()
{
    RejectLinks(destination);
    var actualFiles = Directory.EnumerateFiles(destination, "*", SearchOption.AllDirectories)
        .Select(p => Path.GetRelativePath(destination, p).Replace(Path.DirectorySeparatorChar, '/')).ToArray();
    if (actualFiles.Length != expectedPayload.Length || !expectedPayload.ToHashSet(StringComparer.Ordinal).SetEquals(actualFiles))
        throw new InvalidDataException("Installed inventory differs from the exact selected ZIP.");
    foreach (var name in expectedPayload)
    {
        var path = Path.Combine(destination, name); RejectLinks(path);
        if (!payload[name].AsSpan().SequenceEqual(File.ReadAllBytes(path))) throw new InvalidDataException("Installed payload bytes differ from the actual ZIP.");
    }
}
string Mutate(string label, Action<JsonObject> change)
{
    var mutatedPath = Path.Combine(root, label + ".zip");
    var manifest = JsonNode.Parse(payload["plugin.json"])!.AsObject(); change(manifest);
    using var archive = ZipFile.Open(mutatedPath, ZipArchiveMode.Create);
    foreach (var name in expectedPayload)
    {
        using var output = archive.CreateEntry(name).Open();
        output.Write(name == "plugin.json" ? JsonSerializer.SerializeToUtf8Bytes(manifest) : payload[name]);
    }
    return mutatedPath;
}
static void RejectLinks(string path)
{
    for (var p = Path.GetFullPath(path); !string.IsNullOrEmpty(p); p = Path.GetDirectoryName(p))
        if ((File.Exists(p) || Directory.Exists(p)) && (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0) throw new IOException("Package probe refuses linked paths.");
}
