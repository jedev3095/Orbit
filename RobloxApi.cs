using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Orbit;

public sealed class RobloxApiException(HttpStatusCode statusCode, string message) : InvalidOperationException(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;
    public bool RequiresLogin => StatusCode == HttpStatusCode.Unauthorized;
}

public sealed class RobloxApi : IDisposable
{
    private readonly HttpClient http;
    public RobloxApi(HttpMessageHandler? handler = null)
    {
        http = new HttpClient(handler ?? new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(25) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Orbit/0.1");
    }
    public async Task<JsonNode> Public(string url)
    {
        using var response = await http.GetAsync(url);
        await Check(response);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync()) ?? new JsonObject();
    }
    private static async Task Check(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        if (response.StatusCode is HttpStatusCode.Unauthorized) throw new RobloxApiException(response.StatusCode, "Roblox rejected the saved session (HTTP 401). Sign in again using Add account → Sign in with Roblox, then choose Use this account. Your existing account entry will be refreshed.");
        if ((int)response.StatusCode == 429) throw new RobloxApiException(response.StatusCode, "Roblox is rate limiting requests. Wait a moment and retry.");
        if (response.StatusCode == HttpStatusCode.Forbidden && response.Headers.Contains("rblx-challenge-id"))
            throw new RobloxApiException(response.StatusCode, "Roblox requires account verification (HTTP 403). Sign in and complete verification in the Roblox browser, then refresh this account.");
        var text = await response.Content.ReadAsStringAsync();
        string message = "";
        try { message = JsonNode.Parse(text)?["errors"]?[0]?["message"]?.GetValue<string>() ?? ""; } catch { }
        throw new RobloxApiException(response.StatusCode, $"Roblox returned {(int)response.StatusCode}. {message}".Trim());
    }
    private static HttpRequestMessage Request(string url, HttpMethod method, string cookie, object? body = null, string? csrf = null)
    {
        var uri = new Uri(url);
        if (uri.Scheme != "https" || !(uri.Host == "roblox.com" || uri.Host.EndsWith(".roblox.com", StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("Only Roblox HTTPS endpoints are allowed.");
        var req = new HttpRequestMessage(method, uri);
        req.Headers.Add("Cookie", ".ROBLOSECURITY=" + Input.Cookie(cookie));
        req.Headers.Referrer = new Uri("https://www.roblox.com/");
        if (csrf is not null) req.Headers.Add("X-CSRF-TOKEN", csrf);
        if (body is not null) req.Content = new StringContent(Json.Write(body), Encoding.UTF8, "application/json");
        return req;
    }
    public async Task<HttpResponseMessage> Send(string cookie, string url, HttpMethod method, object? body = null)
    {
        using var req = Request(url, method, cookie, body);
        var response = await http.SendAsync(req);
        if (response.StatusCode == HttpStatusCode.Forbidden && response.Headers.TryGetValues("x-csrf-token", out var values))
        {
            string csrf = values.First(); response.Dispose();
            using var retry = Request(url, method, cookie, body, csrf);
            response = await http.SendAsync(retry);
        }
        try { await Check(response); return response; } catch { response.Dispose(); throw; }
    }
    public async Task<JsonNode> Auth(Account a, string url, HttpMethod method, object? body = null)
    {
        using var response = await Send(a.SecurityToken, url, method, body);
        var text = await response.Content.ReadAsStringAsync();
        return string.IsNullOrWhiteSpace(text) ? new JsonObject() : JsonNode.Parse(text) ?? new JsonObject();
    }
    public async Task<Account> Identify(string cookie)
    {
        using var response = await Send(cookie, "https://users.roblox.com/v1/users/authenticated", HttpMethod.Get);
        var user = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        return new Account { UserId = user["id"]!.GetValue<long>(), Username = user["name"]!.GetValue<string>(), DisplayName = user["displayName"]?.GetValue<string>() ?? "", SecurityToken = Input.Cookie(cookie), Status = "Offline" };
    }
    public async Task Refresh(Account a)
    {
        var user = await Identify(a.SecurityToken);
        a.Username = user.Username; a.DisplayName = user.DisplayName; a.Status = "Offline"; a.LastError = "";
        try { a.Robux = (await Auth(a, $"https://economy.roblox.com/v1/users/{a.UserId}/currency", HttpMethod.Get))["robux"]?.GetValue<long>(); } catch { }
        try
        {
            a.Thumbnail = (await Public($"https://thumbnails.roblox.com/v1/users/avatar-headshot?userIds={a.UserId}&size=150x150&format=Png&isCircular=false"))["data"]?[0]?["imageUrl"]?.GetValue<string>() ?? "";
        } catch { }
        try
        {
            var presence = (await Auth(a, "https://presence.roblox.com/v1/presence/users", HttpMethod.Post, new { userIds = new[] { a.UserId } }))["userPresences"]?[0];
            a.Status = presence?["userPresenceType"]?.GetValue<int>() switch { 1 => "Online", 2 => "In game", 3 => "In Studio", _ => "Offline" };
        } catch { }
    }
    public async Task<string> Ticket(Account a)
    {
        // Identify first: an imported file can decrypt correctly while its saved session has expired.
        var identified = await Identify(a.SecurityToken);
        if (identified.UserId != a.UserId) throw new InvalidOperationException("This saved session belongs to a different Roblox account. Sign in again to refresh the correct account.");
        using var response = await Send(a.SecurityToken, "https://auth.roblox.com/v1/authentication-ticket/", HttpMethod.Post, new { });
        if (!response.Headers.TryGetValues("rbx-authentication-ticket", out var values)) throw new InvalidOperationException("Roblox did not issue a launch ticket. Sign in again.");
        return values.First();
    }
    public async Task<Game> Game(long place)
    {
        if (place <= 0) throw new ArgumentException("Enter a valid place ID.");
        long universe = (await Public($"https://apis.roblox.com/universes/v1/places/{place}/universe"))["universeId"]!.GetValue<long>();
        var detail = (await Public($"https://games.roblox.com/v1/games?universeIds={universe}"))["data"]?[0];
        var game = new Game { PlaceId = place, UniverseId = universe, Name = detail?["name"]?.GetValue<string>() ?? "Experience " + place, Creator = detail?["creator"]?["name"]?.GetValue<string>() ?? "" };
        try { game.Image = (await Public($"https://thumbnails.roblox.com/v1/games/icons?universeIds={universe}&returnPolicy=PlaceHolder&size=256x256&format=Png&isCircular=false"))["data"]?[0]?["imageUrl"]?.GetValue<string>() ?? ""; } catch { }
        return game;
    }
    public Task<JsonNode> Servers(long place, string cursor) => Public($"https://games.roblox.com/v1/games/{place}/servers/Public?sortOrder=Asc&excludeFullGames=true&limit=25&cursor={Uri.EscapeDataString(cursor)}");
    public void Dispose() => http.Dispose();
}
