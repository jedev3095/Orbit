using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Orbit;

public static class Json
{
    public static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    public static string Write(object? value) => JsonSerializer.Serialize(value, Options);
    public static T Read<T>(string value) => JsonSerializer.Deserialize<T>(value, Options) ?? throw new InvalidDataException("Empty data.");
}

public sealed class Account
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public long UserId { get; set; }
    public string Username { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string SecurityToken { get; set; } = "";
    public string Alias { get; set; } = "";
    public string Description { get; set; } = "";
    public string Group { get; set; } = "Default";
    public Dictionary<string, string> Fields { get; set; } = [];
    public DateTime? LastUse { get; set; }
    public long PlaceId { get; set; }
    public string JobId { get; set; } = "";
    public string LaunchMode { get; set; } = "game";
    public long FollowUserId { get; set; }
    public bool AutoRelaunch { get; set; }
    public int RelaunchSeconds { get; set; } = 60;
    [JsonIgnore] public string Status { get; set; } = "Unchecked";
    [JsonIgnore] public string LastError { get; set; } = "";
    [JsonIgnore] public long? Robux { get; set; }
    [JsonIgnore] public string Thumbnail { get; set; } = "";
    [JsonIgnore] public string Tracker { get; set; } = "";
    [JsonIgnore] public int? ProcessId { get; set; }
    [JsonIgnore] public DateTime? ProcessStartedUtc { get; set; }
    [JsonIgnore] public string ProcessName { get; set; } = "";
    [JsonIgnore] public int RelaunchAttempts { get; set; }
}

public sealed class Settings
{
    public bool MultiInstance { get; set; }
    public int LaunchDelay { get; set; } = 8;
    public bool HideUsernames { get; set; }
    public bool RememberWindows { get; set; } = true;
    public bool ClosePrevious { get; set; }
    public bool ApiEnabled { get; set; }
    public int ApiPort { get; set; } = 7963;
    public string ApiToken { get; set; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    public string Accent { get; set; } = "blue";
}

public sealed class Game
{
    public long PlaceId { get; set; }
    public long UniverseId { get; set; }
    public string Name { get; set; } = "";
    public string Creator { get; set; } = "";
    public bool Favorite { get; set; }
    public string Image { get; set; } = "";
    public long Players { get; set; }
    public DateTime LastPlayed { get; set; } = DateTime.UtcNow;
}

public sealed class Database
{
    public List<Account> Accounts { get; set; } = [];
    public List<Game> Games { get; set; } = [];
    public Settings Settings { get; set; } = new();
}

public record Activity(DateTime Time, string Kind, string Message);
public record LaunchResult(int Requested, int Launched, int Failed, bool Cancelled);

public static class Diagnostics
{
    public static string Redact(string message, params string[] secrets)
    {
        foreach (var secret in secrets.Where(s => !string.IsNullOrEmpty(s))) message = message.Replace(secret, "[redacted]", StringComparison.Ordinal);
        return message.Length > 3000 ? message[..3000] : message;
    }
}

public static class Input
{
    public static string Cookie(string input)
    {
        var result = input.Trim();
        const string prefix = ".ROBLOSECURITY=";
        if (result.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) result = result[prefix.Length..].Split(';')[0];
        if (result.Length < 20 || result.Length > 16384 || result.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c == ';'))
            throw new ArgumentException("Enter a valid Roblox session cookie.");
        return result;
    }
    public static bool TrustedOrigin(string source) => Uri.TryCreate(source, UriKind.Absolute, out var u) && u.Scheme == "https" && u.Host == "orbit.local" && u.IsDefaultPort && u.AbsolutePath == "/index.html";
}
