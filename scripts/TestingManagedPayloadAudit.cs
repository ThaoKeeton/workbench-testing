using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

// Reads PE metadata and bounded JSON only. Never loads a payload assembly into this process.
public static class WorkbenchTestingManagedAudit
{
    private const string Target = ".NETCoreApp,Version=v10.0";
    private static readonly string[] PrivateNames = { "Workbench.Testing", "Workbench.Testing.Domain", "Workbench.Testing.Windows", "Workbench.Testing.SourceAcceptance" };
    private sealed class Identity
    {
        public string Name, Culture, Token, FileVersion, InfoVersion, Framework, Machine;
        public Version Version;
        public Dictionary<string, Identity> References = new Dictionary<string, Identity>(StringComparer.OrdinalIgnoreCase);
        public string Hash;
    }
    private static InvalidDataException Fail(string code) => new InvalidDataException(code);
    public static JsonDocument Json(byte[] bytes, string code)
    {
        try
        {
            if (bytes.Length == 0 || bytes.Length > 2097152) throw Fail(code);
            // Validate UTF-8 explicitly; JsonDocument also rejects trailing JSON/comments.
            new UTF8Encoding(false, true).GetString(bytes);
            var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
            try { Unique(doc.RootElement); return doc; } catch { doc.Dispose(); throw; }
        }
        catch { throw Fail(code); }
    }
    private static void Unique(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in value.EnumerateObject()) { if (!names.Add(p.Name)) throw Fail("DuplicateJson"); Unique(p.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var child in value.EnumerateArray()) Unique(child);
    }
    private static string Str(JsonElement obj, string name) => obj.GetProperty(name).GetString();
    private static bool Fields(JsonElement obj, params string[] names) => obj.ValueKind == JsonValueKind.Object && names.ToHashSet(StringComparer.Ordinal).SetEquals(obj.EnumerateObject().Select(p => p.Name));
    public static void ValidateJson(byte[] bytes, string code) { using var doc = Json(bytes, code); }
    public static void Manifest(byte[] bytes, string profile)
    {
        const string code = "TestingManifestInvalid";
        try
        {
            using var doc = Json(bytes, code); var m = doc.RootElement;
            if (!Fields(m, "id", "name", "version", "apiVersion", "minimumHostVersion", "entryAssembly", "entryType", "description", "capabilities")) throw Fail(code);
            bool source = profile == "SourceAcceptance";
            if (!source && profile != "Production") throw Fail(code);
            string name = source ? "Workbench.Testing.SourceAcceptance" : "Workbench.Testing";
            string id = source ? "com.projectworkbench.testing.source-acceptance" : "com.projectworkbench.testing";
            if (Str(m, "id") != id || Str(m, "version") != "0.2.0" || Str(m, "minimumHostVersion") != "0.4.0" ||
                m.GetProperty("apiVersion").ValueKind != JsonValueKind.Number || !m.GetProperty("apiVersion").TryGetInt32(out int api) || api != 1 ||
                Str(m, "entryAssembly") != name + ".dll" || Str(m, "entryType") != name + (source ? ".SourceAcceptancePlugin" : ".TestingPlugin") ||
                string.IsNullOrWhiteSpace(Str(m, "name")) || string.IsNullOrWhiteSpace(Str(m, "description"))) throw Fail(code);
            var expected = new HashSet<string>(new[] { "native-ui", "project-testing", "local-files", "simulated-testing", "remote-figma-read", "openai-case-authoring" }, StringComparer.Ordinal);
            if (source) expected.Add("source-acceptance-fixture");
            var caps = m.GetProperty("capabilities");
            if (caps.ValueKind != JsonValueKind.Array || caps.GetArrayLength() != expected.Count || !expected.SetEquals(caps.EnumerateArray().Select(c => c.GetString()))) throw Fail(code);
        }
        catch { throw Fail(code); }
    }
    private static string Token(byte[] key, bool full)
    {
        if (key.Length == 0) return "";
        return Convert.ToHexString(full ? SHA1.HashData(key).Reverse().Take(8).ToArray() : key).ToLowerInvariant();
    }
    private static string AttributeName(MetadataReader md, CustomAttribute attribute)
    {
        EntityHandle type;
        if (attribute.Constructor.Kind == HandleKind.MemberReference) type = md.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent;
        else if (attribute.Constructor.Kind == HandleKind.MethodDefinition) type = md.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor).GetDeclaringType();
        else return "";
        if (type.Kind == HandleKind.TypeReference) { var t = md.GetTypeReference((TypeReferenceHandle)type); return md.GetString(t.Namespace) + "." + md.GetString(t.Name); }
        if (type.Kind == HandleKind.TypeDefinition) { var t = md.GetTypeDefinition((TypeDefinitionHandle)type); return md.GetString(t.Namespace) + "." + md.GetString(t.Name); }
        return "";
    }
    private static Identity Read(byte[] bytes, string entryType = null, bool requireCode = true)
    {
        try
        {
            using var stream = new MemoryStream(bytes, false); using var pe = new PEReader(stream);
            if (!pe.HasMetadata || pe.PEHeaders.CorHeader == null || requireCode && (pe.PEHeaders.CorHeader.Flags & CorFlags.ILOnly) == 0) throw Fail("TestingManagedPeInvalid");
            var md = pe.GetMetadataReader(); if (!md.IsAssembly) throw Fail("TestingManagedPeInvalid");
            var a = md.GetAssemblyDefinition();
            var info = new Identity { Name = md.GetString(a.Name), Version = a.Version, Culture = md.GetString(a.Culture), Token = Token(md.GetBlobBytes(a.PublicKey), true), Machine = pe.PEHeaders.CoffHeader.Machine.ToString(), Hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() };
            bool code = false;
            if (requireCode) foreach (var h in md.MethodDefinitions) { var m = md.GetMethodDefinition(h); if (m.RelativeVirtualAddress != 0) { pe.GetMethodBody(m.RelativeVirtualAddress); code = true; } }
            foreach (var h in a.GetCustomAttributes())
            {
                var attr = md.GetCustomAttribute(h); string type = AttributeName(md, attr);
                if (type == "System.Runtime.CompilerServices.ReferenceAssemblyAttribute") throw Fail("TestingManagedReferenceAssembly");
                if (type == "System.Reflection.AssemblyFileVersionAttribute" || type == "System.Reflection.AssemblyInformationalVersionAttribute" || type == "System.Runtime.Versioning.TargetFrameworkAttribute")
                {
                    var blob = md.GetBlobReader(attr.Value); if (blob.ReadUInt16() != 1) throw Fail("TestingManagedPeInvalid"); string text = blob.ReadSerializedString();
                    if (type.EndsWith(".AssemblyFileVersionAttribute", StringComparison.Ordinal)) info.FileVersion = text;
                    else if (type.EndsWith(".AssemblyInformationalVersionAttribute", StringComparison.Ordinal)) info.InfoVersion = text;
                    else info.Framework = text;
                }
            }
            if (!code && requireCode) throw Fail("TestingManagedPeInvalid");
            foreach (var h in md.AssemblyReferences)
            {
                var r = md.GetAssemblyReference(h); string n = md.GetString(r.Name);
                if (!info.References.TryAdd(n, new Identity { Name = n, Version = r.Version, Culture = md.GetString(r.Culture), Token = Token(md.GetBlobBytes(r.PublicKeyOrToken), (r.Flags & AssemblyFlags.PublicKey) != 0) })) throw Fail("TestingManagedIdentityInvalid");
            }
            if (!string.IsNullOrEmpty(entryType))
            {
                bool found = false;
                foreach (var h in md.TypeDefinitions)
                {
                    var t = md.GetTypeDefinition(h);
                    if (md.GetString(t.Namespace) + "." + md.GetString(t.Name) != entryType) continue;
                    bool concretePublic = (t.Attributes & TypeAttributes.VisibilityMask) == TypeAttributes.Public && (t.Attributes & (TypeAttributes.Abstract | TypeAttributes.Interface)) == 0 && t.GetGenericParameters().Count == 0;
                    bool contract = false, constructor = false;
                    foreach (var ih in t.GetInterfaceImplementations())
                    {
                        var ihType = md.GetInterfaceImplementation(ih).Interface;
                        if (ihType.Kind != HandleKind.TypeReference) continue;
                        var tr = md.GetTypeReference((TypeReferenceHandle)ihType);
                        if (md.GetString(tr.Name) == "IWorkbenchPlugin" && md.GetString(tr.Namespace) == "Workbench.Sdk" && tr.ResolutionScope.Kind == HandleKind.AssemblyReference && md.GetString(md.GetAssemblyReference((AssemblyReferenceHandle)tr.ResolutionScope).Name) == "Workbench.Sdk") contract = true;
                    }
                    foreach (var mh in t.GetMethods())
                    {
                        var m = md.GetMethodDefinition(mh);
                        if (md.GetString(m.Name) != ".ctor" || (m.Attributes & MethodAttributes.MemberAccessMask) != MethodAttributes.Public || (m.Attributes & MethodAttributes.Static) != 0 || m.RelativeVirtualAddress == 0) continue;
                        var sig = md.GetBlobReader(m.Signature); var header = sig.ReadSignatureHeader();
                        if (header.IsGeneric) sig.ReadCompressedInteger();
                        if (sig.ReadCompressedInteger() == 0 && sig.ReadByte() == 1) constructor = true;
                    }
                    found = concretePublic && contract && constructor;
                }
                if (!found) throw Fail("TestingManagedEntryInvalid");
            }
            return info;
        }
        catch (InvalidDataException) { throw; }
        catch { throw Fail("TestingManagedPeInvalid"); }
    }
    private static Identity Checked(byte[] bytes, string name, string commit, bool portable, string entryType)
    {
        // Read reference markers before identity/architecture checks, including real compiler ref assemblies.
        var i = Read(bytes);
        if (i.Name != name || i.Version.ToString() != "1.0.0.0" || i.FileVersion != "0.2.0.0" || i.InfoVersion != "0.2.0+" + commit || i.Culture != "" || i.Token != "" || i.Framework != Target) throw Fail("TestingManagedIdentityInvalid");
        using var pe = new PEReader(new MemoryStream(bytes, false));
        var flags = pe.PEHeaders.CorHeader.Flags;
        if (portable ? i.Machine != "I386" || pe.PEHeaders.PEHeader.Magic != PEMagic.PE32 || (flags & (CorFlags.Requires32Bit | CorFlags.Prefers32Bit)) != 0 : i.Machine != "Amd64" || pe.PEHeaders.PEHeader.Magic != PEMagic.PE32Plus) throw Fail("TestingManagedArchitectureInvalid");
        if (!string.IsNullOrEmpty(entryType)) Read(bytes, entryType);
        return i;
    }
    private static object Facts(Identity i) => new { name = i.Name, assemblyVersion = i.Version.ToString(), fileVersion = i.FileVersion, informationalVersion = i.InfoVersion, machine = i.Machine, sha256 = i.Hash, references = i.References.Values.OrderBy(r => r.Name, StringComparer.Ordinal).Select(r => new { name = r.Name, version = r.Version.ToString(), token = r.Token, culture = r.Culture }).ToArray() };
    public static string Inspect(byte[] bytes, string name, string commit, bool portable, string entryType) => JsonSerializer.Serialize(Facts(Checked(bytes, name, commit, portable, entryType)));
    private static bool Same(Identity a, Identity b) => a.Name == b.Name && a.Version == b.Version && a.Token == b.Token && a.Culture == b.Culture;
    private static void Regular(string path)
    {
        for (var p = Path.GetFullPath(path); !string.IsNullOrEmpty(p); p = Path.GetDirectoryName(p))
            if ((File.Exists(p) || Directory.Exists(p)) && (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0) throw Fail("TestingDeliveryLinkRefused");
        if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.Directory) != 0) throw Fail("TestingManagedHostBindingInvalid");
    }
    private static byte[] ReadHost(string directory, string filename) { var p = Path.Combine(directory, filename); Regular(p); return File.ReadAllBytes(p); }
    private static HashSet<string> Frameworks(byte[] core)
    {
        try
        {
            using var pe = new PEReader(new MemoryStream(core, false)); var md = pe.GetMetadataReader();
            foreach (var h in md.ManifestResources)
            {
                var r = md.GetManifestResource(h); if (md.GetString(r.Name) != "Workbench.Core.FrameworkAssemblies") continue;
                if (!r.Implementation.IsNil) break;
                var d = pe.PEHeaders.CorHeader.ResourcesDirectory; var b = pe.GetSectionData(d.RelativeVirtualAddress).GetReader(checked((int)r.Offset), d.Size - checked((int)r.Offset));
                int n = b.ReadInt32(); if (n < 1 || n > 65536 || n > b.RemainingBytes) break;
                var names = new UTF8Encoding(false, true).GetString(b.ReadBytes(n)).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
                names.Add("System.Private.CoreLib"); return names;
            }
        }
        catch { }
        throw Fail("TestingManagedHostBindingInvalid");
    }
    private static bool Shared(string name) => name == "Workbench.Sdk" || name == "Workbench.WinUI.Sdk" || name == "Microsoft.WinUI" || name == "WinRT.Runtime" || name == "Microsoft.Windows.SDK.NET" || name == "Microsoft.InteractiveExperiences.Projection" || name.StartsWith("Microsoft.Windows.", StringComparison.Ordinal) && name.EndsWith(".Projection", StringComparison.Ordinal) || name.StartsWith("Microsoft.WindowsAppRuntime.", StringComparison.Ordinal);
    private static void ExternalAsset(string library, string group, JsonProperty asset, JsonElement hostTargets, Dictionary<string, Identity> bindings, bool managed)
    {
        string assetName = Path.GetFileNameWithoutExtension(asset.Name.Replace('\\', '/'));
        if (asset.Value.ValueKind != JsonValueKind.Object || assetName.StartsWith("Workbench.", StringComparison.OrdinalIgnoreCase) && !Shared(assetName) || asset.Name.Split('/').Any(p => p == ".." || p == ".") || asset.Name.StartsWith("/", StringComparison.Ordinal) || asset.Name.Contains('\\')) throw Fail("TestingManagedDepsInvalid");
        // Managed declarations must match the actual host. Unselected native RIDs can
        // be omitted from its published deps and are never copied into the plugin.
        if (managed && !hostTargets.EnumerateObject().Any(ht => ht.Value.TryGetProperty(library, out var hn) && hn.TryGetProperty(group, out var hr) && hr.TryGetProperty(asset.Name, out var ha) && JsonElement.DeepEquals(ha, asset.Value))) throw Fail("TestingManagedDepsInvalid");
        // Only managed references consumed by private IL need an assembly identity binding.
        if (managed && bindings.TryGetValue(assetName, out var host) && asset.Value.TryGetProperty("assemblyVersion", out var av) && av.GetString() != host.Version.ToString()) throw Fail("TestingManagedDepsInvalid");
    }
    private static bool ManagedRuntimeTarget(JsonProperty asset)
    {
        if (asset.Value.ValueKind != JsonValueKind.Object) throw Fail("TestingManagedDepsInvalid");
        string rid = Str(asset.Value, "rid"), kind = Str(asset.Value, "assetType");
        if (string.IsNullOrEmpty(rid) || rid.Any(c => !(c >= 'a' && c <= 'z' || c >= '0' && c <= '9' || c == '-' || c == '.')) || kind != "runtime" && kind != "native") throw Fail("TestingManagedDepsInvalid");
        var parts = asset.Name.Split('/'); bool managed = kind == "runtime";
        if (parts.Length < (managed ? 5 : 4) || parts[0] != "runtimes" || parts[1] != rid || parts[2] != (managed ? "lib" : "native") || parts.Any(p => p.Length == 0 || p == "." || p == "..") || managed && !asset.Name.EndsWith(".dll", StringComparison.Ordinal)) throw Fail("TestingManagedDepsInvalid");
        foreach (var property in asset.Value.EnumerateObject())
        {
            if (property.Name == "rid" || property.Name == "assetType") continue;
            if (property.Name != "fileVersion" && (!managed || property.Name != "assemblyVersion") || property.Value.ValueKind != JsonValueKind.String || !Version.TryParse(property.Value.GetString(), out _)) throw Fail("TestingManagedDepsInvalid");
        }
        return managed;
    }
    public static string Audit(IDictionary payload, string hostDirectory, string commit, string profile)
        => Audit(payload, hostDirectory, commit, commit, profile);
    public static string Audit(IDictionary payload, string hostDirectory, string commit, string hostCommit, string profile)
    {
        bool source = profile == "SourceAcceptance"; if (!source && profile != "Production") throw Fail("TestingManagedPayloadInvalid");
        var names = PrivateNames.Take(source ? 4 : 3).ToArray();
        var files = names.Select(n => n + ".dll").ToHashSet(StringComparer.Ordinal);
        if (!files.SetEquals(payload.Keys.Cast<string>().Where(n => n.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))) || files.Any(n => !(payload[n] is byte[]))) throw Fail("TestingManagedPayloadInvalid");
        var identities = names.ToDictionary(n => n, n => Checked((byte[])payload[n + ".dll"], n, commit, n == "Workbench.Testing.Domain", n == "Workbench.Testing" ? "Workbench.Testing.TestingPlugin" : n == "Workbench.Testing.SourceAcceptance" ? n + ".SourceAcceptancePlugin" : null), StringComparer.OrdinalIgnoreCase);
        var framework = Frameworks(ReadHost(hostDirectory, "Workbench.Core.dll"));
        var bindings = new Dictionary<string, Identity>(StringComparer.OrdinalIgnoreCase);
        foreach (var i in identities.Values)
        foreach (var r in i.References.Values)
        {
            if (identities.TryGetValue(r.Name, out var own)) { if (!Same(r, own)) throw Fail("TestingManagedIdentityInvalid"); continue; }
            if (!Shared(r.Name) && !framework.Contains(r.Name)) throw Fail("TestingManagedHostBindingInvalid");
            Identity host;
            try { host = Read(ReadHost(hostDirectory, r.Name + ".dll"), null, false); }
            catch (InvalidDataException e) when (e.Message != "TestingDeliveryLinkRefused") { throw Fail("TestingManagedHostBindingInvalid"); }
            if (!Same(r, host) || r.Name.StartsWith("Workbench.", StringComparison.Ordinal) && (host.FileVersion != "0.4.0.0" || host.InfoVersion != "0.4.0+" + hostCommit)) throw Fail("TestingManagedHostBindingInvalid");
            bindings[r.Name] = host;
        }
        var hostDepsBytes = ReadHost(hostDirectory, "Workbench.App.deps.json");
        using var hostDeps = Json(hostDepsBytes, "TestingManagedHostBindingInvalid");
        JsonElement hostLibraries, hostTargets;
        try { hostLibraries = hostDeps.RootElement.GetProperty("libraries"); hostTargets = hostDeps.RootElement.GetProperty("targets"); if (hostLibraries.ValueKind != JsonValueKind.Object || hostTargets.ValueKind != JsonValueKind.Object) throw Fail("TestingManagedHostBindingInvalid"); }
        catch { throw Fail("TestingManagedHostBindingInvalid"); }
        var depsFacts = new List<object>();
        foreach (string root in source ? new[] { "Workbench.Testing", "Workbench.Testing.SourceAcceptance" } : new[] { "Workbench.Testing" })
        {
            string file = root + ".deps.json"; if (!(payload[file] is byte[] data)) throw Fail("TestingManagedDepsInvalid");
            using var doc = Json(data, "TestingDepsJsonInvalid");
            try
            {
                var d = doc.RootElement; var targets = d.GetProperty("targets"); var libraries = d.GetProperty("libraries");
                if (Str(d.GetProperty("runtimeTarget"), "name") != Target || targets.EnumerateObject().Count() != 1 || !targets.TryGetProperty(Target, out var target) || !libraries.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal).SetEquals(target.EnumerateObject().Select(p => p.Name))) throw Fail("TestingManagedDepsInvalid");
                var expectedPrivate = identities.Keys.Where(n => n != "Workbench.Testing.SourceAcceptance" || root == n).ToHashSet(StringComparer.Ordinal);
                if (!expectedPrivate.Select(n => n + "/0.2.0").ToHashSet(StringComparer.Ordinal).SetEquals(libraries.EnumerateObject().Where(p => p.Name.StartsWith("Workbench.Testing", StringComparison.Ordinal)).Select(p => p.Name))) throw Fail("TestingManagedDepsInvalid");
                foreach (var lib in libraries.EnumerateObject())
                {
                    int slash = lib.Name.LastIndexOf('/'); if (slash <= 0) throw Fail("TestingManagedDepsInvalid"); string name = lib.Name.Substring(0, slash);
                    var node = target.GetProperty(lib.Name);
                    if (node.TryGetProperty("dependencies", out var dependencies))
                        foreach (var edge in dependencies.EnumerateObject()) if (!libraries.TryGetProperty(edge.Name + "/" + edge.Value.GetString(), out _)) throw Fail("TestingManagedDepsInvalid");
                    if (expectedPrivate.Contains(name))
                    {
                        if (Str(lib.Value, "type") != "project" || !node.TryGetProperty("runtime", out var runtime) || !Fields(runtime, name + ".dll")) throw Fail("TestingManagedDepsInvalid");
                        var privateAsset = runtime.GetProperty(name + ".dll");
                        if (privateAsset.ValueKind != JsonValueKind.Object || privateAsset.EnumerateObject().Any(p => p.Name != "assemblyVersion" && p.Name != "fileVersion") || privateAsset.TryGetProperty("assemblyVersion", out var version) && version.GetString() != identities[name].Version.ToString() || privateAsset.TryGetProperty("fileVersion", out var fileVersion) && fileVersion.GetString() != identities[name].FileVersion) throw Fail("TestingManagedDepsInvalid");
                        if (node.EnumerateObject().Any(p => p.Name != "dependencies" && p.Name != "runtime")) throw Fail("TestingManagedDepsInvalid");
                        var actualPrivateEdges = identities[name].References.Keys.Where(expectedPrivate.Contains).ToHashSet(StringComparer.Ordinal);
                        var declaredPrivateEdges = node.TryGetProperty("dependencies", out var edges) ? edges.EnumerateObject().Where(p => expectedPrivate.Contains(p.Name)).Select(p => p.Name).ToHashSet(StringComparer.Ordinal) : new HashSet<string>(StringComparer.Ordinal);
                        // The fixed project graph and the IL closure are checked independently. A fixture's transitive
                        // IL references can resolve through Testing; losing a direct project edge is still invalid.
                        string[] graphEdges = name == "Workbench.Testing.SourceAcceptance" ? new[] { "Workbench.Testing" } : name == "Workbench.Testing" ? new[] { "Workbench.Testing.Domain", "Workbench.Testing.Windows" } : name == "Workbench.Testing.Windows" ? new[] { "Workbench.Testing.Domain" } : Array.Empty<string>();
                        if (!declaredPrivateEdges.SetEquals(graphEdges) || !actualPrivateEdges.IsSubsetOf(Reachable(name + "/0.2.0", target, expectedPrivate))) throw Fail("TestingManagedDepsInvalid");
                    }
                    else
                    {
                        if (!hostLibraries.TryGetProperty(lib.Name, out var hostLib)) throw Fail("TestingManagedDepsInvalid");
                        bool hostSdkPackage = (name == "Workbench.Sdk" || name == "Workbench.WinUI.Sdk") &&
                            Str(hostLib, "type") == "project" && Str(lib.Value, "type") == "package";
                        if (!hostSdkPackage && Str(hostLib, "type") != Str(lib.Value, "type")) throw Fail("TestingManagedDepsInvalid");
                        if (hostSdkPackage)
                        {
                            string expectedPath = name == "Workbench.Sdk" ? "lib/net10.0/Workbench.Sdk.dll" : "lib/net10.0-windows10.0.19041/Workbench.WinUI.Sdk.dll";
                            if (!node.TryGetProperty("runtime", out var sdkRuntime) || !Fields(sdkRuntime, expectedPath) ||
                                node.EnumerateObject().Any(p => p.Name != "dependencies" && p.Name != "runtime") || !bindings.ContainsKey(name) ||
                                !hostTargets.EnumerateObject().Any(ht => ht.Value.TryGetProperty(lib.Name, out var hn) && hn.TryGetProperty("runtime", out var hr) &&
                                    hr.TryGetProperty(name + ".dll", out var ha) && JsonElement.DeepEquals(ha, sdkRuntime.GetProperty(expectedPath))))
                                throw Fail("TestingManagedDepsInvalid");
                        }
                        else
                        {
                            if (node.TryGetProperty("runtime", out var runtime)) foreach (var asset in runtime.EnumerateObject())
                                ExternalAsset(lib.Name, "runtime", asset, hostTargets, bindings, true);
                            if (node.TryGetProperty("runtimeTargets", out var runtimeTargets)) foreach (var asset in runtimeTargets.EnumerateObject())
                                ExternalAsset(lib.Name, "runtimeTargets", asset, hostTargets, bindings, ManagedRuntimeTarget(asset));
                        }
                    }
                }
                depsFacts.Add(new { path = file, root = root + "/0.2.0", target = Target, sha256 = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant() });
            }
            catch (InvalidDataException) { throw; }
            catch { throw Fail("TestingManagedDepsInvalid"); }
        }
        return JsonSerializer.Serialize(new { schemaVersion = 1, assemblies = identities.Values.OrderBy(i => i.Name, StringComparer.Ordinal).Select(Facts).ToArray(), deps = depsFacts, hostBindings = bindings.Values.OrderBy(i => i.Name, StringComparer.Ordinal).Select(Facts).ToArray(), hostDepsSha256 = Convert.ToHexString(SHA256.HashData(hostDepsBytes)).ToLowerInvariant(), frameworkCatalogSourceSha256 = Convert.ToHexString(SHA256.HashData(ReadHost(hostDirectory, "Workbench.Core.dll"))).ToLowerInvariant() });
    }
    private static HashSet<string> Reachable(string root, JsonElement target, HashSet<string> names)
    {
        var found = new HashSet<string>(StringComparer.Ordinal); var visited = new HashSet<string>(StringComparer.Ordinal); var queue = new Queue<string>(); queue.Enqueue(root);
        while (queue.Count != 0)
        {
            string key = queue.Dequeue(); if (!visited.Add(key)) continue;
            if (!target.TryGetProperty(key, out var node) || !node.TryGetProperty("dependencies", out var deps)) continue;
            foreach (var edge in deps.EnumerateObject()) { if (names.Contains(edge.Name)) found.Add(edge.Name); queue.Enqueue(edge.Name + "/" + edge.Value.GetString()); }
        }
        return found;
    }
}
