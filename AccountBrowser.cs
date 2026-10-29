using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Orbit;

public sealed class AccountBrowser : Form
{
    private readonly AppService app;
    private readonly Account? account;
    private readonly WebView2 view = new() { Dock = DockStyle.Fill };
    private readonly ToolStripButton capture = new("Use this account") { Enabled = false };
    private readonly ToolStripButton savedLogins = new("Saved logins") { Enabled = false };
    private readonly bool refreshSession;
    private Guid? credentialId;
    private bool filling, filled;
    private readonly ToolStripLabel status = new("Sign in on Roblox, then choose Use this account.");
    private readonly Label detail = new() { Dock = DockStyle.Top, Height = 64, Padding = new Padding(12), BackColor = Color.FromArgb(235, 240, 248), ForeColor = Color.FromArgb(35, 48, 65), Text = "Sign in on Roblox. Orbit will validate and save the account automatically when the home page opens." };
    private bool capturing, saved, autoScheduled;
    private readonly CancellationTokenSource lifetime = new();
    private readonly HashSet<string> automaticAttempts = [];
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task Ready => ready.Task;
    internal WebView2 View => view;
    private CoreWebView2Environment? environment;
    public bool IsSignIn => account is null || refreshSession;
    public Guid? AccountId => account?.Id;
    public AccountBrowser(AppService app, Account? account = null, bool refreshSession = false, Guid? credentialId = null)
    {
        this.app = app; this.account = account; this.refreshSession = refreshSession; this.credentialId = credentialId;
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        Text = account is null ? "Add account — Orbit" : $"@{account.Username} — Orbit browser";
        Size = new Size(1050, 820); StartPosition = FormStartPosition.CenterParent;
        var toolbar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Padding = new Padding(8), BackColor = Color.FromArgb(238, 240, 242) };
        var back = new ToolStripButton("←"); back.Click += (_, _) => { if (view.CanGoBack) view.GoBack(); };
        var refresh = new ToolStripButton("Reload"); refresh.Click += (_, _) => view.Reload();
        savedLogins.Click += (_, _) => new PasswordManagerWindow(app.Credentials, id => _ = SelectSavedLogin(id), account).Show(this);
        toolbar.Items.AddRange([back, refresh, new ToolStripSeparator(), savedLogins, new ToolStripSeparator(), capture, new ToolStripSeparator(), status]);
        Controls.Add(view); Controls.Add(detail); Controls.Add(toolbar); capture.Click += async (_, _) => await CaptureAccount();
        Load += async (_, _) => await Initialize();
        FormClosing += (_, _) => lifetime.Cancel();
    }
    private static bool Roblox(string uri) => Uri.TryCreate(uri, UriKind.Absolute, out var u) && u.Scheme == "https" && (u.Host == "roblox.com" || u.Host.EndsWith(".roblox.com", StringComparison.OrdinalIgnoreCase));
    private async Task Initialize()
    {
        try
        {
            string profile = IsSignIn ? "sign-in-" + (account?.Id.ToString("N") ?? "new") : account!.Id.ToString("N");
            environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(app.Vault.DirectoryPath, "Browsers", profile), new CoreWebView2EnvironmentOptions { AreBrowserExtensionsEnabled = false });
            await view.EnsureCoreWebView2Async(environment);
            var core = view.CoreWebView2;
            core.Settings.IsWebMessageEnabled = false; core.Settings.AreHostObjectsAllowed = false; core.Settings.AreDevToolsEnabled = false;
            core.Profile.IsPasswordAutosaveEnabled = false;
            core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            core.NavigationStarting += (_, e) => { if (!Roblox(e.Uri) && e.Uri != "about:blank") { e.Cancel = true; status.Text = "This account window only opens Roblox pages."; } };
            core.NewWindowRequested += (_, e) => { e.Handled = true; if (Roblox(e.Uri)) core.Navigate(e.Uri); };
            core.NavigationCompleted += async (_, e) => { if (e.IsSuccess) { await FillSavedLogin(); await CompleteAfterSignIn(); } };
            core.SourceChanged += async (_, _) => { await FillSavedLogin(); await CompleteAfterSignIn(); };
            savedLogins.Enabled = true;
            if (!IsSignIn)
            {
                var cookie = core.CookieManager.CreateCookie(".ROBLOSECURITY", account!.SecurityToken, ".roblox.com", "/");
                cookie.IsHttpOnly = true; cookie.IsSecure = true; core.CookieManager.AddOrUpdateCookie(cookie);
                status.Text = "Separate browser profile for this account.";
            }
            else
            {
                var cookies = await core.CookieManager.GetCookiesAsync("https://www.roblox.com");
                foreach (var cookie in cookies) core.CookieManager.DeleteCookie(cookie);
                status.Text = "Sign in yourself, or choose Saved logins.";
            }
            core.Navigate(IsSignIn ? "https://www.roblox.com/login" : "https://www.roblox.com/home");
            capture.Enabled = true;
            if (!IsSignIn) detail.Text = "This browser uses the selected account’s saved session. Use this account refreshes its entry after signing in again.";
            ready.TrySetResult();
        }
        catch (Exception)
        {
            ready.TrySetException(new InvalidOperationException("The account browser could not initialize."));
            MessageBox.Show("The account browser could not open. Check WebView2 and try again.", "Orbit browser"); Close();
        }
    }
    private async Task FillSavedLogin()
    {
        if (credentialId is not Guid id || filling || filled || IsDisposed || !LoginAutofill.LoginPage(view.Source?.AbsoluteUri ?? "")) return;
        filling = true;
        try
        {
            var entry = app.Credentials.Get(id);
            if (account is not null && entry.LinkedUserId is long linked && linked != account.UserId) throw new InvalidOperationException("Choose the saved login for this account.");
            for (int attempt = 0; attempt < 20; attempt++)
            {
                lifetime.Token.ThrowIfCancellationRequested();
                if (IsDisposed || credentialId != id || !LoginAutofill.LoginPage(view.Source?.AbsoluteUri ?? "")) return;
                string result = await view.CoreWebView2.ExecuteScriptAsync(LoginAutofill.Script(entry));
                using var report = System.Text.Json.JsonDocument.Parse(result);
                if (report.RootElement.TryGetProperty("filled", out var value) && value.GetBoolean())
                {
                    filled = true; detail.Text = "Saved login filled. Complete Roblox verification if prompted, or press Log In if needed. Orbit saves the refreshed session on the home page.";
                    for (int check = 0; check < 20; check++)
                    {
                        await Task.Delay(200, lifetime.Token);
                        if (IsDisposed || credentialId != id || !LoginAutofill.LoginPage(view.Source?.AbsoluteUri ?? "")) return;
                        if (await view.CoreWebView2.ExecuteScriptAsync(LoginAutofill.SubmitScript(entry)) == "true") break;
                    }
                    return;
                }
                await Task.Delay(250, lifetime.Token);
            }
            detail.Text = "The login form is not ready. Choose Saved logins to retry, or sign in manually.";
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch { if (!IsDisposed) detail.Text = "The saved login could not be filled. Open the local password manager to check it, or sign in manually."; }
        finally { filling = false; }
    }
    private async Task SelectSavedLogin(Guid id)
    {
        if (!LoginAutofill.LoginPage(view.Source?.AbsoluteUri ?? "")) { detail.Text = "Open Add account or Refresh sign-in from the account options to start a fresh login."; return; }
        credentialId = id; filled = false;
        try { while (filling) await Task.Delay(50, lifetime.Token); await FillSavedLogin(); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }
    internal static bool SignedInLanding(string uri) => Uri.TryCreate(uri, UriKind.Absolute, out var url) && url.Scheme == "https" && url.Host == "www.roblox.com" && url.IsDefaultPort && url.AbsolutePath.TrimEnd('/') == "/home";
    private async Task CompleteAfterSignIn()
    {
        if (!IsSignIn || saved || capturing || autoScheduled || !SignedInLanding(view.Source?.AbsoluteUri ?? "")) return;
        autoScheduled = true;
        try { await Task.Delay(400, lifetime.Token); if (!IsDisposed && !saved && SignedInLanding(view.Source?.AbsoluteUri ?? "")) await CaptureAccount(true); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        finally { autoScheduled = false; }
    }
    private async Task CaptureAccount(bool automatic = false)
    {
        if (capturing || saved || IsDisposed) return;
        capturing = true; capture.Enabled = false;
        var secrets = new List<string>(); string stage = "reading the browser session";
        try
        {
            var cookies = await view.CoreWebView2.CookieManager.GetCookiesAsync("https://www.roblox.com");
            secrets.AddRange(cookies.Where(c => c.Name == ".ROBLOSECURITY").OrderBy(c => c.Path == "/" ? 0 : 1).Select(c => c.Value).Distinct().Take(4));
            if (secrets.Count == 0)
            {
                if (automatic) { detail.Text = "Roblox has not provided a signed-in session yet. Finish any verification, then choose Use this account."; return; }
                throw new InvalidOperationException("Finish signing in and complete Roblox verification first, then choose Use this account.");
            }
            if (automatic && !automaticAttempts.Add(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join('\n', secrets)))))) return;
            stage = "validating the session with Roblox"; detail.Text = "Validating your signed-in account with Roblox…"; detail.BackColor = Color.FromArgb(235, 240, 248); detail.ForeColor = Color.FromArgb(35, 48, 65);
            Account? identified = null;
            for (int i = 0; i < secrets.Count; i++)
            {
                try { identified = await app.AcceptSession(secrets[i], "from Roblox browser sign-in", lifetime.Token, account?.UserId); break; }
                catch (RobloxApiException ex) when (ex.RequiresLogin && i < secrets.Count - 1) { }
            }
            if (identified is null) throw new InvalidOperationException("Roblox did not validate this session. Sign in again or use Default browser tab.");
            if (credentialId is Guid loginId)
            {
                try { app.Credentials.Link(loginId, identified.UserId); }
                catch { app.Log("Account session saved, but its saved-login link could not be updated.", "warning"); }
            }
            saved = true; Close();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            string error = Diagnostics.Redact(ex.Message, secrets.ToArray());
            if (!IsDisposed) { status.Text = "Account was not saved. See the message below."; detail.Text = "Account sign-in failed: " + error; detail.BackColor = Color.FromArgb(255, 235, 235); detail.ForeColor = Color.FromArgb(115, 35, 35); }
            app.Log($"Browser sign-in failed while {stage}: {error}", "error");
        }
        finally { capturing = false; if (!IsDisposed) capture.Enabled = !saved; }
    }
}
