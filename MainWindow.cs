using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Orbit;

public sealed class MainWindow : Form
{
    private readonly WebView2 view = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(24, 26, 29) };
    private readonly AppService app;
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task Ready => ready.Task;
    internal WebView2 View => view;
    private bool initialized, closing;
    public MainWindow(AppService app)
    {
        this.app = app;
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        Text = "orbit"; Size = new Size(1080, 680); MinimumSize = new Size(860, 500);
        StartPosition = FormStartPosition.CenterScreen; BackColor = Color.FromArgb(24, 26, 29);
        Controls.Add(view);
        app.Dispatch = work =>
        {
            var completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (closing || IsDisposed) return Task.FromCanceled<object?>(new CancellationToken(true));
            BeginInvoke(async () => { try { completion.TrySetResult(await work()); } catch (Exception ex) { completion.TrySetException(ex); } });
            return completion.Task;
        };
        app.Changed += Push;
        Load += async (_, _) => await Initialize();
        FormClosing += (_, _) => { closing = true; app.Changed -= Push; app.Vault.Save(); };
    }
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    private async Task Initialize()
    {
        try
        {
            int dark = 1; DwmSetWindowAttribute(Handle, 20, ref dark, 4);
            var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(app.Vault.DirectoryPath, "WebView"));
            await view.EnsureCoreWebView2Async(env);
            var core = view.CoreWebView2;
            core.Settings.AreHostObjectsAllowed = false; core.Settings.AreDefaultScriptDialogsEnabled = false;
            core.Settings.AreDevToolsEnabled = false; core.Settings.IsStatusBarEnabled = false; core.Settings.AreDefaultContextMenusEnabled = false;
            core.SetVirtualHostNameToFolderMapping("orbit.local", Path.Combine(AppContext.BaseDirectory, "ui"), CoreWebView2HostResourceAccessKind.DenyCors);
            core.NavigationStarting += (_, e) => { if (!Input.TrustedOrigin(e.Uri)) e.Cancel = true; };
            core.FrameNavigationStarting += (_, e) => e.Cancel = true;
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            core.WebMessageReceived += Message;
            core.NavigationCompleted += (_, e) =>
            {
                if (!e.IsSuccess) return;
                initialized = true; Push();
                ready.TrySetResult();
            };
            if (!app.IsDemo) { try { app.ApplySettings(); } catch (Exception ex) { app.Log(ex.Message, "error"); } }
            core.Navigate("https://orbit.local/index.html");
        }
        catch (Exception ex) { MessageBox.Show("Orbit needs Microsoft Edge WebView2 Runtime.\n\n" + ex.Message, "Orbit"); Close(); }
    }
    private void Push()
    {
        if (!initialized || closing || IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(Push); return; }
        if (Input.TrustedOrigin(view.Source?.AbsoluteUri ?? "")) view.CoreWebView2.PostWebMessageAsJson(Json.Write(new { type = "state", data = app.State() }));
    }
    private async void Message(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!Input.TrustedOrigin(e.Source) || !Input.TrustedOrigin(view.Source?.AbsoluteUri ?? "") || e.WebMessageAsJson.Length > 131072) return;
        string id = "";
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            id = doc.RootElement.GetProperty("id").GetString() ?? "";
            var action = doc.RootElement.GetProperty("action").GetString() ?? "";
            var p = doc.RootElement.GetProperty("payload");
            object? result;
            switch (action)
            {
                case "configureBackups":
                    if (!app.IsDemo)
                    {
                        using var folder = new FolderBrowserDialog { Description = "Choose where to keep daily encrypted Orbit backups", UseDescriptionForTitle = true };
                        if (folder.ShowDialog(this) == DialogResult.OK) { app.Backups.Configure(folder.SelectedPath, p.GetProperty("password").GetString() ?? ""); await app.Backups.RunNow(); }
                    }
                    result = null; break;
                case "loginExternal":
                    if (!app.IsDemo)
                    {
                        var external = Application.OpenForms.OfType<QuickLoginWindow>().FirstOrDefault();
                        if (external is null) new QuickLoginWindow(app).Show(this);
                        else { external.Show(); external.Activate(); }
                    }
                    result = null; break;
                case "login":
                    if (!app.IsDemo)
                    {
                        var login = Application.OpenForms.OfType<AccountBrowser>().FirstOrDefault(b => b.IsSignIn);
                        if (login is null) new AccountBrowser(app).Show(this);
                        else { login.Show(); login.Activate(); }
                    }
                    result = null; break;
                case "browser":
                    var browserAccount = app.Find(p.GetProperty("id").GetGuid());
                    if (!app.IsDemo)
                    {
                        var browser = Application.OpenForms.OfType<AccountBrowser>().FirstOrDefault(b => b.AccountId == browserAccount.Id);
                        if (browser is null) new AccountBrowser(app, browserAccount).Show(this);
                        else { if (browser.WindowState == FormWindowState.Minimized) browser.WindowState = FormWindowState.Normal; browser.Show(); browser.Activate(); }
                    }
                    result = new { accountId = browserAccount.Id }; break;
                case "import": result = app.IsDemo ? null : Import(p); break;
                case "export": result = app.IsDemo ? null : Export(p); break;
                case "copyApiToken": if (!app.IsDemo) Clipboard.SetText(app.Vault.Data.Settings.ApiToken); result = null; break;
                case "openProfile":
                    if (!app.IsDemo) Process.Start(new ProcessStartInfo($"https://www.roblox.com/users/{app.Find(p.GetProperty("id").GetGuid()).UserId}/profile") { UseShellExecute = true });
                    result = null; break;
                default: result = await app.Command(action, p); break;
            }
            Reply(new { type = "result", id, ok = true, data = result }); Push();
        }
        catch (Exception ex)
        {
            // Errors do not include request payloads, cookie values, launch tickets, or file content.
            Reply(new { type = "result", id, ok = false, error = ex is CryptographicException ? "Could not decrypt the selected file." : ex.Message });
        }
    }
    private void Reply(object result) { if (!closing && Input.TrustedOrigin(view.Source?.AbsoluteUri ?? "")) view.CoreWebView2.PostWebMessageAsJson(Json.Write(result)); }
    private object? Import(JsonElement p)
    {
        using var dialog = new OpenFileDialog { Title = "Import accounts or an Orbit backup", Filter = "Account data|*.json;*.backup;*.orbit;*.dat|All files|*.*" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return null;
        if (new FileInfo(dialog.FileName).Length > 32 * 1024 * 1024) throw new InvalidDataException("Import file is too large.");
        var bytes = File.ReadAllBytes(dialog.FileName);
        var imported = LegacyImport.Read(bytes, p.TryGetProperty("password", out var password) ? password.GetString() ?? "" : "");
        int count = app.Vault.Merge(imported); app.Log($"Imported {count} new accounts; matching sessions were refreshed."); return new { count };
    }
    private object? Export(JsonElement p)
    {
        string password = p.GetProperty("password").GetString() ?? "";
        var bytes = app.Vault.Export(password);
        using var dialog = new SaveFileDialog { Title = "Save encrypted Orbit backup", Filter = "Orbit encrypted backup|*.orbit", FileName = $"Orbit-backup-{DateTime.Now:yyyy-MM-dd}.orbit", AddExtension = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return null;
        File.WriteAllBytes(dialog.FileName, bytes); app.Log("Encrypted backup saved."); return null;
    }
}
