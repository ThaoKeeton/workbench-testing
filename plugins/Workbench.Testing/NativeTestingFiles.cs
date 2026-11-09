using Workbench.Testing.Domain.Sources;

namespace Workbench.Testing;

internal static class NativeTestingFiles
{
    internal static Task<byte[]> ReadAsync(string path, CancellationToken token) => Task.Run(async () =>
    {
        RejectLinks(path);
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
        return await TestSourceInput.ReadAsync(input, token).ConfigureAwait(false);
    }, token);

    internal static Task WriteAsync(string path, byte[] bytes, CancellationToken token) => Task.Run(async () =>
    {
        path = Path.GetFullPath(path);
        RejectLinks(path);
        var temporary = Path.Combine(Path.GetDirectoryName(path)!, ".testing-export-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
            {
                await output.WriteAsync(bytes, token).ConfigureAwait(false);
                await output.FlushAsync(token).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            RejectLinks(path);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }, token);

    private static void RejectLinks(string path)
    {
        for (var part = Path.GetFullPath(path); !string.IsNullOrEmpty(part); part = Path.GetDirectoryName(part))
            if ((File.Exists(part) || Directory.Exists(part)) && (File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Local file link refused.");
        if (Directory.Exists(path)) throw new IOException("A regular file is required.");
    }
}
