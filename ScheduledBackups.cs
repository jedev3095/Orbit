using System.Security.Cryptography;
using System.Text;

namespace Orbit;

public sealed class ScheduledBackups : IDisposable
{
    private sealed class Policy
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Folder { get; set; } = "";
        public string Password { get; set; } = "";
        public DateTime? LastSuccess { get; set; }
    }
    private readonly AppService app;
    private readonly string file;
    private Policy? policy;
    private CancellationTokenSource work = new();
    private Task? running;
    private DateTime retryAfter;
    public string LastError { get; private set; } = "";
    public object State => new { enabled = policy is not null, lastSuccess = policy?.LastSuccess, retention = 7, intervalDays = 1, busy = running is { IsCompleted: false }, error = LastError };
    public ScheduledBackups(AppService app)
    {
        this.app = app; file = Path.Combine(app.Vault.DirectoryPath, "backup-policy.dat");
        if (app.IsDemo || !File.Exists(file)) return;
        byte[]? plain = null;
        try { plain = ProtectedData.Unprotect(File.ReadAllBytes(file), null, DataProtectionScope.CurrentUser); policy = Json.Read<Policy>(Encoding.UTF8.GetString(plain)); }
        catch { LastError = "Scheduled backups could not be unlocked. Configure them again."; }
        finally { if (plain is not null) CryptographicOperations.ZeroMemory(plain); }
    }
    private void Save(Policy value)
    {
        byte[] plain = Encoding.UTF8.GetBytes(Json.Write(value));
        try { File.WriteAllBytes(file + ".tmp", ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser)); File.Move(file + ".tmp", file, true); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    public void Configure(string folder, string password)
    {
        if (password.Length < 12) throw new ArgumentException("Use a backup password of at least 12 characters.");
        string target = Path.GetFullPath(folder); if (!Directory.Exists(target)) throw new ArgumentException("Choose an existing backup folder.");
        var next = new Policy { Folder = target, Password = password };
        Save(next); work.Cancel(); work = new(); policy = next; retryAfter = default; LastError = ""; app.Notify();
    }
    public void Disable() { work.Cancel(); if (File.Exists(file)) File.Delete(file); policy = null; LastError = ""; app.Notify(); }
    public void Tick() { if (policy is not null && DateTime.UtcNow >= retryAfter && (policy.LastSuccess is null || DateTime.UtcNow - policy.LastSuccess >= TimeSpan.FromDays(1))) _ = RunNow(); }
    public Task RunNow()
    {
        if (running is { IsCompleted: false }) return running;
        if (policy is null) return Task.CompletedTask;
        var current = policy; string snapshot = Json.Write(app.Vault.Data); var token = work.Token;
        running = Write(current, snapshot, token); return running;
    }
    private async Task Write(Policy current, string snapshot, CancellationToken token)
    {
        try
        {
            await Task.Run(async () =>
            {
                var bytes = Vault.EncodeBackup(Json.Read<Database>(snapshot), current.Password); token.ThrowIfCancellationRequested();
                string path = Path.Combine(current.Folder, $"Orbit-{current.Id}-{DateTime.UtcNow:yyyyMMdd-HHmmss-fffffff}.orbit");
                await File.WriteAllBytesAsync(path + ".tmp", bytes, token); token.ThrowIfCancellationRequested(); File.Move(path + ".tmp", path);
                foreach (var old in new DirectoryInfo(current.Folder).GetFiles($"Orbit-{current.Id}-*.orbit").OrderByDescending(f => f.Name).Skip(7)) old.Delete();
            }, token);
            if (ReferenceEquals(policy, current)) { current.LastSuccess = DateTime.UtcNow; Save(current); LastError = ""; app.Log("Scheduled encrypted backup saved (latest seven retained)."); }
        }
        catch (OperationCanceledException) { }
        catch { LastError = "Scheduled backup failed. Check the selected folder and free space."; retryAfter = DateTime.UtcNow.AddHours(1); app.Log(LastError, "error"); }
        finally { app.Notify(); }
    }
    public void Dispose() => work.Cancel();
}
