using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Orbit;

public sealed class AccountBrowser : Form
{
    private readonly AppService app;
    private readonly Account? account;
    private readonly WebView2 view = new() { Dock = DockStyle.Fill };
    private readonly ToolStripButton capture = new("Use this account") { Enabled = false };
    private readonly ToolStripSplitButton bitwarden = new("Bitwarden") { Enabled = false };
    private readonly ToolStripLabel status = new("Sign in on Roblox, then choose Use this account.");
    private readonly Label detail = new() { Dock = DockStyle.Top, Height = 64, Padding = new Padding(12), BackColor = Color.FromArgb(235, 240, 248), ForeColor = Color.FromArgb(35, 48, 65), Text = "Sign in on Roblox. Orbit will validate and save the account automatically when the home page opens." };
    private bool capturing, saved, autoScheduled;
    private readonly CancellationTokenSource lifetime = new();
    private readonly HashSet<string> automaticAttempts = [];
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task Ready => ready.Task;
    internal WebView2 View => view;
    private CoreWebView2Environment? environment;
    private ExtensionPackage? extension;
    private BitwardenWindow? popup;
    public bool IsSignIn => account is null;
    public Guid? AccountId => account?.Id;
    public AccountBrowser(AppService app, Account? account = null)
    {
        this.app = app; this.account = account;
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        Text = account is null ? "Add account — Orbit" : $"@{account.Username} — Orbit browser";
        Size = new Size(1050, 820); StartPosition = FormStartPosition.CenterParent;
        var toolbar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Padding = new Padding(8), BackColor = Color.FromArgb(238, 240, 242) };
        var back = new ToolStripButton("←"); back.Click += (_, _) => { if (view.CanGoBack) view.GoBack(); };
        var refresh = new ToolStripButton("Reload"); refresh.Click += (_, _) => view.Reload();
        bitwarden.ButtonClick += async (_, _) => await OpenBitwarden();
        bitwarden.DropDownItems.Add("Choose Bitwarden extension folder…", null, async (_, _) => await ChooseExtension());
        toolbar.Items.AddRange([back, refresh, new ToolStripSeparator(), bitwarden, new ToolStripSeparator(), capture, new ToolStripSeparator(), status]);
        Controls.Add(view); Controls.Add(detail); Controls.Add(toolbar); capture.Click += async (_, _) => await CaptureAccount();
        Load += async (_, _) => await Initialize();
        FormClosing += (_, _) => lifetime.Cancel();
    }
    private static bool Roblox(string uri) => Uri.TryCreate(uri, UriKind.Absolute, out var u) && u.Scheme == "https" && (u.Host == "roblox.com" || u.Host.EndsWith(".roblox.com", StringComparison.OrdinalIgnoreCase));
    private async Task Initialize()
    {
        try
        {
            // Reuse Bitwarden's own Orbit sign-in. Reset only Roblox cookies for the next account.
            string profile = account?.Id.ToString("N") ?? "sign-in";
            environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(app.Vault.DirectoryPath, "BrowsersWithExtensions", profile), new CoreWebView2EnvironmentOptions { AreBrowserExtensionsEnabled = true });
            await view.EnsureCoreWebView2Async(environment);
            var core = view.CoreWebView2;
            core.Settings.IsWebMessageEnabled = false; core.Settings.AreHostObjectsAllowed = false; core.Settings.AreDevToolsEnabled = false;
            core.Profile.IsPasswordAutosaveEnabled = false;
            core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            core.NavigationStarting += (_, e) => { if (!Roblox(e.Uri) && e.Uri != "about:blank") { e.Cancel = true; status.Text = "This account window only opens Roblox pages."; } };
            core.NewWindowRequested += (_, e) => { e.Handled = true; if (Roblox(e.Uri)) core.Navigate(e.Uri); };
            core.NavigationCompleted += async (_, e) => { if (e.IsSuccess) await CompleteAfterSignIn(); };
            core.SourceChanged += async (_, _) => await CompleteAfterSignIn();
            try { await InstallExtension(); } catch (Exception ex) { status.Text = "Bitwarden setup: " + ex.Message; }
            bitwarden.Enabled = true;
            if (account is not null)
            {
                var cookie = core.CookieManager.CreateCookie(".ROBLOSECURITY", account.SecurityToken, ".roblox.com", "/");
                cookie.IsHttpOnly = true; cookie.IsSecure = true; core.CookieManager.AddOrUpdateCookie(cookie);
                status.Text = "Separate browser profile for this account.";
            }
            else
            {
                var cookies = await core.CookieManager.GetCookiesAsync("https://www.roblox.com");
                foreach (var cookie in cookies) core.CookieManager.DeleteCookie(cookie);
                status.Text = extension is null ? "Bitwarden not found. Use its menu to choose the extension folder." : "Open Bitwarden to unlock your vault, then sign in to Roblox.";
            }
            core.Navigate(account is null ? "https://www.roblox.com/login" : "https://www.roblox.com/home");
            capture.Enabled = true;
            if (account is not null) detail.Text = "This browser uses the selected account’s saved session. Use this account refreshes its entry after signing in again.";
            ready.TrySetResult();
        }
        catch (Exception)
        {
            ready.TrySetException(new InvalidOperationException("The account browser could not initialize."));
            MessageBox.Show("The account browser could not open. Check WebView2 and try again.", "Orbit browser"); Close();
        }
    }
    private async Task InstallExtension(string? path = null)
    {
        var result = await BitwardenIntegration.Install(view.CoreWebView2, app.Vault.DirectoryPath, path);
        extension = result?.Package; bitwarden.Text = extension is null ? "Set up Bitwarden" : "Bitwarden";
    }
    private async Task ChooseExtension()
    {
        using var dialog = new FolderBrowserDialog { Description = "Choose the Bitwarden extension version folder containing manifest.json", UseDescriptionForTitle = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try { await InstallExtension(dialog.SelectedPath); status.Text = "Bitwarden is ready. Open it to sign in or unlock."; } catch (Exception ex) { status.Text = ex.Message; }
    }
    private async Task OpenBitwarden()
    {
        try
        {
            if (extension is null) { await ChooseExtension(); if (extension is null) return; }
            if (popup is not null && !popup.IsDisposed) { popup.Show(); popup.Activate(); return; }
            popup = new BitwardenWindow(environment!, extension); popup.Show(this); await popup.Ready.WaitAsync(TimeSpan.FromSeconds(20));
        }
        catch (Exception ex) { status.Text = "Bitwarden: " + ex.Message; }
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
                try { identified = await app.AcceptSession(secrets[i], "from Roblox browser sign-in", lifetime.Token); break; }
                catch (RobloxApiException ex) when (ex.RequiresLogin && i < secrets.Count - 1) { }
            }
            if (identified is null) throw new InvalidOperationException("Roblox did not validate this session. Sign in again or use Default browser tab.");
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

public sealed class BitwardenWindow : Form
{
    private readonly WebView2 view = new() { Dock = DockStyle.Fill };
    private readonly CoreWebView2Environment environment;
    private readonly ExtensionPackage package;
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task Ready => ready.Task;
    public BitwardenWindow(CoreWebView2Environment environment, ExtensionPackage package)
    {
        this.environment = environment; this.package = package;
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        Text = "Bitwarden — Orbit"; Size = new Size(440, 690); MinimumSize = new Size(390, 520); StartPosition = FormStartPosition.CenterParent;
        Controls.Add(view); Load += async (_, _) => await Initialize();
    }
    private async Task Initialize()
    {
        try
        {
            await view.EnsureCoreWebView2Async(environment); var core = view.CoreWebView2;
            core.Settings.IsWebMessageEnabled = false; core.Settings.AreHostObjectsAllowed = false; core.Settings.AreDevToolsEnabled = false;
            core.NavigationStarting += (_, e) => { if (!BitwardenIntegration.ExtensionPage(e.Uri, package.Id)) e.Cancel = true; };
            core.NewWindowRequested += (_, e) => { e.Handled = true; if (BitwardenIntegration.ExtensionPage(e.Uri, package.Id)) core.Navigate(e.Uri); };
            core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            core.NavigationCompleted += (_, e) => { if (e.IsSuccess) ready.TrySetResult(); else ready.TrySetException(new InvalidOperationException("Bitwarden could not load: " + e.WebErrorStatus)); };
            core.Navigate(BitwardenIntegration.PopupUrl(package.Id, package.Popup));
        }
        catch (Exception ex) { ready.TrySetException(ex); }
    }
    public async Task CapturePreview(string path) { using var file = File.Create(path); await view.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, file); }
}
