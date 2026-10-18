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
    public ControlServer? Server { get; private set; }
    public Func<Func<Task<object?>>, Task<object?>> Dispatch { get; set; } = work => work();
    private MultiInstanceGuard? multi;
    private readonly Func<MultiInstanceGuard> createMulti;
    private string multiError = "";
    private readonly SemaphoreSlim launchLock = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 5000 };
    private bool watching;
    private CancellationTokenSource? activeLaunch;
    public AppService(Vault vault, bool demo = false, RobloxApi? api = null, Func<MultiInstanceGuard>? multiFactory = null)
    {
        Vault = vault; IsDemo = demo; Api = api ?? new RobloxApi(); createMulti = multiFactory ?? MultiInstanceGuard.Start;
        if (!demo)
        {
            try { if (File.Exists(Path.Combine(vault.DirectoryPath, "activity.json"))) Activity.AddRange(Json.Read<List<Activity>>(File.ReadAllText(Path.Combine(vault.DirectoryPath, "activity.json"))).Take(200)); } catch { }
        }
        if (demo) Seed();
        timer.Tick += async (_, _) => await Watch(); timer.Start();
    }
    public void Notify() => Changed?.Invoke();
    public void Log(string message, string kind = "info")
    {
        message = Diagnostics.Redact(message, [Vault.Data.Settings.ApiToken, .. Vault.Data.Accounts.Select(a => a.SecurityToken), .. Vault.Data.Accounts.Select(a => a.Password)]);
        Activity.Insert(0, new(DateTime.UtcNow, kind, message));
        if (Activity.Count > 200) Activity.RemoveRange(200, Activity.Count - 200);
        if (!IsDemo) { try { File.WriteAllText(Path.Combine(Vault.DirectoryPath, "activity.json"), Json.Write(Activity)); } catch { } }
        Notify();
    }
    public object State() => new
    {
        accounts = Vault.Data.Accounts.Select(a => new { a.Id, a.UserId, a.Username, a.DisplayName, a.Alias, a.Description, a.Group, a.Fields, a.LastUse, a.Status, a.LastError, a.Robux, a.Thumbnail, a.PlaceId, a.JobId, a.AutoRelaunch, a.RelaunchSeconds, running = IsRunning(a), connected = Server?.Connected(a.Id) ?? false }),
        games = Vault.Data.Games.OrderByDescending(g => g.Favorite).ThenByDescending(g => g.LastPlayed),
        settings = new { Vault.Data.Settings.MultiInstance, Vault.Data.Settings.LaunchDelay, Vault.Data.Settings.HideUsernames, Vault.Data.Settings.RememberWindows, Vault.Data.Settings.ClosePrevious, Vault.Data.Settings.ApiEnabled, Vault.Data.Settings.ApiPort, Vault.Data.Settings.Accent },
        activity = Activity.ToArray(), demo = IsDemo, version = "0.1.5", apiRunning = Server is not null, launching = activeLaunch is not null,
        multiInstanceReady = multi?.IsActive == true, multiInstanceError = multiError
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
            case "addCookie":
                RequireLive();
                var added = await Api.Identify(Input.Cookie(Text("cookie")));
                Vault.Merge([added]); Log($"Added or refreshed @{added.Username}."); return null;
            case "editAccount":
                var a = Target(); a.Alias = Text("alias").Trim()[..Math.Min(Text("alias").Trim().Length, 50)];
                a.Group = Text("group", "Default").Trim(); if (a.Group.Length == 0) a.Group = "Default";
                a.Description = Text("description");
                if (a.Description.Length > 5000) throw new ArgumentException("Notes are limited to 5,000 characters.");
                if (p.TryGetProperty("fields", out var fields)) a.Fields = fields.Deserialize<Dictionary<string, string>>(Json.Options) ?? [];
                a.AutoRelaunch = Flag("autoRelaunch"); a.RelaunchSeconds = (int)Math.Clamp(Number("relaunchSeconds", 60), 30, 3600);
                a.PlaceId = Number("placeId"); a.JobId = Text("jobId");
                Vault.Save(); Log($"Updated @{a.Username}."); return null;
            case "remove":
                foreach (var id in Ids(p)) { var account = Find(id); Vault.Data.Accounts.Remove(account); }
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
            case "launch": return await Launch(Ids(p), Number("placeId"), Text("jobId"), Text("mode", "game"));
            case "cancelLaunch": activeLaunch?.Cancel(); Log("Launch queue cancellation requested."); return null;
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
            case "accountTool": RequireLive(); return await Tool(Target(), Text("tool"), Text("value"));
            default: throw new ArgumentException("Unknown command.");
        }
    }
    public static List<Guid> Ids(JsonElement p) => p.GetProperty("ids").EnumerateArray().Select(x => x.GetGuid()).Distinct().ToList();
    private void RequireLive() { if (IsDemo) throw new InvalidOperationException("Preview mode uses sample data. Live account actions are disabled."); }
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
    private void EnsureMultiInstance()
    {
        if (!Vault.Data.Settings.MultiInstance) { multi?.Dispose(); multi = null; multiError = ""; return; }
        if (multi?.IsActive == true) return;
        multi?.Dispose(); multi = null;
        try { multi = createMulti(); multiError = ""; }
        catch (Exception ex) { multiError = ex.Message; throw; }
    }
    private static void SetFailure(Account account, Exception error, string ticket = "", string failureStatus = "Launch failed")
    {
        account.Status = error is RobloxApiException { RequiresLogin: true } ? "Needs login" : failureStatus;
        account.LastError = Diagnostics.Redact(error.Message, account.SecurityToken, account.Password, ticket);
    }
    public async Task<LaunchResult> Launch(List<Guid> ids, long place, string job, string mode = "game")
    {
        RequireLive();
        if (ids.Count == 0 || place <= 0) throw new ArgumentException("Select accounts and enter a place ID (or a user ID for Follow). ");
        if (mode is not ("game" or "private" or "follow")) throw new ArgumentException("Unknown launch mode.");
        if (ids.Count > 1 && !Vault.Data.Settings.MultiInstance) throw new InvalidOperationException("Enable Multiple Roblox instances in Settings and save before launching several accounts.");
        await launchLock.WaitAsync(lifetime.Token);
        activeLaunch = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        int launched = 0, failed = 0; bool cancelled = false;
        Notify();
        try
        {
            foreach (var id in ids)
            {
                activeLaunch.Token.ThrowIfCancellationRequested();
                var a = Find(id);
                string ticket = "";
                try
                {
                    a.LastError = "";
                    EnsureMultiInstance();
                    ticket = await Api.Ticket(a);
                    if (Vault.Data.Settings.ClosePrevious) Stop(a);
                    a.Tracker = Random.Shared.NextInt64(100000000000, 999999999999).ToString();
                    string query = mode switch
                    {
                        "private" => $"request=RequestPrivateGame&placeId={place}&linkCode={Uri.EscapeDataString(job)}",
                        "follow" => $"request=RequestFollowUser&userId={place}",
                        _ => $"request=RequestGame{(string.IsNullOrWhiteSpace(job) ? "" : "Job")}&placeId={place}&browserTrackerId={a.Tracker}&isPlayTogetherGame=false{(string.IsNullOrWhiteSpace(job) ? "" : "&gameId=" + Uri.EscapeDataString(job))}"
                    };
                    var url = $"roblox-player:1+launchmode:play+gameinfo:{ticket}+launchtime:{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}+placelauncherurl:{Uri.EscapeDataString("https://assetgame.roblox.com/game/PlaceLauncher.ashx?" + query)}+browsertrackerid:{a.Tracker}+robloxLocale:en_us+gameLocale:en_us+channel:+LaunchExp:InApp";
                    Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                    a.LastUse = DateTime.UtcNow; a.PlaceId = place; a.JobId = job; a.Status = "Launching";
                    launched++;
                    Log($"Launching @{a.Username}."); Vault.Save();
                    _ = AssociateProcess(a);
                }
                catch (Exception ex) { failed++; SetFailure(a, ex, ticket); Log($"@{a.Username}: {a.LastError}", "error"); }
                if (id != ids[^1]) await Task.Delay(TimeSpan.FromSeconds(Vault.Data.Settings.LaunchDelay), activeLaunch.Token);
            }
            var g = Vault.Data.Games.FirstOrDefault(x => x.PlaceId == place); if (g is not null) g.LastPlayed = DateTime.UtcNow;
            Vault.Save(); Notify();
        }
        catch (OperationCanceledException) { cancelled = true; Log("Launch queue cancelled."); }
        finally { activeLaunch.Dispose(); activeLaunch = null; launchLock.Release(); Notify(); }
        return new LaunchResult(ids.Count, launched, failed, cancelled);
    }
    private async Task AssociateProcess(Account a)
    {
        // Match only the app's unique tracker. Never guess ownership from an arbitrary running Roblox process.
        for (int i = 0; i < 30 && !lifetime.IsCancellationRequested; i++)
        {
            try { await Task.Delay(1000, lifetime.Token); } catch (OperationCanceledException) { return; }
            var pid = ProcessTracker.Find(a.Tracker);
            if (pid is null) continue;
            a.ProcessId = pid; a.Status = "Running";
            if (Vault.Data.Settings.RememberWindows) ProcessTracker.Restore(a);
            Notify(); return;
        }
        Log($"@{a.Username}: launch requested; process ownership could not be verified.", "warning");
    }
    public static bool IsRunning(Account a)
    {
        try { return a.ProcessId is int id && !Process.GetProcessById(id).HasExited; } catch { return false; }
    }
    private void Stop(Account a)
    {
        if (!IsDemo && IsRunning(a)) { using var p = Process.GetProcessById(a.ProcessId!.Value); p.Kill(); a.ProcessId = null; a.Status = "Stopped"; Log($"Stopped @{a.Username}."); }
    }
    private async Task Watch()
    {
        if (watching || IsDemo) return; watching = true;
        try
        {
            foreach (var a in Vault.Data.Accounts.ToArray())
            {
                if (IsRunning(a)) { if (Vault.Data.Settings.RememberWindows) ProcessTracker.Capture(a); continue; }
                if (a.ProcessId is null) continue;
                a.ProcessId = null; a.Status = "Closed"; Notify();
                if (a.AutoRelaunch && a.PlaceId > 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(a.RelaunchSeconds), lifetime.Token);
                    if (Vault.Data.Accounts.Contains(a) && a.AutoRelaunch) await Launch([a.Id], a.PlaceId, a.JobId);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log(ex.Message, "error"); }
        finally { watching = false; }
    }
    private async Task<object> Tool(Account a, string tool, string value)
    {
        JsonNode response;
        switch (tool)
        {
            case "balance": response = await Api.Auth(a, $"https://economy.roblox.com/v1/users/{a.UserId}/currency", HttpMethod.Get); break;
            case "profile": response = await Api.Public($"https://users.roblox.com/v1/users/{a.UserId}"); break;
            case "blocked": response = await Api.Auth(a, "https://accountsettings.roblox.com/v1/users/get-detailed-blocked-users", HttpMethod.Get); break;
            case "displayName":
                response = await Api.Auth(a, $"https://users.roblox.com/v1/users/{a.UserId}/display-names", HttpMethod.Patch, new { newDisplayName = value }); break;
            case "block":
            case "unblock":
                if (!long.TryParse(value, out var userId) || userId <= 0) throw new ArgumentException("Enter a numeric user ID.");
                response = await Api.Auth(a, $"https://accountsettings.roblox.com/v1/users/{userId}/{tool}", HttpMethod.Post); break;
            case "friend":
                if (!long.TryParse(value, out var friend) || friend <= 0) throw new ArgumentException("Enter a numeric user ID.");
                response = await Api.Auth(a, $"https://friends.roblox.com/v1/users/{friend}/request-friendship", HttpMethod.Post); break;
            case "quickLogin": response = await Api.Auth(a, "https://apis.roblox.com/auth-token-service/v1/login/quick-login/authorize", HttpMethod.Post, new { code = value }); break;
            default: throw new ArgumentException("Unknown account tool.");
        }
        Log($"@{a.Username}: {tool} completed."); return response;
    }
    private void Seed()
    {
        string[] names = ["asterism", "northbound", "evergreen", "moonlit", "solstice", "horizon", "afterglow"];
        for (int i = 0; i < names.Length; i++) Vault.Data.Accounts.Add(new Account { Username = names[i], DisplayName = char.ToUpper(names[i][0]) + names[i][1..], UserId = 1000 + i, Alias = i == 0 ? "Main account" : "", Group = i < 2 ? "Personal" : i < 5 ? "Play together" : "Studio", Status = i == 0 ? "In game" : i == 2 ? "Online" : "Offline", Robux = i == 0 ? 2450 : i == 2 ? 720 : 0, LastUse = DateTime.UtcNow.AddHours(-i), SecurityToken = "SAMPLE_ACCOUNT_NO_REAL_CREDENTIALS" });
        Vault.Data.Games.AddRange([new Game { PlaceId = 920587237, Name = "Adopt Me!", Creator = "Uplift Games", Favorite = true }, new Game { PlaceId = 4924922222, Name = "Brookhaven RP", Creator = "Brookhaven", Favorite = true }, new Game { PlaceId = 2753915549, Name = "Blox Fruits", Creator = "Gamer Robot Inc" }]);
        Log("Preview workspace loaded. All accounts shown are sample data.");
    }
    public void Dispose() { lifetime.Cancel(); timer.Stop(); timer.Dispose(); Server?.Dispose(); multi?.Dispose(); Api.Dispose(); }
}

public static class ProcessTracker
{
    // Windows' process command-line query is used only to identify launches made by Orbit.
    public static int? Find(string tracker)
    {
        if (tracker.Length == 0) return null;
        foreach (var process in Process.GetProcessesByName("RobloxPlayerBeta"))
        {
            using (process)
            {
                try
                {
                    var buffer = Marshal.AllocHGlobal(65536);
                    try
                    {
                        if (NtQueryInformationProcess(process.Handle, 60, buffer, 65536, out _) != 0) continue;
                        var str = Marshal.PtrToStructure<UnicodeString>(buffer);
                        var cmd = Marshal.PtrToStringUni(str.Buffer, str.Length / 2) ?? "";
                        if (System.Text.RegularExpressions.Regex.IsMatch(cmd, @"(?:-b\s+|browsertrackerid:)" + tracker + @"(?:\s|\+|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) return process.Id;
                    }
                    finally { Marshal.FreeHGlobal(buffer); }
                }
                catch { }
            }
        }
        return null;
    }
    [StructLayout(LayoutKind.Sequential)] private struct UnicodeString { public ushort Length, MaximumLength; public IntPtr Buffer; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("ntdll.dll")] private static extern int NtQueryInformationProcess(IntPtr handle, int info, IntPtr buffer, int length, out int returned);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr handle, out Rect rect);
    [DllImport("user32.dll")] private static extern bool MoveWindow(IntPtr handle, int x, int y, int width, int height, bool repaint);
    public static void Capture(Account a)
    {
        try { using var p = Process.GetProcessById(a.ProcessId!.Value); if (GetWindowRect(p.MainWindowHandle, out var r) && r.Right > r.Left && r.Left > -30000) a.Fields["Orbit.Window"] = $"{r.Left},{r.Top},{r.Right - r.Left},{r.Bottom - r.Top}"; } catch { }
    }
    public static void Restore(Account a)
    {
        try { if (!a.Fields.TryGetValue("Orbit.Window", out var text)) return; var n = text.Split(',').Select(int.Parse).ToArray(); if (n.Length != 4 || n[2] < 100 || n[3] < 100) return; using var p = Process.GetProcessById(a.ProcessId!.Value); MoveWindow(p.MainWindowHandle, n[0], n[1], n[2], n[3], true); } catch { }
    }
}
