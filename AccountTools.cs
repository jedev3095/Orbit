using System.Text.Json.Nodes;

namespace Orbit;

public sealed class AccountTools(RobloxApi api, Action<string, string> writeLog)
{
    private void log(string message) => writeLog(message, "info");
    public async Task<object> Run(Account a, string tool, string value)
    {
        JsonNode response;
        switch (tool)
        {
            case "balance": response = await api.Auth(a, $"https://economy.roblox.com/v1/users/{a.UserId}/currency", HttpMethod.Get); break;
            case "profile": response = await api.Public($"https://users.roblox.com/v1/users/{a.UserId}"); break;
            case "blocked": response = await api.Auth(a, "https://accountsettings.roblox.com/v1/users/get-detailed-blocked-users", HttpMethod.Get); break;
            case "displayName":
                response = await api.Auth(a, $"https://users.roblox.com/v1/users/{a.UserId}/display-names", HttpMethod.Patch, new { newDisplayName = value }); break;
            case "block":
            case "unblock":
                if (!long.TryParse(value, out var userId) || userId <= 0) throw new ArgumentException("Enter a numeric user ID.");
                response = await api.Auth(a, $"https://accountsettings.roblox.com/v1/users/{userId}/{tool}", HttpMethod.Post); break;
            case "friend":
                if (!long.TryParse(value, out var friend) || friend <= 0) throw new ArgumentException("Enter a numeric user ID.");
                response = await api.Auth(a, $"https://friends.roblox.com/v1/users/{friend}/request-friendship", HttpMethod.Post); break;
            case "quickLogin": response = await api.Auth(a, "https://apis.roblox.com/auth-token-service/v1/login/quick-login/authorize", HttpMethod.Post, new { code = value }); break;
            default: throw new ArgumentException("Unknown account tool.");
        }
        log($"@{a.Username}: {tool} completed."); return response;
    }
}
