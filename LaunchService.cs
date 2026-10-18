using System.Diagnostics;

namespace Orbit;

public record LaunchTarget(long PlaceId, string JobId = "", string Mode = "game", long UserId = 0)
{
    public void Validate()
    {
        if (Mode is not ("game" or "private" or "follow")) throw new ArgumentException("Unknown launch mode.");
        if (Mode == "follow") { if (UserId <= 0 || PlaceId != 0) throw new ArgumentException("Follow requires userId; omit placeId."); }
        else if (PlaceId <= 0 || UserId != 0) throw new ArgumentException("Game and private launches require placeId; omit userId.");
        if (Mode == "private" && string.IsNullOrWhiteSpace(JobId)) throw new ArgumentException("A private server link code is required.");
    }
    public string Query(string tracker)
    {
        Validate();
        return Mode switch
        {
            "follow" => $"request=RequestFollowUser&userId={UserId}",
            "private" => $"request=RequestPrivateGame&placeId={PlaceId}&linkCode={Uri.EscapeDataString(JobId)}",
            _ => $"request=RequestGame{(string.IsNullOrWhiteSpace(JobId) ? "" : "Job")}&placeId={PlaceId}&browserTrackerId={tracker}&isPlayTogetherGame=false{(string.IsNullOrWhiteSpace(JobId) ? "" : "&gameId=" + Uri.EscapeDataString(JobId))}"
        };
    }
}

public interface ILaunchPlatform
{
    void Start(string protocol);
    ProcessIdentity? Find(string tracker);
    bool IsRunning(Account account);
    bool Stop(Account account);
}

public sealed class WindowsLaunchPlatform : ILaunchPlatform
{
    public void Start(string protocol) => Process.Start(new ProcessStartInfo(protocol) { UseShellExecute = true });
    public ProcessIdentity? Find(string tracker) => ProcessTracker.Find(tracker);
    public bool IsRunning(Account account) => ProcessTracker.IsRunning(account);
    public bool Stop(Account account) => ProcessTracker.Stop(account);
}

public sealed class LaunchService : IDisposable
{
    private readonly AppService app;
    private readonly ILaunchPlatform platform;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private readonly Func<DateTime> now;
    private readonly SemaphoreSlim queue = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly Dictionary<Guid, CancellationTokenSource> pending = [];
    private CancellationTokenSource? active;
    public bool IsLaunching => active is not null;
    public const int RetryLimit = 3;
    public LaunchService(AppService app, ILaunchPlatform? platform = null, Func<TimeSpan, CancellationToken, Task>? delay = null, Func<DateTime>? now = null)
    { this.app = app; this.platform = platform ?? new WindowsLaunchPlatform(); this.delay = delay ?? Task.Delay; this.now = now ?? (() => DateTime.UtcNow); }
    public void CancelQueue() => active?.Cancel();
    public void CancelRelaunch(Guid id) { if (pending.TryGetValue(id, out var task)) task.Cancel(); }
    public async Task<LaunchResult> Launch(List<Guid> ids, long placeId, string jobId, string mode = "game", long userId = 0, bool automatic = false, CancellationToken cancellation = default)
    {
        app.RequireLive(); var target = new LaunchTarget(placeId, jobId, mode, userId); target.Validate();
        if (ids.Count == 0) throw new ArgumentException("Select accounts to launch.");
        if (ids.Count > 1 && !app.Vault.Data.Settings.MultiInstance) throw new InvalidOperationException("Enable Multiple Roblox instances in Settings first.");
        if (!automatic) foreach (var id in ids) { CancelRelaunch(id); app.Find(id).RelaunchAttempts = 0; }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellation);
        await queue.WaitAsync(linked.Token);
        active = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
        int launched = 0, failed = 0; bool cancelled = false; app.Notify();
        try
        {
            foreach (var id in ids)
            {
                active.Token.ThrowIfCancellationRequested(); var account = app.Find(id); string ticket = "";
                try
                {
                    account.LastError = ""; app.EnsureMultiInstance(); ticket = await app.Api.Ticket(account);
                    active.Token.ThrowIfCancellationRequested();
                    if (!app.Vault.Data.Accounts.Contains(account)) throw new OperationCanceledException(active.Token);
                    if (app.Vault.Data.Settings.ClosePrevious) StopProcess(account);
                    string tracker = Random.Shared.NextInt64(100000000000, 999999999999).ToString();
                    string url = $"roblox-player:1+launchmode:play+gameinfo:{ticket}+launchtime:{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}+placelauncherurl:{Uri.EscapeDataString("https://assetgame.roblox.com/game/PlaceLauncher.ashx?" + target.Query(tracker))}+browsertrackerid:{tracker}+robloxLocale:en_us+gameLocale:en_us+channel:+LaunchExp:InApp";
                    platform.Start(url); ProcessTracker.Clear(account); account.Tracker = tracker;
                    account.LastUse = now(); account.PlaceId = placeId; account.JobId = jobId; account.LaunchMode = mode; account.FollowUserId = userId; account.Status = "Launching";
                    launched++; app.Log($"Launching @{account.Username}."); app.Vault.Save();
                    _ = Associate(account, tracker);
                }
                catch (OperationCanceledException) when (active.IsCancellationRequested) { throw; }
                catch (Exception ex) { failed++; AppService.SetFailure(account, ex, ticket); app.Log($"@{account.Username}: {account.LastError}", "error"); }
                if (id != ids[^1]) await delay(TimeSpan.FromSeconds(app.Vault.Data.Settings.LaunchDelay), active.Token);
            }
            var game = app.Vault.Data.Games.FirstOrDefault(g => mode != "follow" && g.PlaceId == placeId); if (game is not null) game.LastPlayed = now();
            app.Vault.Save();
        }
        catch (OperationCanceledException) { cancelled = true; app.Log("Launch queue cancelled."); }
        finally { active.Dispose(); active = null; queue.Release(); app.Notify(); }
        return new(ids.Count, launched, failed, cancelled);
    }
    private async Task Associate(Account account, string tracker)
    {
        try
        {
            for (int i = 0; i < 30; i++)
            {
                await delay(TimeSpan.FromSeconds(1), lifetime.Token);
                if (account.Tracker != tracker || !app.Vault.Data.Accounts.Contains(account)) return;
                var identity = platform.Find(tracker); if (identity is null) continue;
                ProcessTracker.Assign(account, identity); account.Status = "Running";
                if (app.Vault.Data.Settings.RememberWindows) ProcessTracker.Restore(account);
                app.Notify(); return;
            }
            if (account.Tracker == tracker)
            {
                account.Status = "Launch unverified"; account.LastError = "The client launch was requested, but its process could not be verified. Automatic retries are paused to avoid opening extra clients.";
                app.Log($"@{account.Username}: {account.LastError}", "warning");
            }
        }
        catch (OperationCanceledException) { }
        catch { account.Status = "Launch unverified"; app.Log("Could not verify the launched client process.", "warning"); }
    }
    public void Stop(Account account) { CancelRelaunch(account.Id); StopProcess(account); account.Tracker = ""; }
    private void StopProcess(Account account)
    {
        if (!app.IsDemo && platform.Stop(account)) { account.Status = "Stopped"; app.Log($"Stopped @{account.Username}."); }
        ProcessTracker.Clear(account);
    }
    public void Watch()
    {
        foreach (var account in app.Vault.Data.Accounts.ToArray())
        {
            if (!account.AutoRelaunch) CancelRelaunch(account.Id);
            if (platform.IsRunning(account))
            {
                if (account.ProcessStartedUtc is DateTime started && now() - started >= TimeSpan.FromMinutes(2)) account.RelaunchAttempts = 0;
                if (app.Vault.Data.Settings.RememberWindows) ProcessTracker.Capture(account);
                continue;
            }
            if (account.ProcessId is null) continue;
            ProcessTracker.Clear(account); account.Status = "Closed";
            if (account.AutoRelaunch) Schedule(account);
        }
        app.Notify();
    }
    internal void Schedule(Account account)
    {
        if (pending.ContainsKey(account.Id) || !account.AutoRelaunch) return;
        if (account.RelaunchAttempts >= RetryLimit) { GiveUp(account); return; }
        var source = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); pending[account.Id] = source;
        _ = Retry(account, source);
    }
    private async Task Retry(Account account, CancellationTokenSource source)
    {
        try
        {
            while (account.RelaunchAttempts < RetryLimit)
            {
                account.RelaunchAttempts++;
                account.Status = $"Relaunch queued ({account.RelaunchAttempts}/{RetryLimit})"; app.Notify();
                await delay(TimeSpan.FromSeconds(Math.Max(1, account.RelaunchSeconds) * Math.Pow(2, account.RelaunchAttempts - 1)), source.Token);
                if (!app.Vault.Data.Accounts.Contains(account) || !account.AutoRelaunch || platform.IsRunning(account)) return;
                var result = await Launch([account.Id], account.PlaceId, account.JobId, account.LaunchMode, account.FollowUserId, true, source.Token);
                if (result.Cancelled || result.Launched > 0) return;
            }
            GiveUp(account);
        }
        catch (OperationCanceledException) { }
        catch { GiveUp(account); }
        finally { if (pending.TryGetValue(account.Id, out var current) && ReferenceEquals(current, source)) pending.Remove(account.Id); source.Dispose(); }
    }
    private void GiveUp(Account account) { account.Status = "Relaunch gave up"; account.LastError = $"Automatic relaunch stopped after {account.RelaunchAttempts} attempts. Launch manually to reset. " + account.LastError; app.Log($"@{account.Username}: {account.LastError}", "warning"); }
    public void Dispose() { lifetime.Cancel(); foreach (var source in pending.Values.ToArray()) source.Cancel(); }
}
