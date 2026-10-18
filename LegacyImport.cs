using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json;

namespace Orbit;

public static class LegacyImport
{
    public static List<Account> Read(byte[] bytes, string password)
    {
        if (bytes.Length > 32 * 1024 * 1024) throw new InvalidDataException("Import file is too large.");
        if (bytes.AsSpan().StartsWith("ORBIT01\n"u8)) return Vault.DecodeBackup(bytes, password).Accounts;
        byte[] plain = bytes;
        if (bytes.AsSpan().StartsWith("Roblox Account Manager created by"u8)) plain = SodiumDecrypt(bytes, password);
        else if (!Encoding.UTF8.GetString(bytes).TrimStart().StartsWith('['))
        {
            // The historical app's entropy is recovered from its static field.
            var entropy = Encoding.UTF8.GetBytes("ROBLOX ACCOUNT MANAGER | :) | BROUGHT TO YOU BUY ic3w0lf");
            plain = ProtectedData.Unprotect(bytes, entropy, DataProtectionScope.CurrentUser);
        }
        try
        {
            var items = JsonNode.Parse(plain)?.AsArray() ?? throw new InvalidDataException("Expected an account list.");
            return items.Select(node => new Account
            {
                UserId = node?["UserID"]?.GetValue<long>() ?? node?["userId"]?.GetValue<long>() ?? 0,
                Username = node?["Username"]?.GetValue<string>() ?? node?["username"]?.GetValue<string>() ?? "",
                SecurityToken = node?["SecurityToken"]?.GetValue<string>() ?? node?["securityToken"]?.GetValue<string>() ?? "",
                Alias = node?["Alias"]?.GetValue<string>() ?? "", Description = node?["Description"]?.GetValue<string>() ?? "",
                Group = node?["Group"]?.GetValue<string>() ?? "Default", Password = node?["Password"]?.GetValue<string>() ?? "",
                Fields = node?["Fields"]?.Deserialize<Dictionary<string, string>>(Json.Options) ?? []
            }).ToList();
        }
        finally { if (!ReferenceEquals(plain, bytes)) CryptographicOperations.ZeroMemory(plain); }
    }
    private static byte[] SodiumDecrypt(byte[] bytes, string password)
    {
        if (bytes.Length < 120) throw new InvalidDataException("Damaged account file.");
        if (password.Length == 0) throw new InvalidDataException("Enter the password used by the original account manager.");
        // Match Sodium 0.10's ArgonHashBinary(StrengthArgon.Moderate) and original SHA-512 password preprocessing.
        var passwordBytes = SHA512.HashData(Encoding.UTF8.GetBytes(password));
        var key = new byte[32]; var salt = bytes.AsSpan(64, 16).ToArray(); var nonce = bytes.AsSpan(80, 24).ToArray();
        var cipher = bytes.AsSpan(104).ToArray(); var plain = new byte[cipher.Length - 16];
        try
        {
            Native.sodium_init();
            if (Native.crypto_pwhash(key, 32, passwordBytes, (ulong)passwordBytes.Length, salt, 6, (nuint)134217728, 1) != 0)
                throw new InvalidDataException("Could not derive the legacy vault key.");
            if (Native.crypto_secretbox_open_easy(plain, cipher, (ulong)cipher.Length, nonce, key) != 0) throw new InvalidDataException("Wrong legacy password or damaged account file.");
            return plain;
        }
        finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(passwordBytes); }
    }
    private static class Native
    {
        [DllImport("libsodium", CallingConvention = CallingConvention.Cdecl)] public static extern int sodium_init();
        [DllImport("libsodium", CallingConvention = CallingConvention.Cdecl)] public static extern int crypto_pwhash(byte[] output, ulong outlen, byte[] password, ulong passwordlen, byte[] salt, ulong opslimit, nuint memlimit, int algorithm);
        [DllImport("libsodium", CallingConvention = CallingConvention.Cdecl)] public static extern int crypto_secretbox_open_easy(byte[] output, byte[] cipher, ulong length, byte[] nonce, byte[] key);
    }
}
