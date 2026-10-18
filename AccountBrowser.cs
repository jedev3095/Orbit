using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Orbit;

public sealed class AccountBrowser : Form
{
    private readonly AppService app;
    private readonly Account? account;
    private readonly WebView2 view = new() { Dock = DockStyle.Fill };
    private readonly ToolStripButton capture = new("Use this account");
    private readonly ToolStripSplitButton bitwarden = new("Bitwarden") { Enabled = false };
    private readonly ToolStripLabel status = new("Sign in on Roblox, then choose Use this account.");
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
        Controls.Add(view); Controls.Add(toolbar); capture.Click += async (_, _) => await CaptureAccount();
        Load += async (_, _) => await Initialize();
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
        }
        catch (Exception)
        {
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
    private async Task CaptureAccount()
    {
        capture.Enabled = false;
        try
        {
            var cookies = await view.CoreWebView2.CookieManager.GetCookiesAsync("https://www.roblox.com");
            var cookie = cookies.FirstOrDefault(c => c.Name == ".ROBLOSECURITY");
            if (cookie is null) throw new InvalidOperationException("Finish signing in to Roblox first.");
            var identified = await app.Api.Identify(cookie.Value);
            app.Vault.Merge([identified]); app.Log($"Added or refreshed @{identified.Username} from Roblox sign-in."); Close();
        }
        catch (Exception ex) { status.Text = ex.Message; capture.Enabled = true; }
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
