namespace Workbench.Testing.Domain.OwnedLab;

internal class OwnedAtomicFiles
{
    internal virtual async Task PublishNewAsync(string path, ReadOnlyMemory<byte> bytes)
    {
        var frozen = bytes.ToArray();
        var pending = Path.Combine(Path.GetDirectoryName(path)!, ".pending-" + Guid.NewGuid().ToString("N"));
        await using (var stream = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await stream.WriteAsync(frozen).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }
        File.Move(pending, path, overwrite: false);
        if (!(await File.ReadAllBytesAsync(path).ConfigureAwait(false)).AsSpan().SequenceEqual(frozen)) throw new IOException("Immutable publication readback failed.");
    }
}
