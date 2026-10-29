using System.Security.Cryptography;
using System.Text;

namespace Orbit;

public sealed record SavedLogin
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "Roblox";
    public string Website { get; init; } = "https://www.roblox.com";
    public string Username { get; init; } = "";
    public string Password { get; init; } = "";
    public long? LinkedUserId { get; init; }
}

public sealed class CredentialVault
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Orbit.LocalPasswords.v1");
    private static readonly byte[] Magic = "ORBITPW1"u8.ToArray();
    private List<SavedLogin> entries = [];
    public string FilePath { get; }
    public bool Available { get; private set; } = true;
    public int Count => entries.Count;
    public IEnumerable<string> Secrets => entries.Select(e => e.Password);
    public event Action? Changed;
    public CredentialVault(string directory)
    {
        Directory.CreateDirectory(directory);
        FilePath = Path.Combine(directory, "logins.dat");
        if (!File.Exists(FilePath)) return;
        try { if (new FileInfo(FilePath).Length > 32 * 1024 * 1024) throw new InvalidDataException(); entries = DecodeLocal(File.ReadAllBytes(FilePath)); }
        catch { Available = false; }
    }
    private static List<SavedLogin> DecodeLocal(byte[] encrypted)
    {
        var bytes = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
        try { return Validate(Json.Read<List<SavedLogin>>(Encoding.UTF8.GetString(bytes))); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    public static bool RobloxWebsite(string website)
    {
        if (string.IsNullOrWhiteSpace(website)) return false;
        if (!Uri.TryCreate(website.Contains("://") ? website : "https://" + website, UriKind.Absolute, out var uri)) return false;
        return uri.Scheme is "https" or "http" && (uri.Host == "roblox.com" || uri.Host.EndsWith(".roblox.com", StringComparison.OrdinalIgnoreCase));
    }
    private static List<SavedLogin> Validate(IEnumerable<SavedLogin> items)
    {
        var list = items.ToList(); if (list.Count > 5000) throw new InvalidDataException("A password vault can contain up to 5,000 logins.");
        foreach (var entry in list)
            if (entry.Id == Guid.Empty || !RobloxWebsite(entry.Website) || string.IsNullOrWhiteSpace(entry.Username) || entry.Username.Length > 512 || string.IsNullOrEmpty(entry.Password) || entry.Password.Length > 4096 || entry.Name is null || entry.Name.Length > 200 || entry.LinkedUserId <= 0)
                throw new InvalidDataException("Each login needs a Roblox website, username/email and password within the supported limits.");
        if (list.Select(e => e.Id).Distinct().Count() != list.Count) throw new InvalidDataException("The password vault contains duplicate identifiers.");
        return list.Select(e => e with { Website = "https://www.roblox.com" }).ToList();
    }
    private void Write(List<SavedLogin> next)
    {
        if (!Available) throw new InvalidOperationException("The password vault could not be decrypted. Restore its backup before changing it.");
        var validated = Validate(next); var bytes = Encoding.UTF8.GetBytes(Json.Write(validated));
        try
        {
            if (bytes.Length > 24 * 1024 * 1024) throw new InvalidDataException("The password vault exceeds its supported encrypted size.");
            File.WriteAllBytes(FilePath + ".tmp", ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser));
            if (File.Exists(FilePath)) File.Replace(FilePath + ".tmp", FilePath, FilePath + ".backup", true); else File.Move(FilePath + ".tmp", FilePath);
            entries = validated;
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
        Changed?.Invoke();
    }
    public IReadOnlyList<SavedLogin> List() => entries.ToArray();
    public SavedLogin Get(Guid id) => entries.SingleOrDefault(e => e.Id == id) ?? throw new ArgumentException("Saved login not found.");
    public SavedLogin? ForAccount(Account account) => entries.FirstOrDefault(e => e.LinkedUserId == account.UserId) ?? entries.FirstOrDefault(e => e.Username.Equals(account.Username, StringComparison.OrdinalIgnoreCase));
    public void Save(SavedLogin entry) => Write(entries.Where(e => e.Id != entry.Id).Append(entry).ToList());
    public void Delete(Guid id) => Write(entries.Where(e => e.Id != id).ToList());
    public int Import(IEnumerable<SavedLogin> imported, bool preserveLinks = false)
    {
        var incoming = Validate(imported); var next = entries.ToList(); int added = 0;
        foreach (var entry in incoming)
        {
            // Import is additive: existing passwords are never silently replaced.
            if (next.Any(e => e.Username.Equals(entry.Username, StringComparison.OrdinalIgnoreCase))) continue;
            next.Add(entry with { Id = Guid.NewGuid(), LinkedUserId = preserveLinks ? entry.LinkedUserId : null }); added++;
        }
        Write(next); return added;
    }
    public void Link(Guid id, long userId)
    {
        var entry = Get(id);
        if (entry.LinkedUserId is not null && entry.LinkedUserId != userId) return;
        if (entry.LinkedUserId != userId) Save(entry with { LinkedUserId = userId });
    }
    public void RestorePrevious()
    {
        byte[] bytes = File.ReadAllBytes(FilePath + ".backup"); var restored = DecodeLocal(bytes);
        if (File.Exists(FilePath)) File.Copy(FilePath, FilePath + ".unreadable-" + Guid.NewGuid().ToString("N"));
        File.WriteAllBytes(FilePath + ".restore", bytes); File.Move(FilePath + ".restore", FilePath, true);
        entries = restored; Available = true; Changed?.Invoke();
    }
    public byte[] Export(string password)
    {
        if (!Available) throw new InvalidOperationException("The password vault is unavailable.");
        return EncodeExport(entries, password);
    }
    public static byte[] EncodeExport(IEnumerable<SavedLogin> entries, string password)
    {
        if (password.Length < 12) throw new ArgumentException("Use an export password of at least 12 characters.");
        var validated = Validate(entries);
        byte[] salt = RandomNumberGenerator.GetBytes(16), nonce = RandomNumberGenerator.GetBytes(12);
        byte[] key = Rfc2898DeriveBytes.Pbkdf2(password, salt, 600000, HashAlgorithmName.SHA256, 32), plain = Encoding.UTF8.GetBytes(Json.Write(validated));
        var cipher = new byte[plain.Length]; var tag = new byte[16];
        try { if (plain.Length > 24 * 1024 * 1024) throw new InvalidDataException("The password export is too large."); using var aes = new AesGcm(key, 16); aes.Encrypt(nonce, plain, cipher, tag, Magic); }
        finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(plain); }
        return [.. Magic, .. salt, .. nonce, .. tag, .. cipher];
    }
    public static List<SavedLogin> DecodeExport(byte[] bytes, string password)
    {
        if (bytes.Length < 52 || bytes.Length > 32 * 1024 * 1024 || !bytes.AsSpan(0, 8).SequenceEqual(Magic)) throw new InvalidDataException("This is not an Orbit password export.");
        byte[] key = Rfc2898DeriveBytes.Pbkdf2(password, bytes.AsSpan(8, 16), 600000, HashAlgorithmName.SHA256, 32), plain = new byte[bytes.Length - 52];
        try { using var aes = new AesGcm(key, 16); aes.Decrypt(bytes.AsSpan(24, 12), bytes.AsSpan(52), bytes.AsSpan(36, 16), plain, Magic); return Validate(Json.Read<List<SavedLogin>>(Encoding.UTF8.GetString(plain))); }
        catch (CryptographicException) { throw new InvalidDataException("Wrong export password or damaged export."); }
        finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(plain); }
    }
}
