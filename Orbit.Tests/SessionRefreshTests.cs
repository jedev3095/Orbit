using System.Net;
using System.Text.Json;
using Xunit;

namespace Orbit;

public sealed class SessionRefreshTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) { Calls++; return Task.FromResult(reply(request)); }
    }
    private static HttpResponseMessage Response(HttpStatusCode status, long id = 42) => new(status) { Content = new StringContent($"{{\"id\":{id},\"name\":\"test-{id}\",\"displayName\":\"Test\"}}") };
    private static Account Account(long id, bool expired = true) => new() { UserId = id, Username = $"test-{id}", SecurityToken = $"synthetic_cookie_for_account_{id}_123456789", Status = expired ? "Needs login" : "Unchecked" };
    private static AppService App(Handler handler) => new(new Vault(Path.Combine(Path.GetTempPath(),"Orbit.Tests",Guid.NewGuid().ToString("N"))), api: new RobloxApi(handler));
    private static void Add(AppService app, Account account, bool password = true)
    {
        app.Vault.Data.Accounts.Add(account);
        if (password) app.Credentials.Save(new() { Username = account.Username, Password = "synthetic_queue_password_" + account.UserId, LinkedUserId = account.UserId });
    }
    [Fact] public async Task ChecksSessionsAndRefreshesOnlyExpiredAccountsSequentially()
    {
        var handler = new Handler(r => r.Headers.GetValues("Cookie").Single().Contains("account_42_") ? Response(HttpStatusCode.OK,42) : Response(HttpStatusCode.Unauthorized));
        using var app = App(handler); Add(app,Account(42,false)); Add(app,Account(43,false)); Add(app,Account(44));
        var seen = new List<long>(); int active=0,maxActive=0;
        app.SessionRefresh.Start(async (account,login,token) =>
        {
            active++; maxActive=Math.Max(maxActive,active); seen.Add(account.UserId);
            Assert.Equal(account.UserId,login.LinkedUserId); await Task.Delay(10,token); account.Status="Offline"; active--; return true;
        });
        await app.SessionRefresh.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new long[]{43,44},seen); Assert.Equal(1,maxActive); Assert.Equal(2,handler.Calls);
        Assert.Equal(1,app.SessionRefresh.Healthy); Assert.Equal(2,app.SessionRefresh.Refreshed); Assert.Equal(3,app.SessionRefresh.Processed);
        Assert.Equal("Offline",app.Find(app.Vault.Data.Accounts[1].Id).Status);
        Assert.DoesNotContain("synthetic_queue_password",Json.Write(app.State())); Assert.DoesNotContain("synthetic_cookie",Json.Write(app.SessionRefresh.State));
    }
    [Fact] public async Task MissingAmbiguousAndWrongAccountLoginsAreSkippedWithoutGuessing()
    {
        using var app=App(new Handler(_ => Response(HttpStatusCode.Unauthorized)));
        Add(app,Account(42),false); Add(app,Account(43)); Add(app,Account(44),false);
        app.Credentials.Save(new() {Username="test-43",Password="different-synthetic-password",LinkedUserId=43});
        app.Credentials.Save(new() {Username="test-44",Password="wrong-account-password",LinkedUserId=900});
        int called=0; app.SessionRefresh.Start((a,l,c)=>{called++;return Task.FromResult(true);}); await app.SessionRefresh.Completion;
        Assert.Equal(0,called); Assert.Equal(3,app.SessionRefresh.Skipped); Assert.Equal(0,app.SessionRefresh.Refreshed);
    }
    [Fact] public async Task SkipAndStopCancelTheCurrentSignInAndPreventOverlappingRuns()
    {
        using var app=App(new Handler(_ => Response(HttpStatusCode.Unauthorized))); Add(app,Account(42)); Add(app,Account(43)); Add(app,Account(44));
        var first=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var second=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int active=0,calls=0,maxActive=0;
        app.SessionRefresh.Start(async(a,l,token)=>
        {
            active++;maxActive=Math.Max(maxActive,active);calls++;if(calls==1)first.SetResult();else second.SetResult();
            try { await Task.Delay(Timeout.Infinite,token);return false; } finally { active--; }
        });
        await first.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.False(app.SessionRefresh.Start((a,l,c)=>Task.FromResult(true)));
        app.SessionRefresh.SkipCurrent();await second.Task.WaitAsync(TimeSpan.FromSeconds(5));app.SessionRefresh.Stop();await app.SessionRefresh.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2,calls);Assert.Equal(1,maxActive);Assert.Equal(0,active);Assert.Equal("Stopped",app.SessionRefresh.Phase);Assert.False(app.SessionRefresh.IsRunning);
        Assert.Equal(2,app.SessionRefresh.Skipped);
    }
    [Fact] public async Task TimeoutAndFailedSignInMoveOnWithoutRetryingPasswords()
    {
        using var app=App(new Handler(_ => Response(HttpStatusCode.Unauthorized)));Add(app,Account(42));Add(app,Account(43));Add(app,Account(44));
        using var queue=new SessionRefreshService(app,TimeSpan.FromMilliseconds(80));var calls=new List<long>();
        queue.Start(async(a,l,token)=>
        {
            calls.Add(a.UserId);
            if(a.UserId==42)await Task.Delay(Timeout.Infinite,token);
            if(a.UserId==43)throw new InvalidOperationException(l.Password);
            return true;
        });
        await queue.Completion.WaitAsync(TimeSpan.FromSeconds(5));Assert.Equal(new long[]{42,43,44},calls);
        Assert.Equal(1,queue.Skipped);Assert.Equal(1,queue.Failed);Assert.Equal(1,queue.Refreshed);
        Assert.DoesNotContain("synthetic_queue_password",Json.Write(app.Activity));
    }
    [Fact] public async Task RateLimitStopsTheBatchAndTransientErrorsDoNotTriggerPasswordLogins()
    {
        using var limited=App(new Handler(_=>Response((HttpStatusCode)429)));Add(limited,Account(42,false));Add(limited,Account(43));
        int signIns=0;limited.SessionRefresh.Start((a,l,c)=>{signIns++;return Task.FromResult(true);});await limited.SessionRefresh.Completion;
        Assert.Equal(0,signIns);Assert.Equal(1,limited.SessionRefresh.Failed);Assert.Equal("Rate limited — try again later",limited.SessionRefresh.Phase);
        using var transient=App(new Handler(_=>Response(HttpStatusCode.ServiceUnavailable)));Add(transient,Account(42,false));
        transient.SessionRefresh.Start((a,l,c)=>{signIns++;return Task.FromResult(true);});await transient.SessionRefresh.Completion;
        Assert.Equal(0,signIns);Assert.Equal(1,transient.SessionRefresh.Failed);Assert.Equal("Unchecked",transient.Vault.Data.Accounts[0].Status);
    }
    [Fact] public async Task DeletedAccountsAreSkippedAndEmptyRunsCanRestart()
    {
        using var app=App(new Handler(_=>Response(HttpStatusCode.Unauthorized)));Add(app,Account(42));Add(app,Account(43));
        app.SessionRefresh.Start((a,l,c)=>{app.Vault.Data.Accounts.RemoveAll(x=>x.UserId==43);return Task.FromResult(true);});await app.SessionRefresh.Completion;
        Assert.Equal(1,app.SessionRefresh.Refreshed);Assert.Equal(1,app.SessionRefresh.Skipped);
        app.Vault.Data.Accounts.Clear();Assert.True(app.SessionRefresh.Start((a,l,c)=>Task.FromResult(true)));await app.SessionRefresh.Completion;Assert.Equal(0,app.SessionRefresh.Total);
    }
    [Fact] public async Task SessionForAnotherUserIsTreatedAsNeedingSignIn()
    {
        using var app=App(new Handler(_=>Response(HttpStatusCode.OK,999)));Add(app,Account(42,false));
        int called=0;app.SessionRefresh.Start((a,l,c)=>{called++;return Task.FromResult(false);});await app.SessionRefresh.Completion;
        Assert.Equal(1,called);Assert.Equal("Needs login",app.Vault.Data.Accounts[0].Status);Assert.Equal(0,app.SessionRefresh.Healthy);
    }
    [OptionalFact("ORBIT_UI_TESTS")]
    public Task QueueAutofillsAndValidatesTwoSyntheticAccountsOneBrowserAtATime() => Sta.Run(async()=>
    {
        using var app=App(new Handler(r=>Response(HttpStatusCode.OK,r.Headers.GetValues("Cookie").Single().Contains("account_42_")?42:43)));
        Add(app,Account(42));Add(app,Account(43));int maxBrowsers=0;
        app.SessionRefresh.Start(async(account,login,token)=>
        {
            using var browser=new AccountBrowser(app,account,refreshSession:true,credentialId:login.Id);
            browser.View.CoreWebView2InitializationCompleted+=(_,_)=>browser.View.CoreWebView2.NavigationStarting+=(_,e)=>{if(!e.Uri.Contains("orbit-test-local=1",StringComparison.Ordinal))e.Cancel=true;};
            var streams=new List<MemoryStream>();
            try
            {
                browser.Show();maxBrowsers=Math.Max(maxBrowsers,Application.OpenForms.OfType<AccountBrowser>().Count());await browser.Ready.WaitAsync(TimeSpan.FromSeconds(20));
                var core=browser.View.CoreWebView2;
                var cookie=core.CookieManager.CreateCookie(".ROBLOSECURITY",account.SecurityToken,".roblox.com","/");cookie.IsSecure=true;cookie.IsHttpOnly=true;core.CookieManager.AddOrUpdateCookie(cookie);
                core.AddWebResourceRequestedFilter("https://www.roblox.com/*",Microsoft.Web.WebView2.Core.CoreWebView2WebResourceContext.All);
                core.WebResourceRequested+=(_,e)=>
                {
                    string html=e.Request.Uri.Contains("/login")?"<input id='login-username'><input id='login-password' type='password'><button id='login-button'>Log in</button><script>document.getElementById('login-button').onclick=()=>{if(document.getElementById('login-username').value==="+Json.Write(login.Username)+"&&document.getElementById('login-password').value==="+Json.Write(login.Password)+")location.href='/home?orbit-test-local=1'}</script>":"<p>Synthetic account home</p>";
                    var stream=new MemoryStream(System.Text.Encoding.UTF8.GetBytes(html));streams.Add(stream);e.Response=core.Environment.CreateWebResourceResponse(stream,200,"OK","Content-Type: text/html");
                };
                core.Navigate("https://www.roblox.com/login?orbit-test-local=1");return await browser.Completion.WaitAsync(TimeSpan.FromSeconds(20));
            }
            finally {if(!browser.IsDisposed)browser.Close();foreach(var stream in streams)stream.Dispose();}
        });
        await app.SessionRefresh.Completion.WaitAsync(TimeSpan.FromSeconds(45));Assert.Equal(2,app.SessionRefresh.Refreshed);Assert.Equal(1,maxBrowsers);
        Assert.All(app.Vault.Data.Accounts,a=>{Assert.Equal("Offline",a.Status);Assert.NotNull(app.Credentials.ForAccount(a)?.VerifiedUtc);});
        Assert.DoesNotContain("synthetic_queue_password",Json.Write(app.State()));
    });
    [OptionalFact("ORBIT_UI_TESTS")]
    public Task NativeQueueSkipClosesCurrentBrowserAndContinuesWithoutSaving() => Sta.Run(async()=>
    {
        using var app=App(new Handler(_=>Response(HttpStatusCode.OK)));Add(app,Account(42));Add(app,Account(43));int opened=0;
        app.SessionRefresh.Start(async(account,login,token)=>
        {
            using var browser=new AccountBrowser(app,account,refreshSession:true,queuedRefresh:true);
            browser.View.CoreWebView2InitializationCompleted+=(_,_)=>browser.View.CoreWebView2.NavigationStarting+=(_,e)=>e.Cancel=true;
            browser.Show();opened++;await browser.Ready.WaitAsync(TimeSpan.FromSeconds(20));
            using var registration=token.Register(()=>browser.BeginInvoke(()=>browser.Close()));
            var buttons=browser.Controls.OfType<ToolStrip>().SelectMany(t=>t.Items.OfType<ToolStripButton>()).ToArray();
            Assert.Contains(buttons,b=>b.Text=="Stop queue");Assert.Single(buttons,b=>b.Text=="Skip account").PerformClick();
            Assert.False(await browser.Completion.WaitAsync(TimeSpan.FromSeconds(10)));return false;
        });
        await app.SessionRefresh.Completion.WaitAsync(TimeSpan.FromSeconds(35));Assert.Equal(2,opened);Assert.Equal(2,app.SessionRefresh.Skipped);
        Assert.All(app.Vault.Data.Accounts,a=>Assert.Equal("Needs login",a.Status));
    });
    [OptionalFact("ORBIT_UI_TESTS")]
    public Task NativeDashboardStartsQueueAndDisplaysProgressAndSkipControls() => Sta.Run(async()=>
    {
        using var app=App(new Handler(_=>Response(HttpStatusCode.Unauthorized)));Add(app,Account(42),false);
        app.Vault.Data.Settings.MultiInstance=false;app.Vault.Data.Settings.ApiEnabled=false;
        using var window=new MainWindow(app){Size=new(1080,680)};window.Show();await window.Ready.WaitAsync(TimeSpan.FromSeconds(20));await Task.Delay(150);
        var core=window.View.CoreWebView2;
        await core.ExecuteScriptAsync("document.querySelector('[data-action=refreshExpired]').click()");
        for(int i=0;i<60&&app.SessionRefresh.Total==0;i++)await Task.Delay(50);
        await app.SessionRefresh.Completion.WaitAsync(TimeSpan.FromSeconds(5));await Task.Delay(120);
        Assert.Equal(1,app.SessionRefresh.Skipped);Assert.Contains("Finished",await core.ExecuteScriptAsync("document.querySelector('.session-refresh-bar').innerText"));
        var account=app.Vault.Data.Accounts[0];app.Credentials.Save(new(){Username=account.Username,Password="synthetic-native-queue-password",LinkedUserId=42});
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        app.SessionRefresh.Start(async(a,l,c)=>{entered.SetResult();await Task.Delay(Timeout.Infinite,c);return false;});await entered.Task;await Task.Delay(120);
        Assert.Equal("true",await core.ExecuteScriptAsync("document.querySelector('[data-action=refreshExpired]').disabled"));
        Assert.Equal("true",await core.ExecuteScriptAsync("!!document.querySelector('[data-action=skipSessionRefresh]') && !!document.querySelector('[data-action=stopSessionRefresh]')"));
        string folder=Path.Combine(AppContext.BaseDirectory,"TestResults","ui");Directory.CreateDirectory(folder);
        using(var file=File.Create(Path.Combine(folder,"session-refresh.png")))await core.CapturePreviewAsync(Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png,file);
        await core.ExecuteScriptAsync("document.querySelector('[data-action=skipSessionRefresh]').click()");await app.SessionRefresh.Completion.WaitAsync(TimeSpan.FromSeconds(5));Assert.Equal(1,app.SessionRefresh.Skipped);
        window.Close();
    });
}
