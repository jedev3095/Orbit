using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace Orbit;

public sealed class CredentialTests
{
    private static string Temp() { string path = Path.Combine(Path.GetTempPath(), "Orbit.Tests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
    private static SavedLogin Login(string username = "synthetic-user", string password = "synthetic-private-password") => new() { Username = username, Password = password, Name = "Test Roblox" };
    [Fact] public void PasswordsAndUsernamesAreEncryptedInCurrentAndPreviousFiles()
    {
        var store = new CredentialVault(Temp()); var login = Login(); store.Save(login); store.Save(login with { Name = "Updated" });
        foreach (string file in new[] { store.FilePath, store.FilePath + ".backup" })
        {
            string bytes = Encoding.UTF8.GetString(File.ReadAllBytes(file)); Assert.DoesNotContain(login.Password, bytes); Assert.DoesNotContain(login.Username, bytes);
            Assert.ThrowsAny<CryptographicException>(() => ProtectedData.Unprotect(File.ReadAllBytes(file), "wrong-purpose"u8.ToArray(), DataProtectionScope.CurrentUser));
        }
        var reopened = new CredentialVault(Path.GetDirectoryName(store.FilePath)!); Assert.True(reopened.Available); Assert.Equal(login.Password, reopened.Get(login.Id).Password);
    }
    [Fact] public void CorruptVaultCannotBeOverwrittenAndPreviousCanBeRecovered()
    {
        string directory = Temp(); var store = new CredentialVault(directory); store.Save(Login()); store.Save(Login("second"));
        File.WriteAllText(store.FilePath, "corrupt"); var broken = new CredentialVault(directory); Assert.False(broken.Available);
        Assert.Throws<InvalidOperationException>(() => broken.Save(Login("third"))); Assert.Equal("corrupt", File.ReadAllText(store.FilePath));
        broken.RestorePrevious(); Assert.True(broken.Available); Assert.Single(broken.List());
        Assert.Equal("corrupt", File.ReadAllText(Directory.GetFiles(directory, "logins.dat.unreadable-*").Single()));
    }
    [Fact] public void PortableExportsUseAuthenticatedEncryptionAndDistinctRandomness()
    {
        var store = new CredentialVault(Temp()); var login = Login(); store.Save(login); const string password = "synthetic-export-password";
        var first = store.Export(password); var second = store.Export(password); Assert.NotEqual(first, second); Assert.DoesNotContain(login.Password, Encoding.UTF8.GetString(first));
        Assert.Equal(login.Password, Assert.Single(CredentialVault.DecodeExport(first, password)).Password);
        Assert.Throws<InvalidDataException>(() => CredentialVault.DecodeExport(first, "incorrect-password"));
        first[^1] ^= 1; Assert.Throws<InvalidDataException>(() => CredentialVault.DecodeExport(first, password));
        Assert.Throws<ArgumentException>(() => store.Export("short"));
    }
    [Fact] public void ImportsDoNotReplaceExistingPasswordsAndAreAtomicForInvalidRows()
    {
        var store = new CredentialVault(Temp()); var login = Login(); store.Save(login);
        Assert.Equal(2, store.Import([login with { Password = "another-password" }, Login("new-user")])); Assert.Equal(login.Password, store.Get(login.Id).Password);
        byte[] before = File.ReadAllBytes(store.FilePath);
        Assert.Throws<InvalidDataException>(() => store.Import([Login("valid-new"), Login("invalid") with { Website = "https://roblox.com.attacker.invalid" }]));
        Assert.Equal(before, File.ReadAllBytes(store.FilePath)); Assert.Equal(3, store.Count);
    }
    [Theory]
    [InlineData("https://www.roblox.com/login", true)]
    [InlineData("roblox.com", true)]
    [InlineData("https://roblox.com.attacker.invalid", false)]
    [InlineData("https://notroblox.com", false)]
    [InlineData("https://roblox.com@attacker.invalid", false)]
    [InlineData("file:///roblox.com", false)]
    public void CsvImportRequiresARealRobloxWebsite(string uri, bool expected) => Assert.Equal(expected, CredentialVault.RobloxWebsite(uri));
    [Fact] public void BitwardenCsvHandlesBomQuotesCommasMultilineAndSkipsUnrelatedData()
    {
        const string csv = "\uFEFFfolder,favorite,type,name,notes,fields,reprompt,login_uri,login_username,login_password,login_totp\r\n,0,login,\"Roblox, main\",\"Multiline\r\nnotes\",,0,https://www.roblox.com,synthetic-user,\"comma,quote\"\"password\",\r\n,0,login,Other,,,0,https://example.invalid,other,unrelated-password,\r\n,0,note,Note,private-notes,,,,,,\r\n";
        var result = CredentialCsv.Read(csv); Assert.Equal(2, result.Skipped); var entry = Assert.Single(result.Logins);
        Assert.Equal("Roblox, main", entry.Name); Assert.Equal("comma,quote\"password", entry.Password); Assert.Equal("synthetic-user", entry.Username);
    }
    [Theory]
    [InlineData("name,url,username,password\nRoblox,https://www.roblox.com,user,password-value")]
    [InlineData("Title,Url,Username,Password\nRoblox,https://roblox.com,user,password-value")]
    [InlineData("url,username,password,extra,name,grouping,fav\nhttps://www.roblox.com,user,password-value,,Roblox,,0")]
    public void CommonCsvHeadersAreRecognized(string csv) => Assert.Equal("password-value", Assert.Single(CredentialCsv.Read(csv).Logins).Password);
    [Fact] public void MalformedCsvErrorsNeverIncludePasswordContents()
    {
        var error = Assert.Throws<InvalidDataException>(() => CredentialCsv.Read("url,username,password\nhttps://www.roblox.com,user,\"secret-password-with-broken-quote"));
        Assert.DoesNotContain("secret-password", error.Message);
        Assert.Throws<InvalidDataException>(() => CredentialCsv.Read("name,username\nRoblox,user"));
    }
    [Fact] public void AppStateAndActivityNeverExposeStoredPasswords()
    {
        using var app = new AppService(new Vault(Temp())); var login = Login(); app.Credentials.Save(login);
        app.Log("Diagnostic " + login.Password); string state = Json.Write(app.State());
        Assert.DoesNotContain(login.Password, state); Assert.DoesNotContain(login.Username, state); Assert.Contains("[redacted]", state);
    }
    [Fact] public async Task RefreshCannotReplaceTheWrongAccount()
    {
        var vault = new Vault(Temp()); vault.Merge([new() { UserId = 43, Username = "different-user", SecurityToken = "synthetic_original_cookie_123456" }]);
        using var app = new AppService(vault, api: new RobloxApi(new SignInFixture()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => app.AcceptSession("synthetic_cookie_for_signin_12345", "from saved login", expectedUserId: 43));
        Assert.Equal(43, Assert.Single(vault.Data.Accounts).UserId); Assert.Equal("synthetic_original_cookie_123456", vault.Data.Accounts.Single().SecurityToken);
    }
    [Theory]
    [InlineData("https://www.roblox.com/login", true)]
    [InlineData("https://www.roblox.com/login?returnUrl=home", true)]
    [InlineData("http://www.roblox.com/login", false)]
    [InlineData("https://www.roblox.com:444/login", false)]
    [InlineData("https://www.roblox.com/home", false)]
    [InlineData("https://www.roblox.com.attacker.invalid/login", false)]
    public void PasswordFillIsRestrictedToTheHttpsLoginPage(string uri, bool expected) => Assert.Equal(expected, LoginAutofill.LoginPage(uri));
    [Fact] public async Task ScheduledBackupsIncludeASeparateEncryptedPasswordExport()
    {
        var vault = new Vault(Temp()); using var app = new AppService(vault); var entry = Login(); app.Credentials.Save(entry); string folder = Temp();
        const string password = "synthetic-backup-password"; app.Backups.Configure(folder, password); await app.Backups.RunNow();
        var file = Assert.Single(Directory.GetFiles(folder, "*.orbitlogins")); Assert.Equal(entry.Password, Assert.Single(CredentialVault.DecodeExport(File.ReadAllBytes(file), password)).Password);
        Assert.DoesNotContain(entry.Password, Encoding.UTF8.GetString(File.ReadAllBytes(file)));
    }
    [OptionalFact("ORBIT_UI_TESTS")]
    public Task NativePasswordManagerShowsMetadataWithoutPasswordColumns() => Sta.Run(async () =>
    {
        var store = new CredentialVault(Temp()); var entry = Login(); store.Save(entry);
        using var window = new PasswordManagerWindow(store); window.Show(); await Task.Delay(150);
        var grid = Assert.Single(window.Controls.OfType<DataGridView>());
        Assert.Equal(4, grid.Columns.Count); Assert.DoesNotContain(grid.Rows.Cast<DataGridViewRow>().SelectMany(row => row.Cells.Cast<DataGridViewCell>()), cell => Equals(cell.Value, entry.Password));
        string folder = Path.Combine(AppContext.BaseDirectory, "TestResults", "passwords"); Directory.CreateDirectory(folder);
        using var image = new System.Drawing.Bitmap(window.Width, window.Height); window.DrawToBitmap(image, new System.Drawing.Rectangle(0, 0, window.Width, window.Height)); image.Save(Path.Combine(folder, "manager.png"));
        window.Close();
    });
}
