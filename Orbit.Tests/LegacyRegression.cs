using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Orbit;

public static class SelfTests
{
    public static int Run(string[] args)
    {
        var results = new List<object>();
        var index = Array.IndexOf(args, "--output");
        string output = index >= 0 ? args[index + 1] : Path.Combine(Environment.CurrentDirectory, "test-results.json");
        void Check(string name, bool value) { results.Add(new { name, passed = value }); if (!value) throw new Exception(name); }
        void Reject(string name, Action action) { bool rejected = false; try { action(); } catch { rejected = true; } Check(name, rejected); }
        string dir = Path.Combine(Path.GetTempPath(), "OrbitTests", Guid.NewGuid().ToString("N"));
        try
        {
            Check("Exact dashboard origin admitted", Input.TrustedOrigin("https://orbit.local/index.html"));
            foreach (string origin in new[] { "https://orbit.local.attacker.test/index.html", "http://orbit.local/index.html", "https://orbit.local:444/index.html", "https://orbit.local/other.html", "https://attacker.test/index.html" })
                Check("Host bridge rejects " + origin, !Input.TrustedOrigin(origin));
            const string cookie = "_synthetic_cookie_for_offline_test_12345678";
            string singleton = "Orbit.Tests." + Guid.NewGuid().ToString("N");
            using (var guard = MultiInstanceGuard.Start(singleton + ".mutex", singleton + ".event"))
            using (var mutex = Mutex.OpenExisting(singleton + ".mutex"))
            using (var eventMutex = Mutex.OpenExisting(singleton + ".event"))
            {
                Check("Multi-instance holds both singleton names", guard.IsActive && !mutex.WaitOne(0) && !eventMutex.WaitOne(0));
                Reject("Singleton Event cannot be created while the guard is active", () => { using var blocked = new EventWaitHandle(false, EventResetMode.AutoReset, singleton + ".event"); });
                Reject("A second manager cannot silently claim readiness", () => { using var other = MultiInstanceGuard.Start(singleton + ".mutex", singleton + ".event"); });
                Task.Run(guard.Dispose).GetAwaiter().GetResult();
                Check("Guard can be disabled from another thread", !guard.IsActive);
                bool primaryReleased = mutex.WaitOne(0), eventReleased = eventMutex.WaitOne(0);
                Check("Both singleton mutexes are released without abandonment", primaryReleased && eventReleased);
                mutex.ReleaseMutex(); eventMutex.ReleaseMutex();
                using var reacquired = MultiInstanceGuard.Start(singleton + ".mutex", singleton + ".event");
                Check("Multi-instance can be re-enabled after disposal", reacquired.IsActive);
            }
            using (var existingEvent = new EventWaitHandle(false, EventResetMode.AutoReset, singleton + ".collision"))
            {
                Reject("An existing Roblox-style Event blocks activation", () => { using var blocked = MultiInstanceGuard.Start(singleton + ".rollback", singleton + ".collision"); });
                using var rolledBack = new Mutex(false, singleton + ".rollback");
                Check("Failed activation releases partially acquired singleton", rolledBack.WaitOne(0)); rolledBack.ReleaseMutex();
            }
            Check("Cookie header normalized", Input.Cookie(".ROBLOSECURITY=" + cookie + "; Path=/") == cookie);
            Reject("Cookie header injection rejected", () => Input.Cookie(cookie + "\r\nHeader: value"));
            var vault = new Vault(dir);
            var account = new Account { UserId = 42, Username = "offline-test", SecurityToken = cookie, Fields = new() { ["note"] = "value" } };
            Check("First merge adds account", vault.Merge([account]) == 1);
            var stored = File.ReadAllBytes(vault.FilePath);
            Check("Vault stores encrypted content", !Encoding.UTF8.GetString(stored).Contains(cookie) && !Encoding.UTF8.GetString(stored).Contains("private-password"));
            var reopened = new Vault(dir); reopened.Load();
            Check("DPAPI vault round trip", reopened.Data.Accounts.Single().SecurityToken == cookie);
            Check("Duplicate import updates without duplication", reopened.Merge([new Account { UserId = 42, Username = "offline-test", SecurityToken = cookie + "refreshed" }]) == 0 && reopened.Data.Accounts.Count == 1);
            Check("Vault replacement keeps backup", File.Exists(reopened.FilePath + ".backup"));
            var before = reopened.Data.Accounts.Count;
            Reject("Invalid mixed import rejected", () => reopened.Merge([new Account { UserId = 99, Username = "valid", SecurityToken = cookie }, new Account { UserId = 0, Username = "invalid" }]));
            Check("Invalid import does not partly merge", before == reopened.Data.Accounts.Count);
            var backup = vault.Export("a sufficiently long password");
            Check("Backup round trip", Vault.DecodeBackup(backup, "a sufficiently long password").Accounts.Single().SecurityToken == cookie);
            Reject("Wrong backup password rejected", () => Vault.DecodeBackup(backup, "wrong password"));
            backup[^1] ^= 1;
            Reject("Tampered backup rejected", () => Vault.DecodeBackup(backup, "a sufficiently long password"));
            Reject("Short backup password rejected", () => vault.Export("short"));
            var legacy = Encoding.UTF8.GetBytes("[{\"UserID\":42,\"Username\":\"legacy\",\"SecurityToken\":\"" + cookie + "\",\"Group\":\"Imported\",\"Fields\":{\"test\":\"ok\"}}]");
            Check("Original JSON import", LegacyImport.Read(legacy, "").Single().Group == "Imported");
            var legacyEntropy = Encoding.UTF8.GetBytes("ROBLOX ACCOUNT MANAGER | :) | BROUGHT TO YOU BUY ic3w0lf");
            Check("Legacy entropy length matches binary", legacyEntropy.Length == 56);
            var legacyDpapi = ProtectedData.Protect(legacy, legacyEntropy, DataProtectionScope.LocalMachine);
            Check("Original machine DPAPI import", LegacyImport.Read(legacyDpapi, "").Single().Username == "legacy");
            Reject("Truncated legacy vault rejected", () => LegacyImport.Read("Roblox Account Manager created by"u8.ToArray(), "password"));
            // Independent fixture: exact recovered 64-byte header, original SHA512/Argon2i Moderate parameters,
            // and libsodium crypto_secretbox_easy encryption, generated outside the importer.
            var legacyLocked = Convert.FromBase64String("Um9ibG94IEFjY291bnQgTWFuYWdlciBjcmVhdGVkIGJ5IGljM3cwbGYyMiBAIGdpdGh1Yi5jb20gLi4uLi4uLgABAgMEBQYHCAkKCwwNDg8AAQIDBAUGBwgJCgsMDQ4PEBESExQVFhd+zShL1vhx26fEVa4693DOe8Y5nqGpCZCM/gpMZgbjoepJXRPkDWALGny+xQE5rkSYlKnmcNkROzyM1WMuIJZOdxwNGA/XjvPPbkNN3Mxfi+dZ2rL3/xeIGIW/CDfSZbO/EoU9xaSRmPk9svbAnqjtDnEbaW31n/PaUYKKjDx4");
            Check("Original password-encrypted format import", LegacyImport.Read(legacyLocked, "fixture-password").Single().Username == "legacy-password-test");
            Reject("Wrong original password rejected", () => LegacyImport.Read(legacyLocked, "incorrect-password"));
            int unauthorizedRequests = 0;
            using (var failingApp = new AppService(vault, api: new RobloxApi(new FakeHandler(_ =>
            {
                unauthorizedRequests++;
                return new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("{\"errors\":[{\"code\":9002}]}") };
            }))))
            {
                var result = failingApp.Launch([account.Id], 4924922222, "").GetAwaiter().GetResult();
                Check("Expired session returns failed launch result", result.Failed == 1 && result.Launched == 0);
                Check("Expired session shows Needs login", account.Status == "Needs login" && account.LastError.Contains("HTTP 401"));
                Check("Expired session is rejected before ticket or client launch", unauthorizedRequests == 1 && account.ProcessId is null);
                Check("Launch error never contains saved credentials", !Json.Write(failingApp.State()).Contains(cookie));
                failingApp.FlushActivityAsync().GetAwaiter().GetResult();
                Check("Activity persists actionable error", File.ReadAllText(Path.Combine(dir, "activity.json")).Contains("HTTP 401"));
            }
            using (var restoredApp = new AppService(vault)) Check("Activity restored on restart", restoredApp.Activity.Any(a => a.Message.Contains("HTTP 401")));
            int blockedRequests = 0;
            vault.Data.Settings.MultiInstance = true;
            using (var existingEvent = new EventWaitHandle(false, EventResetMode.AutoReset, singleton + ".launch-event"))
            using (var blockedApp = new AppService(vault, api: new RobloxApi(new FakeHandler(_ => { blockedRequests++; throw new Exception("No network request expected"); })), multiFactory: () => MultiInstanceGuard.Start(singleton + ".launch-mutex", singleton + ".launch-event")))
            {
                var result = blockedApp.Launch([account.Id], 4924922222, "").GetAwaiter().GetResult();
                Check("Blocked multi-instance fails before authentication or launching", result.Failed == 1 && blockedRequests == 0);
                Check("Blocked multi-instance gives actionable restart guidance", account.LastError.Contains("Close all Roblox clients") && Json.Write(blockedApp.State()).Contains("multiInstanceError"));
            }
            vault.Data.Settings.MultiInstance = false;
            vault.Merge([new Account { UserId = account.UserId, Username = account.Username, SecurityToken = cookie }]);
            Check("Fresh import clears previous sign-in error", account.LastError == "" && account.Status == "Unchecked");
            Check("Diagnostic redaction removes cookie and ticket", Diagnostics.Redact("secret-cookie temporary-ticket", "secret-cookie", "temporary-ticket") == "[redacted] [redacted]");
            int ticketRequests = 0; bool csrfRetried = false, initialJson = false, retryJson = false;
            using (var api = new RobloxApi(new FakeHandler(request =>
            {
                if (request.Method == HttpMethod.Get) return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"id\":42,\"name\":\"offline-test\"}") };
                ticketRequests++;
                bool json = request.Content?.Headers.ContentType?.MediaType == "application/json" && request.Content.ReadAsStringAsync().GetAwaiter().GetResult().Trim() == "{}";
                if (ticketRequests == 1) initialJson = json; else retryJson = json;
                if (!json) return new HttpResponseMessage(HttpStatusCode.UnsupportedMediaType) { Content = new StringContent("{\"errors\":[{\"message\":\"UnsupportedMediaType\"}]}") };
                if (ticketRequests == 1) { var response = new HttpResponseMessage(HttpStatusCode.Forbidden); response.Headers.Add("x-csrf-token", "synthetic-csrf"); return response; }
                csrfRetried = request.Headers.GetValues("x-csrf-token").Single() == "synthetic-csrf";
                var success = new HttpResponseMessage(HttpStatusCode.OK); success.Headers.Add("rbx-authentication-ticket", "synthetic-ticket"); return success;
            }))) Check("Valid session receives ticket after CSRF retry", api.Ticket(account).GetAwaiter().GetResult() == "synthetic-ticket" && csrfRetried && ticketRequests == 2);
            Check("Initial launch-ticket request sends JSON content type and empty object", initialJson);
            Check("CSRF retry retains launch-ticket JSON body and content type", retryJson);
            int quickRequests = 0; bool quickJson = true, quickCsrf = false, quickCancel = false, quickRedeem = false;
            using (var quick = new QuickLogin(new FakeHandler(request =>
            {
                quickRequests++;
                quickJson &= request.Content?.Headers.ContentType?.MediaType == "application/json";
                if (quickRequests == 1) { var challenge = new HttpResponseMessage(HttpStatusCode.Forbidden); challenge.Headers.Add("x-csrf-token", "quick-csrf"); return challenge; }
                quickCsrf |= request.Headers.TryGetValues("x-csrf-token", out var tokens) && tokens.Single() == "quick-csrf";
                string path = request.RequestUri!.AbsolutePath;
                var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult()).RootElement;
                var response = new HttpResponseMessage(HttpStatusCode.OK);
                if (path.EndsWith("/create")) response.Content = new StringContent("{\"code\":\"ABC123\",\"privateKey\":\"synthetic-private-key\",\"expirationTime\":\"2099-01-01T00:00:00\"}");
                else if (path.EndsWith("/status"))
                {
                    Check("Quick Login polling uses only the attempt's code and key", body.GetProperty("code").GetString() == "ABC123" && body.GetProperty("privateKey").GetString() == "synthetic-private-key");
                    response.Content = new StringContent("{\"status\":\"Validated\"}");
                }
                else if (path == "/v2/login")
                {
                    quickRedeem = body.GetProperty("ctype").GetString() == "AuthToken" && body.GetProperty("cvalue").GetString() == "ABC123" && body.GetProperty("password").GetString() == "synthetic-private-key";
                    response.Headers.Add("Set-Cookie", ".ROBLOSECURITY=" + cookie + "; Domain=.roblox.com; Path=/; Secure; HttpOnly");
                }
                return response;
            })))
            {
                Reject("Quick Login requires a created attempt before polling", () => quick.Status().GetAwaiter().GetResult());
                Check("Quick Login creates a code after CSRF retry", quick.Create().GetAwaiter().GetResult() == "ABC123" && quickCsrf);
                Check("Quick Login uses server expiration", quick.Expires.Year == 2099 && quick.Expires.Offset == TimeSpan.Zero && quick.Expires.Hour == 0);
                Check("Approved Quick Login extracts session cookie", quick.Redeem().GetAwaiter().GetResult() == cookie && quickRedeem);
                Check("Every Quick Login POST sends JSON", quickJson);
            }
            using (var pending = new QuickLogin(new FakeHandler(request =>
            {
                string path = request.RequestUri!.AbsolutePath;
                if (path.EndsWith("/cancel")) quickCancel = true;
                if (path == "/v2/login") throw new Exception("Unapproved login must not redeem");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(path.EndsWith("/create") ? "{\"code\":\"ABC123\",\"privateKey\":\"synthetic-private-key\"}" : "{\"status\":\"Created\"}") };
            })))
            {
                pending.Create().GetAwaiter().GetResult();
                Reject("Unapproved Quick Login cannot redeem", () => pending.Redeem().GetAwaiter().GetResult());
                pending.Cancel().GetAwaiter().GetResult();
                Check("Abandoned Quick Login is cancelled", quickCancel);
            }
            using var app = new AppService(vault, true);
            string state = Json.Write(app.State());
            Check("Dashboard state excludes credentials", !state.Contains(cookie) && !state.Contains("private-password") && !state.Contains("apiToken") && !state.Contains("securityToken"));
            var token = Encoding.UTF8.GetBytes("Bearer " + new string('a', 64));
            Check("API exact token accepted", ControlServer.Authorized(token, Encoding.UTF8.GetString(token)));
            Check("API mismatching token rejected", !ControlServer.Authorized(token, "Bearer " + new string('b', 64)));
            Check("API missing token rejected", !ControlServer.Authorized(token, null));
            Check("API empty configured token rejected", !ControlServer.Authorized("Bearer "u8.ToArray(), "Bearer "));
            using (var tcp = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0))
            {
                tcp.Start(); int port = ((IPEndPoint)tcp.LocalEndpoint).Port; tcp.Stop();
                using var server = new ControlServer(app, port, new string('a', 64)); server.Start();
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                var address = $"http://127.0.0.1:{port}/v1/state";
                Check("HTTP listener rejects anonymous", http.GetAsync(address).GetAwaiter().GetResult().StatusCode == HttpStatusCode.Unauthorized);
                http.DefaultRequestHeaders.Add("Authorization", "Bearer " + new string('a', 64));
                var response = http.GetStringAsync(address).GetAwaiter().GetResult();
                Check("HTTP state authenticated and sanitized", response.Contains("accounts") && !response.Contains(cookie));
                http.DefaultRequestHeaders.Add("Origin", "https://attacker.test");
                Check("HTTP rejects browser origins", http.GetAsync(address).GetAwaiter().GetResult().StatusCode == HttpStatusCode.Forbidden);
                using var ws = new System.Net.WebSockets.ClientWebSocket();
                ws.Options.SetRequestHeader("Authorization", "Bearer " + new string('a', 64));
                ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/Nexus?accountId={vault.Data.Accounts[0].Id}"), CancellationToken.None).GetAwaiter().GetResult();
                ws.SendAsync(Encoding.UTF8.GetBytes("{\"Name\":\"ping\"}"), System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None).GetAwaiter().GetResult();
                var reply = new byte[64];
                var wsResult = ws.ReceiveAsync(new ArraySegment<byte>(reply), new CancellationTokenSource(5000).Token).GetAwaiter().GetResult();
                Check("Authenticated Nexus ping round trip", Encoding.UTF8.GetString(reply, 0, wsResult.Count) == "pong");
                ws.Abort();
            }
            File.WriteAllText(output, Json.Write(new { passed = true, tests = results })); return 0;
        }
        catch (Exception ex) { File.WriteAllText(output, Json.Write(new { passed = false, error = ex.ToString(), tests = results })); return 1; }
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response(request));
    }
}
