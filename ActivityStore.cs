namespace Orbit;

public sealed class ActivityStore : IDisposable
{
    private readonly string path;
    private readonly System.Threading.Timer timer;
    private readonly SemaphoreSlim writer = new(1, 1);
    private readonly object gate = new();
    private string? snapshot;
    private bool disposed;
    public string LastError { get; private set; } = "";
    public ActivityStore(string path) { this.path = path; timer = new(async _ => { try { await FlushAsync(); } catch { LastError = "Activity history could not be saved."; } }); }
    public void Queue(Activity[] entries)
    {
        lock (gate) { if (disposed) return; snapshot = Json.Write(entries); timer.Change(1500, Timeout.Infinite); }
    }
    public async Task FlushAsync()
    {
        await writer.WaitAsync().ConfigureAwait(false);
        string? current = null;
        try
        {
            lock (gate) { current = snapshot; snapshot = null; }
            if (current is null) return;
            await File.WriteAllTextAsync(path + ".tmp", current).ConfigureAwait(false);
            File.Move(path + ".tmp", path, true); LastError = "";
        }
        catch { lock (gate) snapshot ??= current; LastError = "Activity history could not be saved."; }
        finally { writer.Release(); }
    }
    public void Dispose() { lock (gate) disposed = true; timer.Dispose(); FlushAsync().GetAwaiter().GetResult(); }
}
