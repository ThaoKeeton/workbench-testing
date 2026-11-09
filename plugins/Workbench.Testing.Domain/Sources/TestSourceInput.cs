using Workbench.Sdk;
namespace Workbench.Testing.Domain.Sources;

public static class TestSourceInput
{
    /// <summary>Reads at most 1MiB plus one byte from a caller-owned local stream; never uses Length or seeks.</summary>
    public static async Task<byte[]> ReadAsync(Stream content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        using var output=new MemoryStream();
        var buffer=new byte[81920];
        try
        {
            while(true)
            {
                var remaining=LocalSnapshotParser.ByteLimit+1-(int)output.Length;
                var count=await content.ReadAsync(buffer.AsMemory(0,Math.Min(buffer.Length,remaining)),cancellationToken).ConfigureAwait(false);
                if(count==0) return output.ToArray();
                output.Write(buffer,0,count);
                if(output.Length>LocalSnapshotParser.ByteLimit) throw DomainErrors.Error("StoreLimitExceeded");
            }
        }
        catch(Exception error) when(error is IOException or UnauthorizedAccessException) { throw DomainErrors.Error("StoreIoFailure"); }
    }
}
