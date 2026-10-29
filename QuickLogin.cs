using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Orbit;

// A fresh HTTP session owns only this login attempt. No browser profiles are read.
public sealed class QuickLogin : IDisposable
{
    public const string BrowserUrl = "https://www.roblox.com/crossdevicelogin/ConfirmCode";
    private const string Root = "https://apis.roblox.com/auth-token-service/v1/login/";
    private readonly HttpClient http;
    private readonly CookieContainer cookies = new();
    private string csrf = "", code = "", privateKey = "";
    public DateTimeOffset Expires { get; private set; }
    public QuickLogin(HttpMessageHandler? handler = null)
    {
        http = new HttpClient(handler ?? new HttpClientHandler { CookieContainer = cookies, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(25) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Orbit/0.1");
    }
    private async Task<HttpResponseMessage> Post(string url, object body, CancellationToken cancellation)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(Json.Write(body), Encoding.UTF8, "application/json") };
            request.Headers.Referrer = new Uri("https://www.roblox.com/");
            if (csrf.Length > 0) request.Headers.Add("x-csrf-token", csrf);
            var response = await http.SendAsync(request, cancellation);
            if (attempt == 0 && response.StatusCode == HttpStatusCode.Forbidden && response.Headers.TryGetValues("x-csrf-token", out var values))
            { csrf = values.First(); response.Dispose(); continue; }
            if (!response.IsSuccessStatusCode)
            {
                int status = (int)response.StatusCode; response.Dispose();
                throw new InvalidOperationException(status == 400 ? "This Quick Login code expired or was rejected. Start again, or use the Orbit browser window." : $"Roblox Quick Login returned HTTP {status}. Retry, or use the Orbit browser window.");
            }
            return response;
        }
        throw new InvalidOperationException("Roblox Quick Login could not start.");
    }
    public async Task<string> Create(CancellationToken cancellation = default)
    {
        using var response = await Post(Root + "create", new { }, cancellation);
        var data = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellation));
        code = data?["code"]?.GetValue<string>() ?? "";
        privateKey = data?["privateKey"]?.GetValue<string>() ?? "";
        if (code.Length is < 4 or > 12 || !code.All(char.IsAsciiLetterOrDigit) || privateKey.Length == 0) throw new InvalidOperationException("Roblox did not return a valid Quick Login code.");
        Expires = DateTimeOffset.TryParse(data?["expirationTime"]?.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var expiry) ? expiry : DateTimeOffset.UtcNow.AddMinutes(2);
        return code;
    }
    public async Task<string> Status(CancellationToken cancellation = default)
    {
        if (code.Length == 0) throw new InvalidOperationException("Start Quick Login first.");
        using var response = await Post(Root + "status", new { code, privateKey }, cancellation);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellation))?["status"]?.GetValue<string>() ?? "Unknown";
    }
    public async Task<string> Redeem(CancellationToken cancellation = default)
    {
        if (await Status(cancellation) != "Validated") throw new InvalidOperationException("Approve the code in Roblox before adding this account.");
        using var response = await Post("https://auth.roblox.com/v2/login", new { ctype = "AuthToken", cvalue = code, password = privateKey }, cancellation);
        // The handler retains cookies normally; explicit headers also support isolated test handlers.
        var cookie = cookies.GetCookies(new Uri("https://www.roblox.com/"))[".ROBLOSECURITY"]?.Value;
        if (cookie is null && response.Headers.TryGetValues("Set-Cookie", out var headers))
            foreach (var header in headers)
                if (header.StartsWith(".ROBLOSECURITY=", StringComparison.Ordinal)) cookie = header[15..].Split(';')[0];
        if (string.IsNullOrEmpty(cookie)) throw new InvalidOperationException("Roblox did not finish signing in. Complete verification using the Orbit browser window instead.");
        privateKey = "";
        return Input.Cookie(cookie);
    }
    public async Task Cancel()
    {
        if (code.Length == 0 || privateKey.Length == 0) return;
        try { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); using var response = await Post(Root + "cancel", new { code }, timeout.Token); } catch { }
        code = privateKey = "";
    }
    public void Dispose() { code = privateKey = csrf = ""; http.Dispose(); }
}

public sealed class QuickLoginWindow : Form
{
    private readonly AppService app;
    private readonly CancellationTokenSource cancellation = new();
    private readonly QuickLogin login = new();
    private readonly Label code = new() { Text = "Creating code…", AutoSize = true, Font = new Font("Segoe UI", 28, FontStyle.Bold) };
    private readonly Label status = new() { AutoSize = false, Width = 460, Height = 65, Text = "Connecting to Roblox…" };
    private string currentCode = "";
    public QuickLoginWindow(AppService app)
    {
        this.app = app; Text = "Default browser sign-in — Orbit"; ClientSize = new Size(510, 310); FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false;
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        StartPosition = FormStartPosition.CenterParent; BackColor = Color.FromArgb(24, 26, 29); ForeColor = Color.FromArgb(225, 228, 234); Font = new Font("Segoe UI", 10);
        var layout = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(22) };
        layout.Controls.Add(new Label { Text = "Authorize this Orbit sign-in with Quick Login", AutoSize = true });
        layout.Controls.Add(code); layout.Controls.Add(status);
        var buttons = new FlowLayoutPanel { Width = 465, Height = 48 };
        Button AddButton(string text, Action action) { var button = new Button { Text = text, AutoSize = true, BackColor = Color.FromArgb(48, 52, 60), ForeColor = ForeColor, FlatStyle = FlatStyle.Flat }; button.Click += (_, _) => action(); buttons.Controls.Add(button); return button; }
        AddButton("Copy code", () => { if (currentCode.Length > 0) Clipboard.SetText(currentCode); });
        AddButton("Open browser", OpenBrowser);
        AddButton("Close", Close);
        layout.Controls.Add(buttons); Controls.Add(layout);
        Shown += async (_, _) => await Run();
        FormClosing += (_, _) => cancellation.Cancel();
    }
    private void OpenBrowser()
    {
        try { Process.Start(new ProcessStartInfo(QuickLogin.BrowserUrl) { UseShellExecute = true }); }
        catch { status.Text = "Could not open your default browser. Open roblox.com, then Settings → Quick Login and enter this code."; }
    }
    private async Task Run()
    {
        bool redeemed = false;
        try
        {
            currentCode = await login.Create(cancellation.Token); code.Text = currentCode;
            status.Text = "In your browser, sign in if needed. Enter this code in Roblox Quick Login, review the account, then approve. Orbit will add it automatically.";
            OpenBrowser();
            while (DateTimeOffset.UtcNow < login.Expires)
            {
                await Task.Delay(3000, cancellation.Token);
                string state = await login.Status(cancellation.Token);
                if (state == "Cancelled") throw new InvalidOperationException("You cancelled this sign-in in Roblox. Close this window and start again.");
                if (state != "Validated") continue;
                var cookie = await login.Redeem(cancellation.Token); redeemed = true;
                await app.AcceptSession(cookie, "using default browser sign-in", cancellation.Token); Close(); return;
            }
            status.Text = "This code expired. Close this window and choose Default browser again for a new code.";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex) { if (!IsDisposed) status.Text = ex is HttpRequestException or TaskCanceledException ? "Could not connect to Roblox. Try again or use the Orbit browser window." : ex.Message; }
        finally { if (!redeemed) await login.Cancel(); login.Dispose(); }
    }
}
