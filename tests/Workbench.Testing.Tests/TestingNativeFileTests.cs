using Workbench.Testing;
using Workbench.Sdk;
using Xunit;

namespace Workbench.Core.Tests;

public sealed class TestingNativeFileTests
{
    [Fact]
    public async Task Same_native_file_reader_preserves_original_bytes_and_refuses_oversize_without_changing_input()
    {
        using var disk = new TestDirectory();
        var path = Path.Combine(disk.Root, "literal.synthetic.json");
        byte[] original = [239, 187, 191, 123, 125, 13, 10];
        await File.WriteAllBytesAsync(path, original);
        Assert.Equal(original, await NativeTestingFiles.ReadAsync(path, default));
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
        await File.WriteAllBytesAsync(path, new byte[1024 * 1024 + 1]);
        Assert.Equal("StoreLimitExceeded", (await Assert.ThrowsAsync<PluginTestingException>(() => NativeTestingFiles.ReadAsync(path, default))).ErrorCode);
        Assert.Equal(1024 * 1024 + 1, new FileInfo(path).Length);
    }

    [Fact]
    public async Task Same_native_export_writer_replaces_exact_bytes_and_failed_or_cancelled_destination_retains_old_file()
    {
        using var disk = new TestDirectory();
        var path = Path.Combine(disk.Root, "export.csv");
        byte[] expected = [239, 187, 191, 34, 39, 61, 49, 34, 13, 10];
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        await NativeTestingFiles.WriteAsync(path, expected, default);
        Assert.Equal(expected, await File.ReadAllBytesAsync(path));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NativeTestingFiles.WriteAsync(path, [4, 5], cancelled.Token));
        Assert.Equal(expected, await File.ReadAllBytesAsync(path));
        var directory = Path.Combine(disk.Root, "directory.csv"); Directory.CreateDirectory(directory);
        await Assert.ThrowsAsync<IOException>(() => NativeTestingFiles.WriteAsync(directory, [6, 7], default));
        Assert.True(Directory.Exists(directory));
        Assert.Equal(expected, await File.ReadAllBytesAsync(path));
        Assert.Empty(Directory.EnumerateFiles(disk.Root, ".testing-export-*.tmp"));
    }
}
