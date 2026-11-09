using System.Text.Json;
using Workbench.OwnedLab.WindowsProbe;

if (args is ["--reference-only-check"]) { await NativeChecks.ReferenceOnlyAsync(); return; }
if (args is ["--scenario-policy-check"]) { NativeChecks.ScenarioPolicyChecks(); return; }
if (args is ["--fixture-staging-check"]) { NativeChecks.FixtureStagingChecks(); return; }
if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess) throw new PlatformNotSupportedException("Actual Windows x64 native execution is required.");
if (args.Length != 2) throw new ArgumentException("Pass the retained report directory and exact ZIP SHA256.");
var output = Path.GetFullPath(args[0]);
Directory.CreateDirectory(output);
var checks = new NativeChecks(output, args[1]);
try
{
    await checks.RunAsync();
    await File.WriteAllTextAsync(Path.Combine(output, "report.json"), JsonSerializer.Serialize(new { schema = 1, succeeded = true, checks = checks.Results }, new JsonSerializerOptions { WriteIndented = true }));
}
catch
{
    await File.WriteAllTextAsync(Path.Combine(output, "report.json"), JsonSerializer.Serialize(new { schema = 1, succeeded = false, checks = checks.Results }, new JsonSerializerOptions { WriteIndented = true }));
    throw;
}
