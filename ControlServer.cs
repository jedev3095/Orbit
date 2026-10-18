using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Orbit;

public sealed class ControlServer : IDisposable
{
    private readonly AppService app;
    private readonly HttpListener listener = new();
    private readonly byte[] token;
    private readonly CancellationTokenSource lifetime = new();
    private readonly ConcurrentDictionary<Guid, WebSocket> clients = new();
    public int Port { get; }
    public ControlServer(AppService app, int port, string token) { this.app = app; Port = port; this.token = Encoding.UTF8.GetBytes("Bearer " + token); listener.Prefixes.Add($"http://127.0.0.1:{port}/"); }
    public void Start() { listener.Start(); _ = Loop(); }
    public bool Connected(Guid id) => clients.TryGetValue(id, out var socket) && socket.State == WebSocketState.Open;
    public static bool Authorized(byte[] expected, string? supplied)
    {
        var bytes = Encoding.UTF8.GetBytes(supplied ?? "");
        return expected.Length >= 39 && CryptographicOperations.FixedTimeEquals(expected, bytes);
    }
    private async Task Loop()
    {
        while (!lifetime.IsCancellationRequested)
        {
            try { var c = await listener.GetContextAsync(); _ = Handle(c); }
            catch when (lifetime.IsCancellationRequested) { break; }
            catch { break; }
        }
    }
    private async Task Reply(HttpListenerContext c, int status, object data)
    {
        var bytes = Encoding.UTF8.GetBytes(Json.Write(data));
        c.Response.StatusCode = status; c.Response.ContentType = "application/json; charset=utf-8";
        c.Response.Headers["Cache-Control"] = "no-store";
        c.Response.ContentLength64 = bytes.Length;
        await c.Response.OutputStream.WriteAsync(bytes); c.Response.Close();
    }
    private async Task Handle(HttpListenerContext c)
    {
        try
        {
            if (!c.Request.IsLocal || !Authorized(token, c.Request.Headers["Authorization"])) { await Reply(c, 401, new { error = "Unauthorized" }); return; }
            if (c.Request.Headers["Origin"] is not null) { await Reply(c, 403, new { error = "Browser-origin requests are not accepted." }); return; }
            string path = c.Request.Url!.AbsolutePath;
            if (path == "/Nexus" && c.Request.IsWebSocketRequest) { await Socket(c); return; }
            if (path == "/v1/state" && c.Request.HttpMethod == "GET")
            { await Reply(c, 200, await app.Dispatch(() => Task.FromResult<object?>(app.State())) ?? new { }); return; }
            if (path is not ("/v1/launch" or "/v1/refresh" or "/v1/stop") || c.Request.HttpMethod != "POST") { await Reply(c, 404, new { error = "Unknown route" }); return; }
            if (c.Request.ContentLength64 < 0 || c.Request.ContentLength64 > 65536) { await Reply(c, 413, new { error = "A bounded JSON body is required." }); return; }
            using var reader = new StreamReader(c.Request.InputStream, Encoding.UTF8);
            var text = await reader.ReadToEndAsync(lifetime.Token);
            if (text.Length > 65536) { await Reply(c, 413, new { error = "Request too large" }); return; }
            using var doc = JsonDocument.Parse(text);
            var p = doc.RootElement.Clone(); string action = path.Split('/')[^1];
            var result = await app.Dispatch(() => app.Command(action, p));
            await Reply(c, 200, new { ok = true, result });
        }
        catch (Exception)
        {
            try { await Reply(c, 400, new { error = "Request failed. Check account IDs, parameters, and the activity log." }); } catch { }
        }
    }
    private async Task Socket(HttpListenerContext c)
    {
        if (!Guid.TryParse(c.Request.QueryString["accountId"], out var id)) { await Reply(c, 400, new { error = "accountId required" }); return; }
        await app.Dispatch(() => { app.Find(id); return Task.FromResult<object?>(null); });
        var accepted = await c.AcceptWebSocketAsync(null); var socket = accepted.WebSocket;
        if (clients.TryRemove(id, out var old)) old.Dispose(); clients[id] = socket;
        await app.Dispatch(() => { app.Log("Nexus client connected."); return Task.FromResult<object?>(null); });
        try
        {
            var buffer = new byte[16384];
            while (socket.State == WebSocketState.Open && !lifetime.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), lifetime.Token);
                if (result.MessageType == WebSocketMessageType.Close) break;
                if (!result.EndOfMessage || result.MessageType != WebSocketMessageType.Text) { await socket.CloseAsync(WebSocketCloseStatus.MessageTooBig, "Text messages limited to 16 KB", CancellationToken.None); break; }
                using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(buffer, 0, result.Count));
                var message = doc.RootElement;
                string name = message.TryGetProperty("Name", out var n) ? n.GetString() ?? "" : "";
                if (name == "ping") await socket.SendAsync("pong"u8.ToArray(), WebSocketMessageType.Text, true, lifetime.Token);
                if (name == "Log" && message.TryGetProperty("Payload", out var payload) && payload.TryGetProperty("Content", out var content))
                {
                    string safe = content.GetString() ?? ""; if (safe.Length > 2000) safe = safe[..2000];
                    await app.Dispatch(() => { app.Log("Nexus: " + safe); return Task.FromResult<object?>(null); });
                }
            }
        }
        finally { if (clients.TryGetValue(id, out var current) && ReferenceEquals(current, socket)) clients.TryRemove(id, out _); socket.Dispose(); await app.Dispatch(() => { app.Notify(); return Task.FromResult<object?>(null); }); }
    }
    public async Task Send(List<Guid> ids, string message)
    {
        if (message.Length == 0 || message.Length > 16000) throw new ArgumentException("Enter a command of up to 16,000 characters.");
        int sent = 0;
        foreach (var id in ids) if (clients.TryGetValue(id, out var socket) && socket.State == WebSocketState.Open) { await socket.SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, true, lifetime.Token); sent++; }
        if (sent == 0) throw new InvalidOperationException("None of the selected accounts has a connected Nexus client.");
    }
    public void Dispose() { lifetime.Cancel(); listener.Close(); foreach (var socket in clients.Values) socket.Dispose(); clients.Clear(); }
}
