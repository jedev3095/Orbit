using System.Security.Cryptography;
using System.Text;

namespace Orbit;

public sealed class Vault
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Orbit.AccountVault.v1");
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("ORBIT01\n");
    public string DirectoryPath { get; }
    public string FilePath => Path.Combine(DirectoryPath, "vault.dat");
    public Database Data { get; private set; } = new();
    public Vault(string directory) { DirectoryPath = directory; Directory.CreateDirectory(directory); }
    public void Load()
    {
        if (!File.Exists(FilePath)) return;
        try
        {
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), Entropy, DataProtectionScope.CurrentUser);
            try { Data = Json.Read<Database>(Encoding.UTF8.GetString(plain)); }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
        catch (Exception ex) { throw new InvalidDataException("The vault could not be opened. Your existing file has been preserved. Restore an Orbit backup or use the original Windows account.", ex); }
    }
    public void Save()
    {
        var plain = Encoding.UTF8.GetBytes(Json.Write(Data));
        try
        {
            var encrypted = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
            var temp = FilePath + ".tmp";
            File.WriteAllBytes(temp, encrypted);
            if (File.Exists(FilePath)) File.Replace(temp, FilePath, FilePath + ".backup", true);
            else File.Move(temp, FilePath);
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    public byte[] Export(string password) => EncodeBackup(Data, password);
    public static byte[] EncodeBackup(Database data, string password)
    {
        if (password.Length < 12) throw new ArgumentException("Use a backup password of at least 12 characters.");
        byte[] salt = RandomNumberGenerator.GetBytes(16), nonce = RandomNumberGenerator.GetBytes(12);
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, 600000, HashAlgorithmName.SHA256, 32);
        var plain = Encoding.UTF8.GetBytes(Json.Write(data));
        var cipher = new byte[plain.Length]; var tag = new byte[16];
        try { using var aes = new AesGcm(key, 16); aes.Encrypt(nonce, plain, cipher, tag, Magic); }
        finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(plain); }
        return [.. Magic, .. salt, .. nonce, .. tag, .. cipher];
    }
    public static Database DecodeBackup(byte[] bytes, string password)
    {
        if (bytes.Length < 52 || bytes.Length > 32 * 1024 * 1024 || !bytes.AsSpan(0, 8).SequenceEqual(Magic)) throw new InvalidDataException("This is not an Orbit backup.");
        var key = Rfc2898DeriveBytes.Pbkdf2(password, bytes.AsSpan(8, 16), 600000, HashAlgorithmName.SHA256, 32);
        var plain = new byte[bytes.Length - 52];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(bytes.AsSpan(24, 12), bytes.AsSpan(52), bytes.AsSpan(36, 16), plain, Magic);
            return Json.Read<Database>(Encoding.UTF8.GetString(plain));
        }
        catch (CryptographicException) { throw new InvalidDataException("Wrong backup password or damaged backup."); }
        finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(plain); }
    }
    public void RestorePrevious()
    {
        string backup = FilePath + ".backup";
        var bytes = File.ReadAllBytes(backup);
        var plain = ProtectedData.Unprotect(bytes, Entropy, DataProtectionScope.CurrentUser);
        Database recovered;
        try { recovered = Json.Read<Database>(Encoding.UTF8.GetString(plain)); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        // Validate before touching the damaged file. Keep both the damaged copy
        // and the known-good backup for manual recovery.
        if (File.Exists(FilePath)) File.Copy(FilePath, FilePath + ".unreadable-" + Guid.NewGuid().ToString("N"));
        File.WriteAllBytes(FilePath + ".restore", bytes); File.Move(FilePath + ".restore", FilePath, true); Data = recovered;
    }
    internal static List<Account> PrepareMerge(IEnumerable<Account> accounts)
    {
        var incoming = accounts.ToList();
        foreach (var a in incoming)
        {
            if (a.UserId <= 0 || string.IsNullOrWhiteSpace(a.Username)) throw new InvalidDataException("An imported account has no valid user ID or username.");
            a.SecurityToken = Input.Cookie(a.SecurityToken);
            a.Group = string.IsNullOrWhiteSpace(a.Group) ? "Default" : a.Group;
            a.Fields ??= [];
        }
        return incoming;
    }
    public int Merge(IEnumerable<Account> accounts)
    {
        var incoming = PrepareMerge(accounts);
        int added = 0;
        foreach (var a in incoming)
        {
            var existing = Data.Accounts.FirstOrDefault(x => x.UserId == a.UserId);
            if (existing is null) { a.Id = Guid.NewGuid(); Data.Accounts.Add(a); added++; }
            else { existing.SecurityToken = a.SecurityToken; existing.Status = "Unchecked"; existing.LastError = ""; }
        }
        Save(); return added;
    }
}
