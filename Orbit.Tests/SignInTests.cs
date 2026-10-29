using System.Net;
using Xunit;

namespace Orbit;

internal sealed class SignInFixture(HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(new HttpResponseMessage(status)
    { Content = new StringContent("{\"id\":42,\"name\":\"validated-user\",\"displayName\":\"Validated\"}") });
}

public sealed class SignInTests
{
    private static Vault NewVault() => new(Path.Combine(Path.GetTempPath(), "Orbit.Tests", Guid.NewGuid().ToString("N")));
    private const string Cookie = "synthetic_signin_session_cookie_12345";
    [Fact] public async Task RefreshedSignInIsValidatedAndPreservesAccountCustomizations()
    {
        var vault = NewVault(); vault.Merge([new() { UserId = 42, Username = "old-name", Alias = "My alias", Group = "Imported", Description = "Keep notes", SecurityToken = Cookie }]);
        var before = vault.Data.Accounts.Single(); before.Status = "Needs login"; before.LastError = "old error";
        using var app = new AppService(vault, api: new RobloxApi(new SignInFixture())); Guid? selected = null; app.AccountAccepted += id => selected = id;
        var result = await app.AcceptSession(Cookie, "from synthetic sign-in");
        Assert.Same(before, result); Assert.Equal("Offline", result.Status); Assert.Equal("", result.LastError); Assert.Equal(before.Id, selected);
        Assert.Equal("My alias", result.Alias); Assert.Equal("Imported", result.Group); Assert.Equal("Keep notes", result.Description);
        var reopened = new Vault(vault.DirectoryPath); reopened.Load(); Assert.Equal("validated-user", reopened.Data.Accounts.Single().Username);
        Assert.DoesNotContain(Cookie, Json.Write(app.State()));
    }
    [Fact] public async Task FailedValidationNeverSavesOrAnnouncesAnAccount()
    {
        var vault = NewVault(); using var app = new AppService(vault, api: new RobloxApi(new SignInFixture(HttpStatusCode.Unauthorized)));
        bool announced = false; app.AccountAccepted += _ => announced = true;
        await Assert.ThrowsAsync<RobloxApiException>(() => app.AcceptSession(Cookie, "from synthetic sign-in"));
        Assert.Empty(vault.Data.Accounts); Assert.False(announced); Assert.False(File.Exists(vault.FilePath));
    }
    [Fact] public async Task ClosingSignInBeforeValidationFinishesPreventsSaving()
    {
        var vault = NewVault(); using var app = new AppService(vault, api: new RobloxApi(new SignInFixture()));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => app.AcceptSession(Cookie, "from synthetic sign-in", cancellation.Token));
        Assert.Empty(vault.Data.Accounts);
    }
    [Theory]
    [InlineData("https://www.roblox.com/home", true)]
    [InlineData("https://www.roblox.com/home?source=login", true)]
    [InlineData("https://www.roblox.com/login", false)]
    [InlineData("https://www.roblox.com/login/two-step-verification", false)]
    [InlineData("https://www.roblox.com.attacker.invalid/home", false)]
    public void AutoSaveWaitsForTheRealSignedInLanding(string url, bool expected) => Assert.Equal(expected, AccountBrowser.SignedInLanding(url));

    [OptionalFact("ORBIT_UI_TESTS")]
    public Task BrowserAutomaticallySavesTheValidatedSessionAfterHomeNavigation() => Sta.Run(async () =>
    {
        var vault = NewVault(); using var app = new AppService(vault, api: new RobloxApi(new SignInFixture()));
        using var browser = new AccountBrowser(app);
        // The profile is new and contains only a synthetic cookie. All browser
        // page requests are cancelled or answered locally, not sent to Roblox.
        browser.View.CoreWebView2InitializationCompleted += (_, _) => browser.View.CoreWebView2.NavigationStarting += (_, e) => { if (!e.Uri.Contains("orbit-test-local=1", StringComparison.Ordinal)) e.Cancel = true; };
        var accepted = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously); app.AccountAccepted += id => accepted.TrySetResult(id);
        browser.Show(); await browser.Ready.WaitAsync(TimeSpan.FromSeconds(45));
        var core = browser.View.CoreWebView2;
        var cookie = core.CookieManager.CreateCookie(".ROBLOSECURITY", Cookie, ".roblox.com", "/"); cookie.IsSecure = true; cookie.IsHttpOnly = true; core.CookieManager.AddOrUpdateCookie(cookie);
        using var html = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("<!doctype html><title>Synthetic signed-in page</title><p>Test home page</p>"));
        core.AddWebResourceRequestedFilter("https://www.roblox.com/*", Microsoft.Web.WebView2.Core.CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, e) => { html.Position = 0; e.Response = core.Environment.CreateWebResourceResponse(html, 200, "OK", "Content-Type: text/html"); };
        core.Navigate("https://www.roblox.com/home?orbit-test-local=1");
        Guid id = await accepted.Task.WaitAsync(TimeSpan.FromSeconds(15)); await Task.Delay(50);
        var saved = Assert.Single(vault.Data.Accounts); Assert.Equal(id, saved.Id); Assert.Equal("Offline", saved.Status); Assert.Equal(Cookie, saved.SecurityToken); Assert.True(browser.IsDisposed);
    });

    [OptionalFact("ORBIT_UI_TESTS")]
    public Task SavedPasswordFillsAndRefreshesOnlyTheExpectedAccount() => Sta.Run(async () =>
    {
        var vault = NewVault(); vault.Merge([new() { UserId = 42, Username = "validated-user", Alias = "Keep this alias", SecurityToken = Cookie }]); var account = vault.Data.Accounts.Single(); account.Status = "Needs login";
        using var app = new AppService(vault, api: new RobloxApi(new SignInFixture()));
        var login = new SavedLogin { Username = "validated-user", Password = "quotes\" and comma, plus </script> \\ password" }; app.Credentials.Save(login);
        using var browser = new AccountBrowser(app, account, refreshSession: true, credentialId: login.Id);
        browser.View.CoreWebView2InitializationCompleted += (_, _) => browser.View.CoreWebView2.NavigationStarting += (_, e) => { if (!e.Uri.Contains("orbit-test-local=1", StringComparison.Ordinal)) e.Cancel = true; };
        var accepted = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously); app.AccountAccepted += id => accepted.TrySetResult(id);
        browser.Show(); await browser.Ready.WaitAsync(TimeSpan.FromSeconds(20)); var core = browser.View.CoreWebView2;
        var cookie = core.CookieManager.CreateCookie(".ROBLOSECURITY", Cookie, ".roblox.com", "/"); cookie.IsSecure = true; cookie.IsHttpOnly = true; core.CookieManager.AddOrUpdateCookie(cookie);
        var streams = new List<MemoryStream>(); int intercepted=0;
        core.AddWebResourceRequestedFilter("https://www.roblox.com/*", Microsoft.Web.WebView2.Core.CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, e) =>
        {
            intercepted++;
            string html = e.Request.Uri.Contains("/login") ? "<title>Synthetic local login</title><input id='login-username'><input id='login-password' type='password'><button id='login-button' disabled>Log in</button><script>" +
                "for(const input of document.querySelectorAll('input'))input.addEventListener('input',()=>setTimeout(()=>document.getElementById('login-button').disabled=false,50));" +
                "window.syntheticClicks=0;document.getElementById('login-button').onclick=()=>{window.syntheticClicks++;if(document.getElementById('login-username').value==='validated-user'&&document.getElementById('login-password').value===" + Json.Write(login.Password) + ")location.href='/home?orbit-test-local=1'};</script>" : "<p>Synthetic signed-in home</p>";
            var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(html)); streams.Add(stream); e.Response = core.Environment.CreateWebResourceResponse(stream, 200, "OK", "Content-Type: text/html");
        };
        core.Navigate("https://www.roblox.com/login?orbit-test-local=1");
        Guid id = await accepted.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(intercepted >= 2, "Login and home pages must both be intercepted locally.");
        await Task.Delay(50);
        Assert.Equal(account.Id, id); Assert.Equal("Offline", account.Status); Assert.Equal("Keep this alias", account.Alias); Assert.Equal(42, app.Credentials.Get(login.Id).LinkedUserId); Assert.True(browser.IsDisposed);
        foreach (var stream in streams) stream.Dispose();
    });

    [OptionalFact("ORBIT_UI_TESTS")]
    public Task AcceptedAccountBecomesVisibleDespiteFilters() => Sta.Run(async () =>
    {
        var vault = NewVault(); using var app = new AppService(vault, api: new RobloxApi(new SignInFixture()));
        using var window = new MainWindow(app); window.Show(); await window.Ready.WaitAsync(TimeSpan.FromSeconds(20)); await Task.Delay(200);
        await window.View.CoreWebView2.ExecuteScriptAsync("page='games';query='hidden by old search';group='different group';render()");
        var account = await app.AcceptSession(Cookie, "from synthetic sign-in"); await Task.Delay(100);
        string json = await window.View.CoreWebView2.ExecuteScriptAsync($"({{visible:document.querySelectorAll('[data-select]').length,selected:selected.has('{account.Id}'),page,query,group,status:state.accounts[0].status}})");
        using var report = System.Text.Json.JsonDocument.Parse(json); var root = report.RootElement;
        Assert.Equal(1, root.GetProperty("visible").GetInt32()); Assert.True(root.GetProperty("selected").GetBoolean());
        Assert.Equal("accounts", root.GetProperty("page").GetString()); Assert.Equal("", root.GetProperty("query").GetString()); Assert.Equal("", root.GetProperty("group").GetString()); Assert.Equal("Offline", root.GetProperty("status").GetString());
        window.Close();
    });
}
