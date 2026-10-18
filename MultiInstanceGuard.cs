namespace Orbit;

// Windows mutex ownership belongs to a thread. Keep acquisition and release on
// one dedicated thread, including when settings are changed after an await.
public sealed class MultiInstanceGuard : IDisposable
{
    private readonly ManualResetEventSlim stop = new(false), ready = new(false);
    private readonly Thread worker;
    private Exception? failure;
    private volatile bool active;
    private int disposed;
    public bool IsActive => active;
    public static MultiInstanceGuard Start() => new("ROBLOX_singletonMutex", "ROBLOX_singletonEvent");
    internal static MultiInstanceGuard Start(string mutexName, string eventName) => new(mutexName, eventName);
    private MultiInstanceGuard(string mutexName, string eventName)
    {
        worker = new Thread(() => Hold(mutexName, eventName)) { IsBackground = true, Name = "Orbit multi-instance" };
        worker.Start();
        if (!ready.Wait(TimeSpan.FromSeconds(5))) { Dispose(); throw new InvalidOperationException("Multi-instance could not start. Restart Orbit and try again."); }
        if (failure is not null) { Dispose(); throw new InvalidOperationException("Multi-instance is not ready. Close all Roblox clients and other multi-instance managers, then save Settings again before launching Roblox.", failure); }
    }
    private void Hold(string mutexName, string eventName)
    {
        var held = new List<Mutex>();
        try
        {
            foreach (string name in new[] { mutexName, eventName })
            {
                // The second name must be occupied by a Mutex, preventing a
                // Roblox singleton Event from being established under it.
                var mutex = new Mutex(false, name);
                bool owned;
                try { owned = mutex.WaitOne(0); }
                catch (AbandonedMutexException) { owned = true; }
                catch { mutex.Dispose(); throw; }
                if (!owned) { mutex.Dispose(); throw new InvalidOperationException("Another process owns the singleton object."); }
                held.Add(mutex);
            }
            active = true; ready.Set(); stop.Wait();
        }
        catch (Exception ex) { failure = ex; ready.Set(); }
        finally
        {
            active = false;
            for (int i = held.Count - 1; i >= 0; i--) { held[i].ReleaseMutex(); held[i].Dispose(); }
        }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        stop.Set();
        if (worker.Join(TimeSpan.FromSeconds(5))) { stop.Dispose(); ready.Dispose(); }
    }
}
