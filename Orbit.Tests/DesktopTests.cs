using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Xunit;

namespace Orbit;

public sealed class OptionalFactAttribute : FactAttribute
{
    public OptionalFactAttribute(string variable) { if (Environment.GetEnvironmentVariable(variable) != "1") Skip = "Opt-in desktop/integration test."; }
}
internal static class Sta
{
    public static Task Run(Func<Task> action)
    {
        var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            using var context = new ApplicationContext();
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            bool started = false;
            async void Idle(object? sender, EventArgs args)
            {
                if (started) return; started = true; Application.Idle -= Idle;
                try { await action(); result.TrySetResult(); } catch (Exception ex) { result.TrySetException(ex); }
                finally { context.ExitThread(); }
            }
            Application.Idle += Idle; Application.Run(context);
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return result.Task;
    }
}
public sealed class DesktopTests
{
    [OptionalFact("ORBIT_UI_TESTS")]
    public Task DashboardInNativeWebView() => Sta.Run(async () =>
    {
        string output = Path.Combine(AppContext.BaseDirectory, "TestResults", "ui"); Directory.CreateDirectory(output);
        foreach (bool narrow in new[] { false, true })
        {
            var vault = new Vault(Path.Combine(Path.GetTempPath(), "Orbit.Tests", Guid.NewGuid().ToString("N")));
            using var app = new AppService(vault, true, new RobloxApi(new SearchFixture())); TestSeed.Fill(app);
            using var window = new MainWindow(app); if (narrow) window.Size = new(880, 560);
            window.Show(); await window.Ready.WaitAsync(TimeSpan.FromSeconds(20)); await Task.Delay(600);
            await window.View.CoreWebView2.ExecuteScriptAsync(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "UiSmoke.js")));
            await window.View.CoreWebView2.ExecuteScriptAsync("orbitSmoke().then(r=>window.testResult=r).catch(e=>window.testResult={passed:false,error:e.message})");
            string json = "null";
            for (int i = 0; i < 100 && json == "null"; i++) { await Task.Delay(100); json = await window.View.CoreWebView2.ExecuteScriptAsync("window.testResult ?? null"); }
            File.WriteAllText(Path.Combine(output, narrow ? "narrow.json" : "normal.json"), json);
            using var report = JsonDocument.Parse(json); Assert.True(report.RootElement.GetProperty("passed").GetBoolean(), json);
            foreach (string page in new[] { "accounts", "games", "settings" })
            {
                await window.View.CoreWebView2.ExecuteScriptAsync($"document.querySelector('nav [data-page=\"{page}\"]').click()");
                await Task.Delay(100); using var image = File.Create(Path.Combine(output, $"{page}-{(narrow ? "narrow" : "normal")}.png"));
                await window.View.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, image);
            }
            window.Close();
        }
    });

    [OptionalFact("ORBIT_LIVE_TEST")]
    public Task TwoLiveClients() => Sta.Run(async () =>
    {
        string[] names = (Environment.GetEnvironmentVariable("ORBIT_LIVE_ACCOUNTS") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (names.Length != 2 || !long.TryParse(Environment.GetEnvironmentVariable("ORBIT_LIVE_PLACE"), out long place) || place <= 0) throw new InvalidOperationException("Provide two accounts and a place in the live test environment variables.");
        var real = new Vault(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Orbit")); real.Load();
        string path = Path.Combine(Path.GetTempPath(), "Orbit.Tests", Guid.NewGuid().ToString("N"));
        var test = new Vault(path);
        foreach (string name in names)
        {
            var saved = real.Data.Accounts.SingleOrDefault(a => a.Username.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidOperationException("A selected live test account was not found.");
            var copy = Json.Read<Account>(Json.Write(saved)); copy.AutoRelaunch = false; test.Data.Accounts.Add(copy);
        }
        test.Data.Settings.MultiInstance = true; test.Data.Settings.RememberWindows = false;
        using var app = new AppService(test);
        bool passed = false; DateTime started = DateTime.UtcNow;
        try
        {
            // Identify both before opening either client. Never print cookies,
            // account names, launch URLs, tickets or process command lines.
            foreach (var account in test.Data.Accounts) await app.Api.Identify(account.SecurityToken);
            app.ApplySettings();
            var launch = await app.Launch(test.Data.Accounts.Select(a => a.Id).ToList(), place, "");
            Assert.Equal(2, launch.Launched); Assert.Equal(0, launch.Failed);
            for (int i = 0; i < 60 && test.Data.Accounts.Any(a => !ProcessTracker.IsRunning(a)); i++) await Task.Delay(1000);
            Assert.True(test.Data.Accounts.All(ProcessTracker.IsRunning), "Both launched client processes must be verified.");
            Assert.Equal(2, test.Data.Accounts.Select(a => a.ProcessId).Distinct().Count());
            await Task.Delay(15000);
            passed = test.Data.Accounts.All(ProcessTracker.IsRunning);
            Assert.True(passed, "Both clients must survive concurrently for the observation period.");
        }
        finally
        {
            foreach (var account in test.Data.Accounts) app.Launches.Stop(account);
            app.Dispose();
            string output = Path.Combine(AppContext.BaseDirectory, "TestResults"); Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, "live-launch.json"), Json.Write(new { passed, placeId = place, requestedClients = 2, observationSeconds = 15, elapsedSeconds = (int)(DateTime.UtcNow - started).TotalSeconds, accountsRedacted = true }));
            string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Orbit.Tests")) + Path.DirectorySeparatorChar;
            if (Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase)) Directory.Delete(path, true);
        }
    });
}
internal sealed class SearchFixture : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
    {
        Content = new StringContent(request.RequestUri!.Host == "thumbnails.roblox.com" ? "{\"data\":[]}" : "{\"searchResults\":[{\"contentGroupType\":\"Game\",\"contents\":[{\"rootPlaceId\":189707,\"universeId\":65241,\"name\":\"Natural Disaster Survival\",\"creatorName\":\"Sample creator\",\"playerCount\":123}]}],\"nextPageToken\":\"synthetic-next-page\"}")
    });
}
