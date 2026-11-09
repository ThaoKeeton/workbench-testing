using Microsoft.Windows.Storage.Pickers;
using Workbench.Sdk;

namespace Workbench.Testing;

internal sealed partial class TestingPage
{
    private Microsoft.UI.WindowId HostWindowId => XamlRoot?.ContentIslandEnvironment.AppWindowId
        ?? throw new InvalidOperationException("Attach this page to the host window before picking a file.");
    private async Task PickImportAsync(TestSourceKind kind)
    {
        var picker = new FileOpenPicker(HostWindowId);
        picker.FileTypeFilter.Add(".json");
        // Await the ORIGINAL operation. A cancelled wrapper must never release a still-open native picker.
        var result = await picker.PickSingleFileAsync();
        if (result is null) return;
        await _plugin.ImportFileAsync(kind, result.Path, _plugin.Lifetime);
    }
    private async Task PickExportAsync()
    {
        // Flush the accepted model before a destination is offered. Failure retains that draft.
        var bytes = await Controller.ExportCsvAsync();
        var picker = new FileSavePicker(HostWindowId) { SuggestedFileName = "test-cases" };
        picker.FileTypeChoices.Add("CSV", new[] { ".csv" });
        var result = await picker.PickSaveFileAsync();
        if (result is null) return;
        await NativeTestingFiles.WriteAsync(result.Path, bytes, _plugin.Lifetime);
    }
}
