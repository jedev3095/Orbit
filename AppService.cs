using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Orbit;

public sealed class AppService : IDisposable
{
    public Vault Vault { get; }
    public RobloxApi Api { get; }
    public List<Activity> Activity { get; } = [];
    public bool IsDemo { get; }
    public event Action? Changed;
    public event Action<Guid>? AccountAccepted;
    public ControlServer? Server { get; private set; }
    public Func<Func<Task<object?>>, Task<object?>> Dispatch { get; set; } = work => work();
    private MultiInstanceGuard? multi;
    private readonly Func<MultiInstanceGuard> createMulti;
    private string multiError = "";
    private readonly CancellationTokenSource lifetime = new();
    private bool disposed;
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 5000 };
    public LaunchService Launches { get; }
    public ScheduledBackups Backups { get; }
    private readonly AccountTools tools;
    private readonly ActivityStore activityStore;
    public AppService(Vault vault, bool demo = false, RobloxApi? api = null, Func<MultiInstanceGuard>? multiFactory = null)
    {
        Vault = vault; IsDemo = demo; Api = api ?? new RobloxApi(); createMulti = multiFactory ?? MultiInstanceGuard.Start;
        if (!demo)
        {
            try { if (File.Exists(Path.Combine(vault.DirectoryPath, "activity.json"))) Activity.AddRange(Json.Read<List<Activity>>(File.ReadAllText(Path.Combine(vault.DirectoryPath, "activity.json"))).Take(200)); } catch { }
        }
        activityStore = new ActivityStore(Path.Combine(vault.DirectoryPath, "activity.json"));
        tools = new AccountTools(Api, Log);
        Launches = new LaunchService(this);
        Backups = new ScheduledBackups(this);
        timer.Tick += async (_, _) => await Watch(); timer.Start();
    }
    public Task FlushActivityAsync() => activityStore.FlushAsync();
    internal async Task ListenerFailed(ControlServer source, Exception error)
    {
        try { await Dispatch(() => { if (ReferenceEquals(Server, source)) { Server = null; source.Dispose(); Log($"Local API listener stopped unexpectedly ({error.GetType().Name}, 0x{error.HResult:X8}). Save local control settings to restart it.", "error"); } return Task.FromResult<object?>(null); }); } catch { source.Dispose(); }
    }
    public void Notify() => Changed?.Invoke();
    public async Task<Account> AcceptSession(string cookie, string source, CancellationToken cancellation = default)
    {
        RequireLive();
        var identified = await Api.Identify(Input.Cookie(cookie));
        cancellation.ThrowIfCancellationRequested();
        Vault.Merge([identified]);
        var stored = Vault.Data.Accounts.Single(a => a.UserId == identified.UserId);
        bool identityChanged = stored.Username != identified.Username || stored.DisplayName != identified.DisplayName;
        stored.Username = identified.Username; stored.DisplayName = identified.DisplayName;
        stored.Status = identified.Status; stored.LastError = "";
        if (identityChanged) Vault.Save();
        Log($"Added or refreshed @{stored.Username} {source}.");
        AccountAccepted?.Invoke(stored.Id);
        return stored;
    }
    public void Log(string message, string kind = "info")
    {
        message = Diagnostics.Redact(message, [Vault.Data.Settings.ApiToken, .. Vault.Data.Accounts.Select(a => a.SecurityToken)]);
        Activity.Insert(0, new(DateTime.UtcNow, kind, message));
        if (Activity.Count > 200) Activity.RemoveRange(200, Activity.Count - 200);
        if (!IsDemo) activityStore.Queue(Activity.ToArray());
        Notify();
    }
    public object State() => new
    {
        accounts = Vault.Data.Accounts.Select(a => new { a.Id, a.UserId, a.Username, a.DisplayName, a.Alias, a.Description, a.Group, a.Fields, a.LastUse, a.Status, a.LastError, a.Robux, a.Thumbnail, a.PlaceId, a.JobId, a.AutoRelaunch, a.RelaunchSeconds, running = IsRunning(a), connected = Server?.Connected(a.Id) ?? false }),
        games = Vault.Data.Games.OrderByDescending(g => g.Favorite).ThenByDescending(g => g.LastPlayed),
        settings = new { Vault.Data.Settings.MultiInstance, Vault.Data.Settings.LaunchDelay, Vault.Data.Settings.HideUsernames, Vault.Data.Settings.RememberWindows, Vault.Data.Settings.ClosePrevious, Vault.Data.Settings.ApiEnabled, Vault.Data.Settings.ApiPort, Vault.Data.Settings.Accent },
        activity = Activity.ToArray(), demo = IsDemo, version = typeof(AppService).Assembly.GetName().Version?.ToString(3), apiRunning = Server?.IsRunning == true, launching = Launches.IsLaunching,
        multiInstanceReady = multi?.IsActive == true, multiInstanceError = multiError, backups = Backups.State
    };
    public Account Find(Guid id) => Vault.Data.Accounts.FirstOrDefault(a => a.Id == id) ?? throw new ArgumentException("Account not found.");
    public async Task<object?> Command(string action, JsonElement p)
    {
        string Text(string name, string fallback = "") => p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var v) ? v.GetString() ?? fallback : fallback;
        long Number(string name, long fallback = 0) => p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var v) && v.TryGetInt64(out long n) ? n : fallback;
        bool Flag(string name) => p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
        Account Target() => Find(Guid.Parse(Text("id")));
        switch (action)
        {
            case "state": return State();
            case "searchPlaces": return await Api.SearchPlaces(Text("query"), Text("cursor"), Text("session"));
            case "rotateApiToken":
                string previous = Vault.Data.Settings.ApiToken;
                Vault.Data.Settings.ApiToken = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
                try { Vault.Save(); } catch { Vault.Data.Settings.ApiToken = previous; throw; }
                Server?.RotateToken(Vault.Data.Settings.ApiToken); Log("API token regenerated. Existing Nexus connections were closed."); return null;
            case "disableBackups": Backups.Disable(); Log("Scheduled backups disabled."); return null;
            case "backupNow": await Backups.RunNow(); return null;
            case "addCookie":
                RequireLive();
                await AcceptSession(Text("cookie"), "from a session cookie"); return null;
            case "editAccount":
                var a = Target(); a.Alias = Text("alias").Trim()[..Math.Min(Text("alias").Trim().Length, 50)];
                a.Group = Text("group", "Default").Trim(); if (a.Group.Length == 0) a.Group = "Default";
                a.Description = Text("description");
                if (a.Description.Length > 5000) throw new ArgumentException("Notes are limited to 5,000 characters.");
                if (p.TryGetProperty("fields", out var fields)) a.Fields = fields.Deserialize<Dictionary<string, string>>(Json.Options) ?? [];
                a.AutoRelaunch = Flag("autoRelaunch"); a.RelaunchSeconds = (int)Math.Clamp(Number("relaunchSeconds", 60), 30, 3600);
                a.PlaceId = Number("placeId"); a.JobId = Text("jobId");
                if (!a.AutoRelaunch) Launches.CancelRelaunch(a.Id);
                Vault.Save(); Log($"Updated @{a.Username}."); return null;
            case "remove":
                foreach (var id in Ids(p)) { var account = Find(id); Launches.CancelRelaunch(id); Vault.Data.Accounts.Remove(account); }
                Vault.Save(); Log("Removed selected accounts from Orbit."); return null;
            case "refresh":
                RequireLive();
                foreach (var id in Ids(p))
                {
                    var account = Find(id);
                    try { await Api.Refresh(account); } catch (Exception ex) { SetFailure(account, ex, failureStatus: "Refresh failed"); Log($"@{account.Username}: {account.LastError}", "error"); }
                    Notify(); await Task.Delay(200, lifetime.Token);
                }
                Vault.Save(); Log("Account refresh finished."); return null;
            case "launch": return await Launch(Ids(p), Number("placeId"), Text("jobId"), Text("mode", "game"), Number("userId"));
            case "cancelLaunch": Launches.CancelQueue(); Log("Launch queue cancellation requested."); return null;
            case "stop": foreach (var id in Ids(p)) Stop(Find(id)); return null;
            case "addGame":
                RequireLive(); var game = await Api.Game(Number("placeId"));
                if (Vault.Data.Games.All(g => g.PlaceId != game.PlaceId)) Vault.Data.Games.Add(game);
                Vault.Save(); Log($"Added {game.Name} to your library."); return null;
            case "favorite":
                var favorite = Vault.Data.Games.First(g => g.PlaceId == Number("placeId")); favorite.Favorite = !favorite.Favorite; Vault.Save(); Notify(); return null;
            case "removeGame": Vault.Data.Games.RemoveAll(g => g.PlaceId == Number("placeId")); Vault.Save(); Notify(); return null;
            case "servers": RequireLive(); return await Api.Servers(Number("placeId"), Text("cursor"));
            case "settings":
                var settings = Vault.Data.Settings;
                settings.MultiInstance = Flag("multiInstance"); settings.LaunchDelay = (int)Math.Clamp(Number("launchDelay", 8), 1, 60);
                settings.HideUsernames = Flag("hideUsernames"); settings.RememberWindows = Flag("rememberWindows"); settings.ClosePrevious = Flag("closePrevious");
                settings.ApiEnabled = Flag("apiEnabled"); settings.ApiPort = (int)Math.Clamp(Number("apiPort", 7963), 1024, 65535);
                settings.Accent = Text("accent") == "violet" ? "violet" : "blue";
                try { ApplySettings(); } finally { Vault.Save(); Notify(); }
                Log("Settings saved."); return null;
            case "nexusSend":
                RequireLive(); if (Server is null) throw new InvalidOperationException("Enable local controls in Settings first.");
                await Server.Send(Ids(p), Text("message")); Log("Sent a command to connected Nexus clients."); return null;
            case "accountTool": RequireLive(); return await tools.Run(Target(), Text("tool"), Text("value"));
            default: throw new ArgumentException("Unknown command.");
        }
    }
    public static List<Guid> Ids(JsonElement p) => p.GetProperty("ids").EnumerateArray().Select(x => x.GetGuid()).Distinct().ToList();
    internal void RequireLive() { if (IsDemo) throw new InvalidOperationException("Preview mode uses sample data. Live account actions are disabled."); }
    public void ApplySettings()
    {
        if (IsDemo) return;
        var settings = Vault.Data.Settings;
        Exception? multiFailure = null;
        try { EnsureMultiInstance(); } catch (Exception ex) { multiFailure = ex; }
        if (Server is not null && (!settings.ApiEnabled || Server.Port != settings.ApiPort)) { Server.Dispose(); Server = null; }
        if (settings.ApiEnabled && Server is null)
        {
            try { Server = new ControlServer(this, settings.ApiPort, settings.ApiToken); Server.Start(); }
            catch { Server?.Dispose(); Server = null; settings.ApiEnabled = false; throw new InvalidOperationException("Local control server could not start. Check that the port is available."); }
        }
        if (multiFailure is not null) throw multiFailure;
    }
    internal void EnsureMultiInstance()
    {
        if (!Vault.Data.Settings.MultiInstance) { multi?.Dispose(); multi = null; multiError = ""; return; }
        if (multi?.IsActive == true) return;
        multi?.Dispose(); multi = null;
        try { multi = createMulti(); multiError = ""; }
        catch (Exception ex) { multiError = ex.Message; throw; }
    }
    internal static void SetFailure(Account account, Exception error, string ticket = "", string failureStatus = "Launch failed")
    {
        account.Status = error is RobloxApiException { RequiresLogin: true } ? "Needs login" : failureStatus;
        account.LastError = Diagnostics.Redact(error.Message, account.SecurityToken, ticket);
    }
    public Task<LaunchResult> Launch(List<Guid> ids, long placeId, string jobId, string mode = "game", long userId = 0) => Launches.Launch(ids, placeId, jobId, mode, userId);
    public static bool IsRunning(Account account) => ProcessTracker.IsRunning(account);
    private void Stop(Account account) => Launches.Stop(account);
    private Task Watch() { if (!IsDemo) { Launches.Watch(); Backups.Tick(); } return Task.CompletedTask; }
    public void Dispose() { if (disposed) return; disposed = true; lifetime.Cancel(); timer.Stop(); timer.Dispose(); Launches.Dispose(); Backups.Dispose(); Server?.Dispose(); multi?.Dispose(); activityStore.Dispose(); Api.Dispose(); }
}
