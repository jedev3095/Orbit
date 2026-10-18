using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace Orbit;

public record ExtensionPackage(string Path, string Id, string Version, string Popup);

public static class BitwardenIntegration
{
    private static readonly string[] Ids = ["nngceckbapebfimnlniiiahkandclblb", "jbkfoedolllekgbhcbcoahefnbanhhlh"];
    public static string ExtensionId(string key)
    {
        var bytes = SHA256.HashData(Convert.FromBase64String(key));
        return string.Concat(bytes.Take(16).SelectMany(b => new[] { (char)('a' + (b >> 4)), (char)('a' + (b & 15)) }));
    }
    public static ExtensionPackage Inspect(string directory)
    {
        directory = System.IO.Path.GetFullPath(directory);
        using var manifest = JsonDocument.Parse(File.ReadAllText(System.IO.Path.Combine(directory, "manifest.json")));
        var root = manifest.RootElement;
        string id = ExtensionId(root.GetProperty("key").GetString() ?? "");
        if (!Ids.Contains(id)) throw new InvalidDataException("The selected folder is not the Chrome or Edge Bitwarden extension.");
        string popup = root.GetProperty("action").GetProperty("default_popup").GetString() ?? "";
        _ = PopupUrl(id, popup);
        var popupFile = System.IO.Path.GetFullPath(System.IO.Path.Combine(directory, popup.Replace('/', System.IO.Path.DirectorySeparatorChar)));
        if (!popupFile.StartsWith(directory + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(popupFile)) throw new InvalidDataException("Bitwarden's popup page is missing.");
        string version = root.GetProperty("version").GetString() ?? "";
        if (!System.Text.RegularExpressions.Regex.IsMatch(version, @"^\d+(?:\.\d+){0,3}$")) throw new InvalidDataException("Invalid extension version.");
        return new(directory, id, version, popup);
    }
    public static string PopupUrl(string id, string popup)
    {
        if (!Ids.Contains(id) || string.IsNullOrWhiteSpace(popup) || popup.StartsWith('/') || popup.Contains('\\') || popup.Contains(':') || popup.Split('/').Any(x => x is "." or "..")) throw new InvalidDataException("Invalid extension popup path.");
        return $"chrome-extension://{id}/{popup}";
    }
    public static bool ExtensionPage(string uri, string id) => Uri.TryCreate(uri, UriKind.Absolute, out var u) && u.Scheme == "chrome-extension" && u.Host == id && Ids.Contains(id);
    public static IReadOnlyList<ExtensionPackage> Discover(string? local = null, string? roaming = null)
    {
        local ??= Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        roaming ??= Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var roots = new[]
        {
            System.IO.Path.Combine(local, "imput", "Helium", "User Data"), System.IO.Path.Combine(local, "Google", "Chrome", "User Data"),
            System.IO.Path.Combine(local, "Microsoft", "Edge", "User Data"), System.IO.Path.Combine(local, "BraveSoftware", "Brave-Browser", "User Data"),
            System.IO.Path.Combine(local, "Chromium", "User Data"), System.IO.Path.Combine(local, "Helium", "User Data"),
            System.IO.Path.Combine(roaming, "Opera Software", "Opera GX Stable"), System.IO.Path.Combine(roaming, "Opera Software", "Opera Stable")
        };
        var found = new List<ExtensionPackage>();
        foreach (string root in roots.Where(Directory.Exists))
        {
            try
            {
                var profiles = new[] { root }.Concat(Directory.EnumerateDirectories(root).Where(p => System.IO.Path.GetFileName(p) == "Default" || System.IO.Path.GetFileName(p).StartsWith("Profile ", StringComparison.Ordinal)));
                foreach (var profile in profiles)
                foreach (var id in Ids)
                {
                    var extensions = System.IO.Path.Combine(profile, "Extensions", id);
                    if (!Directory.Exists(extensions)) continue;
                    foreach (var version in Directory.EnumerateDirectories(extensions)) try { found.Add(Inspect(version)); } catch { }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return found.OrderByDescending(p => Version.TryParse(p.Version, out var version) ? version : new Version()).ToList();
    }
    public static ExtensionPackage Cache(ExtensionPackage package, string vaultDirectory)
    {
        // Copy only the extension code package, never browser Local Extension Settings, cookies,
        // password databases, or Bitwarden vault storage.
        string root = System.IO.Path.Combine(vaultDirectory, "ExtensionPackages", "Bitwarden");
        Directory.CreateDirectory(root);
        string destination = System.IO.Path.Combine(root, package.Id + "-" + package.Version);
        if (Directory.Exists(destination)) return Inspect(destination);
        string staging = System.IO.Path.Combine(root, "staging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        foreach (string file in Directory.EnumerateFiles(package.Path, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
        {
            string relative = System.IO.Path.GetRelativePath(package.Path, file);
            if (relative.Split(System.IO.Path.DirectorySeparatorChar).Contains("_metadata")) continue;
            string target = System.IO.Path.GetFullPath(System.IO.Path.Combine(staging, relative));
            if (!target.StartsWith(staging + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Extension path escaped its package.");
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!); File.Copy(file, target, false);
        }
        _ = Inspect(staging); Directory.Move(staging, destination); return Inspect(destination);
    }
    public static async Task<(CoreWebView2BrowserExtension Extension, ExtensionPackage Package)?> Install(CoreWebView2 core, string vaultDirectory, string? selected = null)
    {
        var package = selected is null ? Discover().FirstOrDefault() : Inspect(selected);
        if (package is null)
        {
            var cached = System.IO.Path.Combine(vaultDirectory, "ExtensionPackages", "Bitwarden");
            if (Directory.Exists(cached)) foreach (var dir in Directory.EnumerateDirectories(cached)) try { package = Inspect(dir); break; } catch { }
        }
        if (package is null) return null;
        package = Cache(package, vaultDirectory);
        var installed = (await core.Profile.GetBrowserExtensionsAsync()).FirstOrDefault(e => e.Id == package.Id);
        installed ??= await core.Profile.AddBrowserExtensionAsync(package.Path);
        if (!installed.IsEnabled) await installed.EnableAsync(true);
        return (installed, package);
    }
}
