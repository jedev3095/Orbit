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
    private readonly bool smoke;
    private readonly string[] args;
    private bool initialized, closing;
    public MainWindow(AppService app, bool smoke, string[] args)
    {
        this.app = app; this.smoke = smoke; this.args = args;
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        Text = "orbit"; Size = new Size(1080, 680); MinimumSize = new Size(860, 500);
        if (args.Contains("--narrow")) Size = new Size(880, 560);
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
            core.Settings.AreDevToolsEnabled = smoke; core.Settings.IsStatusBarEnabled = false; core.Settings.AreDefaultContextMenusEnabled = false;
            core.SetVirtualHostNameToFolderMapping("orbit.local", Path.Combine(AppContext.BaseDirectory, "ui"), CoreWebView2HostResourceAccessKind.DenyCors);
            core.NavigationStarting += (_, e) => { if (!Input.TrustedOrigin(e.Uri)) e.Cancel = true; };
            core.FrameNavigationStarting += (_, e) => e.Cancel = true;
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            core.WebMessageReceived += Message;
            core.NavigationCompleted += async (_, e) =>
            {
                if (!e.IsSuccess) return;
                initialized = true; Push();
                if (smoke) await Smoke();
            };
            if (!smoke) { try { app.ApplySettings(); } catch (Exception ex) { app.Log(ex.Message, "error"); } }
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
                case "loginExternal":
                    if (!smoke)
                    {
                        var external = Application.OpenForms.OfType<QuickLoginWindow>().FirstOrDefault();
                        if (external is null) new QuickLoginWindow(app).Show(this);
                        else { external.Show(); external.Activate(); }
                    }
                    result = null; break;
                case "login":
                    if (!smoke)
                    {
                        var login = Application.OpenForms.OfType<AccountBrowser>().FirstOrDefault(b => b.IsSignIn);
                        if (login is null) new AccountBrowser(app).Show(this);
                        else { login.Show(); login.Activate(); }
                    }
                    result = null; break;
                case "browser":
                    var browserAccount = app.Find(p.GetProperty("id").GetGuid());
                    if (!smoke)
                    {
                        var browser = Application.OpenForms.OfType<AccountBrowser>().FirstOrDefault(b => b.AccountId == browserAccount.Id);
                        if (browser is null) new AccountBrowser(app, browserAccount).Show(this);
                        else { if (browser.WindowState == FormWindowState.Minimized) browser.WindowState = FormWindowState.Normal; browser.Show(); browser.Activate(); }
                    }
                    result = new { accountId = browserAccount.Id }; break;
                case "import": result = smoke ? null : Import(p); break;
                case "export": result = smoke ? null : Export(p); break;
                case "copyApiToken": if (!smoke) Clipboard.SetText(app.Vault.Data.Settings.ApiToken); result = null; break;
                case "openProfile":
                    if (!smoke) Process.Start(new ProcessStartInfo($"https://www.roblox.com/users/{app.Find(p.GetProperty("id").GetGuid()).UserId}/profile") { UseShellExecute = true });
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
    private async Task Smoke()
    {
        try
        {
            var index = Array.IndexOf(args, "--output"); var output = index >= 0 ? args[index + 1] : Path.Combine(app.Vault.DirectoryPath, "smoke");
            Directory.CreateDirectory(output);
            await Task.Delay(800);
            string smokeFunction = args.Contains("--smoke-empty") ? "orbitSmokeEmpty" : "orbitSmoke";
            await view.CoreWebView2.ExecuteScriptAsync($"window.{smokeFunction}().then(result => window.smokeResult = result).catch(error => window.smokeResult = {{passed:false, error:error.stack || error.message}})");
            string results = "null";
            for (int i = 0; i < 100; i++)
            {
                await Task.Delay(100);
                results = await view.CoreWebView2.ExecuteScriptAsync("window.smokeResult ?? null");
                if (results != "null") break;
            }
            await Task.Delay(300);
            using (var file = File.Create(Path.Combine(output, "orbit-dashboard.png"))) await view.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, file);
            foreach (string page in new[] { "settings", "games", "control" })
            {
                await view.CoreWebView2.ExecuteScriptAsync($"document.querySelector('[data-page=\"{page}\"]').click()");
                await Task.Delay(100);
                using var file = File.Create(Path.Combine(output, $"orbit-{page}.png"));
                await view.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, file);
            }
            await view.CoreWebView2.ExecuteScriptAsync("document.querySelector('nav [data-page=\"accounts\"]').click(); document.querySelector('.identity-name[data-action=\"edit\"]')?.click()");
            await Task.Delay(100);
            using (var file = File.Create(Path.Combine(output, "orbit-account-dialog.png"))) await view.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, file);
            File.WriteAllText(Path.Combine(output, "ui-smoke.json"), results);
            await view.CoreWebView2.ExecuteScriptAsync("document.querySelector('#modal').close(); document.querySelector('[data-action=\"add\"]').click()");
            await Task.Delay(100);
            using (var choices = File.Create(Path.Combine(output, "orbit-sign-in-choices.png"))) await view.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, choices);
            using (var report = JsonDocument.Parse(results))
                if (report.RootElement.ValueKind != JsonValueKind.Object || !report.RootElement.TryGetProperty("passed", out var passed) || !passed.GetBoolean()) Environment.ExitCode = 1;
            Close();
        }
        catch (Exception ex) { File.WriteAllText(Path.Combine(app.Vault.DirectoryPath, "smoke-error.txt"), ex.ToString()); Environment.ExitCode = 1; Close(); }
    }
}
