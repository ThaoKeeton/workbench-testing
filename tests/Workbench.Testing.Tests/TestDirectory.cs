using System.Text.Json;
using Workbench.Core;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Workbench.Core.Tests;

internal sealed class TestDirectory : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "workbench-tests", Guid.NewGuid().ToString("N"));
    public string Plugins => Path.Combine(Root, "plugins");
    public string User => Path.Combine(Root, "user");

    public TestDirectory() => Directory.CreateDirectory(Plugins);

    public PluginManager Manager(params string[] additionalDiscoveryRoots) => new(new PluginManagerOptions
    {
        PluginsRoot = Plugins,
        UserRoot = User,
        AdditionalDiscoveryRoots = additionalDiscoveryRoots,
        HostVersion = new Version(0, 1, 0)
    });

    public string Package(string id, string? folder = null, Dictionary<string, object?>? fields = null, bool external = false)
    {
        var path = Path.Combine(external ? Path.Combine(Root, "sources") : Plugins, folder ?? id);
        Directory.CreateDirectory(path);
        foreach (var file in Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "fixtures")))
            File.Copy(file, Path.Combine(path, Path.GetFileName(file)));
        var manifest = new Dictionary<string, object?>
        {
            ["id"] = id,
            ["name"] = "Fixture plugin",
            ["version"] = "1.0.0",
            ["apiVersion"] = 1,
            ["minimumHostVersion"] = "0.1.0",
            ["entryAssembly"] = "Workbench.TestPlugin.dll",
            ["entryType"] = "Workbench.TestPlugin.FixturePlugin",
            ["description"] = "Real dynamically loaded test fixture",
            ["capabilities"] = new[] { "views", "commands", "settings" }
        };
        if (fields is not null)
            foreach (var field in fields)
                manifest[field.Key] = field.Value;
        File.WriteAllText(Path.Combine(path, "plugin.json"), JsonSerializer.Serialize(manifest));
        return path;
    }

    public ActivationMarker WatchActivation() => new(Path.Combine(Root, "activated.marker"));

    public void Dispose() => RemoveOwnedDirectory(Root);

    private static bool RemoveOwnedDirectory(string directory)
    {
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
        {
            Directory.Delete(directory); // The owned root itself may have been replaced by a link.
            return false;
        }
        var retainedMappedFile = false;
        foreach (var path in Directory.EnumerateFileSystemEntries(directory))
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                // Remove the link itself, never recurse into its target.
                if ((attributes & FileAttributes.Directory) != 0) Directory.Delete(path);
                else File.Delete(path);
            }
            else if ((attributes & FileAttributes.Directory) != 0)
                retainedMappedFile |= RemoveOwnedDirectory(path);
            else
            {
                try { File.Delete(path); }
                catch (Exception error) when ((error is IOException or UnauthorizedAccessException) && IsMappedFixture(path))
                {
                    // Noncollectible plugin DLLs stay mapped until the test process exits.
                    // Leave only these owned files/ancestors; do not hide unrelated IO failures.
                    retainedMappedFile = true;
                }
            }
        }
        if (!retainedMappedFile) Directory.Delete(directory);
        return retainedMappedFile;
    }

    private static bool IsMappedFixture(string path) => OperatingSystem.IsWindows()
        && (Path.GetFileName(path) is "Workbench.TestPlugin.dll" or "Workbench.TestDependency.dll")
        && AppDomain.CurrentDomain.GetAssemblies().Any(assembly => !assembly.IsDynamic
            && string.Equals(assembly.Location, path, StringComparison.OrdinalIgnoreCase));
}

internal sealed class ActivationMarker : IDisposable
{
    private const string Variable = "WORKBENCH_TEST_ACTIVATION_MARKER";
    private readonly string? _previous = Environment.GetEnvironmentVariable(Variable);
    public string Path { get; }

    public ActivationMarker(string path)
    {
        Path = path;
        Environment.SetEnvironmentVariable(Variable, path);
    }

    public void Dispose() => Environment.SetEnvironmentVariable(Variable, _previous);
}
