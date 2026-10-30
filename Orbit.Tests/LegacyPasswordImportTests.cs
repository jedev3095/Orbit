using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Orbit;

public sealed class LegacyPasswordImportTests
{
    private const string Cookie = "synthetic_cookie_for_tests_only_123456";
    private const string Secret = "  synthetic,\"password\n\nwith blank lines🔐  ";
    private static string Temp() => Path.Combine(Path.GetTempPath(), "Orbit.Tests", Guid.NewGuid().ToString("N"));
    private static byte[] Input(string password = Secret) => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new[]
    {
        new { UserID = 42, Username = "legacy-password-test", SecurityToken = Cookie, Password = password },
        new { UserID = 43, Username = "session-only-test", SecurityToken = Cookie, Password = "" }
    }));
    [Fact] public void PasswordsAreSeparatedFromSessionRecordsAndSurviveEncryptedStorage()
    {
        var data = LegacyImport.ReadContents(Input(), "");
        Assert.Equal(2, data.Accounts.Count); var login = Assert.Single(data.Logins);
        Assert.Equal(Secret, login.Password); Assert.Equal(42L, login.LinkedUserId);
        Assert.DoesNotContain(Secret, Json.Write(data.Accounts)); Assert.Null(typeof(Account).GetProperty("Password"));
        var vault = new Vault(Temp()); var credentials = new CredentialVault(vault.DirectoryPath);
        Assert.Equal(new AccountImportResult(2, 1), LegacyImport.Apply(vault, credentials, data));
        var reopened = new CredentialVault(vault.DirectoryPath);
        Assert.Equal(Secret, Assert.Single(reopened.List()).Password);
        Assert.Equal(login.Username, reopened.ForAccount(vault.Data.Accounts.Single(a => a.UserId == 42))?.Username);
        Assert.DoesNotContain("synthetic,", Encoding.UTF8.GetString(File.ReadAllBytes(credentials.FilePath)));
        Assert.DoesNotContain("\"password\":", Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(vault.FilePath), Encoding.UTF8.GetBytes("Orbit.AccountVault.v1"), DataProtectionScope.CurrentUser)), StringComparison.OrdinalIgnoreCase);
    }
    [Fact] public void ReimportAddsPasswordsForAlreadyImportedAccountsWithoutReplacingVariants()
    {
        var data = LegacyImport.ReadContents(Input(), ""); var vault = new Vault(Temp()); vault.Merge(data.Accounts);
        var credentials = new CredentialVault(vault.DirectoryPath);
        var old = new SavedLogin { Name = "legacy-password-test", Username = "legacy-password-test", Password = "older-synthetic-password", LinkedUserId = 42 };
        credentials.Save(old);
        Assert.Equal(new AccountImportResult(0, 1), LegacyImport.Apply(vault, credentials, data));
        Assert.Equal(2, credentials.Count); Assert.Equal(old.Password, credentials.Get(old.Id).Password);
        Assert.Null(credentials.ForAccount(vault.Data.Accounts.Single(a => a.UserId == 42)));
        Assert.Equal(new AccountImportResult(0, 0), LegacyImport.Apply(vault, credentials, LegacyImport.ReadContents(Input(), "")));
        Assert.Equal(2, credentials.Count);
    }
    [Fact] public void InvalidSessionsAndPasswordsAreRejectedBeforeChangingEitherStore()
    {
        var vault = new Vault(Temp()); var credentials = new CredentialVault(vault.DirectoryPath);
        var data = LegacyImport.ReadContents(Input(), ""); data.Accounts[1].UserId = 0;
        Assert.Throws<InvalidDataException>(() => LegacyImport.Apply(vault, credentials, data));
        Assert.False(File.Exists(vault.FilePath)); Assert.False(File.Exists(credentials.FilePath));
        Assert.Throws<InvalidDataException>(() => LegacyImport.Apply(vault, credentials, LegacyImport.ReadContents(Input(new string('x', 4097)), "")));
        Assert.Empty(vault.Data.Accounts); Assert.False(File.Exists(vault.FilePath)); Assert.False(File.Exists(credentials.FilePath));
    }
    [Fact] public void UnavailablePasswordVaultCannotSilentlyDropSelectedPasswords()
    {
        var vault = new Vault(Temp()); Directory.CreateDirectory(vault.DirectoryPath);
        File.WriteAllText(Path.Combine(vault.DirectoryPath,"logins.dat"),"damaged");
        var credentials = new CredentialVault(vault.DirectoryPath);
        Assert.Throws<InvalidOperationException>(() => LegacyImport.Apply(vault, credentials, LegacyImport.ReadContents(Input(), "")));
        Assert.Empty(vault.Data.Accounts); Assert.False(File.Exists(vault.FilePath));
        Assert.Equal("damaged", File.ReadAllText(credentials.FilePath));
    }
    [Fact] public void LegacyDpapiAndPasswordEncryptedFilesPreserveSavedPasswords()
    {
        var bytes = ProtectedData.Protect(Input(), Encoding.UTF8.GetBytes("ROBLOX ACCOUNT MANAGER | :) | BROUGHT TO YOU BUY ic3w0lf"), DataProtectionScope.LocalMachine);
        Assert.Equal(Secret, Assert.Single(LegacyImport.ReadContents(bytes, "").Logins).Password);
        var locked = Convert.FromBase64String("Um9ibG94IEFjY291bnQgTWFuYWdlciBjcmVhdGVkIGJ5IGljM3cwbGYyMiBAIGdpdGh1Yi5jb20gLi4uLi4uLgABAgMEBQYHCAkKCwwNDg8AAQIDBAUGBwgJCgsMDQ4PEBESExQVFhfDuMi5XWkRCr1YJCpp8ai9e8Y5nqGpCZCM/gpYYBjt1Lt5XBj3DmRERXzogEQ0qFrUh7G4c88NOi/O02JwINERIW1EW1j2guTDSEVSwPYS2qBoi/G5qQyUApi0MiDUaYe1GLUww7ORkfApgevLiIPqNG0BWiWYnfXcU4CEjG0FFll4LEkcCMVBlzTSc/JczzbQZYVfMwMFyKYv8LKavi7MOILCONyMutkOmPRoKMP4btGRFQf3sJiM2gaoeWQ3gA==");
        Assert.Equal(Secret, Assert.Single(LegacyImport.ReadContents(locked,"fixture-password").Logins).Password);
        Assert.Throws<InvalidDataException>(() => LegacyImport.ReadContents(locked,"incorrect-password"));
        locked[^1] ^= 1;
        Assert.Throws<InvalidDataException>(() => LegacyImport.ReadContents(locked,"fixture-password"));
    }
    [Fact] public void MalformedPasswordErrorsDoNotEchoSecretValues()
    {
        var error = Assert.Throws<InvalidDataException>(() => LegacyImport.ReadContents(Encoding.UTF8.GetBytes("[{\"UserID\":42,\"Username\":\"test\",\"Password\":{\"secret\":\"private-test-value\"}}]"), ""));
        Assert.DoesNotContain("private-test-value", error.Message);
    }
    [Fact] public void OrbitSessionBackupsStillImportWithoutInventingPasswords()
    {
        var vault = new Vault(Temp()); vault.Merge(LegacyImport.ReadContents(Input(), "").Accounts);
        var data = LegacyImport.ReadContents(vault.Export("synthetic-backup-password"), "synthetic-backup-password");
        Assert.Equal(2, data.Accounts.Count); Assert.Empty(data.Logins);
    }
    [OptionalFact("ORBIT_UI_TESTS")]
    public Task NativeRamPreviewAllowsAccountMergeWithDuplicateOrDeselectedPasswords() => Sta.Run(async () =>
    {
        var data = LegacyImport.ReadContents(Input(), ""); var credentials = new CredentialVault(Temp()); credentials.Import(data.Logins, preserveLinks:true);
        using var preview = new CredentialImportPreview(new(data.Logins,0),credentials,preserveLinks:true,accountCount:data.Accounts.Count);
        preview.Show(); await Task.Delay(100);
        Assert.Empty(preview.SelectedLogins);
        var grid = Assert.Single(preview.Controls.OfType<DataGridView>());
        Assert.True(grid.Rows[0].Cells[0].ReadOnly);
        var import = Assert.Single(preview.Controls.OfType<FlowLayoutPanel>().SelectMany(p => p.Controls.OfType<Button>()), b => b.Text.StartsWith("Import accounts"));
        Assert.True(import.Enabled);
        Assert.Contains(preview.Controls.OfType<Label>(), l => l.Text.Contains("2 account records. Saved passwords found: 1."));
        var folder = Path.Combine(AppContext.BaseDirectory,"TestResults","passwords"); Directory.CreateDirectory(folder);
        using var bitmap = new System.Drawing.Bitmap(preview.Width,preview.Height); preview.DrawToBitmap(bitmap,new System.Drawing.Rectangle(0,0,preview.Width,preview.Height)); bitmap.Save(Path.Combine(folder,"ram-import-preview.png"));
        preview.Close();
    });
}
