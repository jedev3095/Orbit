using Xunit;

namespace Orbit;

public sealed class CsvAuditTests
{
    private static string Quote(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
    [Fact] public void BitwardenPasswordCharactersMustRoundTripExactly()
    {
        string[] passwords = [" leading", "trailing ", " both ", "comma,password", "quote\"password", "tab\tpassword", "line\nfeed", "carriage\rreturn", "windows\r\nlines", "blank\n\nline", "Unicode-🔐-é-中", "<&>\\/='`$", "quote\"\",comma", "  \t spaced\t  "];
        foreach (string password in passwords)
        {
            string csv = "folder,favorite,type,name,notes,fields,reprompt,login_uri,login_username,login_password,login_totp\r\n" +
                ",0,login,Roblox,\"notes, with quotes \"\" and\nnew lines\",,0,https://www.roblox.com,user," + Quote(password) + ",\r\n";
            var entry = Assert.Single(CredentialCsv.Read(csv).Logins);
            Assert.Equal(password, entry.Password);
        }
    }
    [Fact] public void BitwardenMultipleUrisAndLoginTypeCasingAreHandled()
    {
        string csv = "type,name,login_uri,login_username,login_password\nLogin,Roblox,\"https://example.invalid,https://www.roblox.com/login\",user,pw-value\n";
        Assert.Equal("pw-value", Assert.Single(CredentialCsv.Read(csv).Logins).Password);
    }
    [Fact] public void MissingSitesAndOtherSkippedRowsHaveReasonsAndExplicitChoices()
    {
        string csv = "type,name,login_uri,login_username,login_password\nlogin,No site,,user,pw-value\nlogin,Other,https://example.invalid,user,pw-value\nlogin,Passkey,https://roblox.com,user,\nnote,Note,,,\n";
        var plan = CredentialCsv.Read(csv); Assert.Empty(plan.Logins); Assert.Equal(4, plan.Skipped);
        Assert.NotNull(plan.Rows); Assert.Equal(4, plan.Rows.Count);
        Assert.True(plan.Rows[0].CanSelect); Assert.False(plan.Rows[0].Selected); Assert.NotNull(plan.Rows[0].Login);
        Assert.All(plan.Rows.Skip(1), row => Assert.False(row.CanSelect));
        Assert.All(plan.Rows, row => { Assert.NotEmpty(row.Reason); Assert.DoesNotContain("pw-value", row.Reason); });
    }
    [Fact] public void CanonicalBitwardenColumnsCannotBeDisplacedByOtherNamedColumns()
    {
        string csv = "name,password,login_password,url,login_uri,username,login_username\nRoblox,incorrect,correct-password,https://example.invalid,https://roblox.com,wrong-user,right-user\n";
        var login = Assert.Single(CredentialCsv.Read(csv).Logins); Assert.Equal("right-user", login.Username); Assert.Equal("correct-password", login.Password);
        Assert.Throws<InvalidDataException>(() => CredentialCsv.Read("url,username,password,password\nhttps://roblox.com,user,one,two"));
    }
    [Fact] public void DistinctBitwardenEntriesSurviveSharedEmailsAndChangedPasswords()
    {
        string directory = Path.Combine(Path.GetTempPath(), "Orbit.Tests", Guid.NewGuid().ToString("N")); var store = new CredentialVault(directory);
        SavedLogin One(string name, string password) => new() { Name = name, Username = "shared@example.invalid", Password = password };
        var original = One("Roblox A", "old-password"); store.Save(original); store.Link(original.Id, 42);
        Assert.Equal(3, store.Import([One("Roblox A", "new-password"), One("Roblox B", "b-password"), One("Roblox C", "new-password"), One("Roblox A", "new-password")]));
        Assert.Equal(4, store.Count); Assert.Equal("old-password", store.Get(original.Id).Password);
        var account = new Account { UserId = 42, Username = "actual-account-name" }; Assert.Null(store.ForAccount(account));
        var updated = store.List().Single(e => e.Name == "Roblox A" && e.Password == "new-password"); store.Link(updated.Id, 42);
        Assert.Equal(updated.Id, store.ForAccount(account)?.Id);
        var reopened = new CredentialVault(directory); Assert.Equal(4, reopened.Count); Assert.Equal(updated.Id, reopened.ForAccount(account)?.Id);
    }
    [Fact] public void ManyQuotedPasswordsRoundTripWithoutTransformations()
    {
        var random = new Random(42); const string alphabet = "Ab0 ,\"\t\r\n\\=<>&é中";
        for (int trial = 0; trial < 150; trial++)
        {
            string password = new(Enumerable.Range(0, 1 + random.Next(100)).Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray());
            string csv = "login_uri,login_username,login_password\r\nhttps://roblox.com,user," + Quote(password) + "\r\n";
            Assert.Equal(password, Assert.Single(CredentialCsv.Read(csv).Logins).Password);
        }
    }
    [Fact] public void LongestPasswordIsRedactedBeforeItsPrefix()
        => Assert.Equal("[redacted]", Diagnostics.Redact("synthetic-password-long", "synthetic-password", "synthetic-password-long"));
    [Fact] public void FullCsvImportAndEncryptedReloadKeepEveryPasswordCharacter()
    {
        string[] passwords = [" leading and trailing ", "two\n\nline breaks", "CR\rLF\r\nexact", "quotes\"\"commas,unicode🔐", "\\tab\tand,quotes\""];
        var lines = passwords.Select((password, i) => "https://roblox.com,user-" + i + "," + Quote(password));
        var plan = CredentialCsv.Read("login_uri,login_username,login_password\r\n" + string.Join("\r\n", lines));
        string directory = Path.Combine(Path.GetTempPath(), "Orbit.Tests", Guid.NewGuid().ToString("N")); var store = new CredentialVault(directory);
        Assert.Equal(passwords.Length, store.Import(plan.Logins)); var reopened = new CredentialVault(directory);
        for (int i = 0; i < passwords.Length; i++) Assert.Equal(passwords[i], reopened.List().Single(e => e.Username == "user-" + i).Password);
    }
    [OptionalFact("ORBIT_UI_TESTS")]
    public Task NativeImportPreviewExplainsSkipsAndAllowsExplicitMissingSiteSelection() => Sta.Run(async () =>
    {
        string directory = Path.Combine(Path.GetTempPath(), "Orbit.Tests", Guid.NewGuid().ToString("N")); var store = new CredentialVault(directory);
        store.Save(new() { Name = "Roblox", Username = "shared", Password = "old-password" });
        var plan = CredentialCsv.Read("name,login_uri,login_username,login_password\nRoblox,https://roblox.com,shared,new-password\nMissing site,,second,new-password\nOther,https://example.invalid,other,other-password\nRoblox,https://roblox.com,shared,old-password\n");
        using var preview = new CredentialImportPreview(plan, store); preview.Show(); await Task.Delay(100);
        var grid = Assert.Single(preview.Controls.OfType<DataGridView>());
        Assert.Single(preview.SelectedLogins); Assert.False(grid.Rows[1].Cells[0].ReadOnly); Assert.True(grid.Rows[2].Cells[0].ReadOnly); Assert.True(grid.Rows[3].Cells[0].ReadOnly);
        grid.Rows[1].Cells[0].Value = true; Assert.Equal(2, preview.SelectedLogins.Count);
        Assert.Equal(2, store.Import(preview.SelectedLogins)); Assert.Equal(3, store.Count); Assert.Contains(store.List(), e => e.Username == "second");
        string folder = Path.Combine(AppContext.BaseDirectory, "TestResults", "passwords"); Directory.CreateDirectory(folder);
        using var image = new System.Drawing.Bitmap(preview.Width, preview.Height); preview.DrawToBitmap(image, new System.Drawing.Rectangle(0, 0, preview.Width, preview.Height)); image.Save(Path.Combine(folder, "import-preview.png"));
        preview.Close();
    });
}
