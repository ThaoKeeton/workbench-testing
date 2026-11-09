namespace Workbench.Samples;

/// <summary>Nonsecret sample text only. Keeps the in-memory value recoverable until its serialized write succeeds.</summary>
internal sealed class SampleTextBuffer(string text, Func<string, Task> write)
{
    private readonly object _sync = new();
    private string _text = text;
    private long _version;
    private long _savedVersion;
    private Task? _pending;

    internal string Text { get { lock (_sync) return _text; } }
    internal void Set(string value)
    {
        lock (_sync)
        {
            _text = value;
            _version++;
        }
    }

    internal Task SaveAsync()
    {
        lock (_sync)
        {
            if (_pending is not null) return _pending;
            if (_savedVersion == _version) return Task.CompletedTask;
            return _pending = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        string snapshot;
                        long version;
                        lock (_sync)
                        {
                            if (_savedVersion == _version) { _pending = null; return; }
                            snapshot = _text;
                            version = _version;
                        }
                        await write(snapshot).ConfigureAwait(false);
                        lock (_sync) _savedVersion = version;
                    }
                }
                catch
                {
                    // The buffer stays dirty; a later preparation retries it.
                    lock (_sync) _pending = null;
                    throw;
                }
            });
        }
    }

    internal void Autosave()
    {
        // No UI continuation survives a view/owner. Preparation and Stop await
        // the same actual write task, surfacing failure while keeping the model.
        _ = SaveAsync().ContinueWith(static task => { _ = task.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    internal static async Task WriteFileAsync(string path, string value)
    {
        var temporary = Path.Combine(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temporary, value).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
