using System.Text;
using Microsoft.VisualBasic.FileIO;

namespace Orbit;

public record CredentialImportPlan(List<SavedLogin> Logins, int Skipped);
public static class CredentialCsv
{
    public static CredentialImportPlan Read(string csv)
    {
        if (Encoding.UTF8.GetByteCount(csv) > 8 * 1024 * 1024) throw new InvalidDataException("CSV imports are limited to 8 MB.");
        using var parser = new TextFieldParser(new StringReader(csv)) { TextFieldType = FieldType.Delimited, HasFieldsEnclosedInQuotes = true, TrimWhiteSpace = false };
        parser.SetDelimiters(",");
        try
        {
            var headers = parser.ReadFields() ?? throw new InvalidDataException("The CSV has no header.");
            var names = headers.Select(h => h.Trim().TrimStart('\uFEFF').ToLowerInvariant().Replace("_", "").Replace(" ", "")).ToList();
            int Find(params string[] options) => names.FindIndex(n => options.Contains(n));
            int user = Find("loginusername", "username", "user", "login"), password = Find("loginpassword", "password"), website = Find("loginuri", "url", "website", "loginurl"), title = Find("name", "title"), type = Find("type");
            if (user < 0 || password < 0 || website < 0) throw new InvalidDataException("Expected website/url, username and password columns (or Bitwarden login_uri, login_username and login_password).");
            var result = new List<SavedLogin>(); int skipped = 0, rows = 0;
            while (!parser.EndOfData)
            {
                if (++rows > 5000) throw new InvalidDataException("CSV imports are limited to 5,000 rows.");
                var fields = parser.ReadFields() ?? [];
                if (fields.Length != headers.Length) throw new InvalidDataException($"CSV row {rows + 1} has an unexpected number of fields.");
                string Value(int i) => i < 0 ? "" : fields[i];
                string uri = Value(website).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(CredentialVault.RobloxWebsite) ?? "";
                if ((type >= 0 && Value(type) != "login") || uri.Length == 0 || string.IsNullOrWhiteSpace(Value(user)) || Value(password).Length == 0) { skipped++; continue; }
                if (Value(user).Length > 512 || Value(password).Length > 4096) throw new InvalidDataException($"CSV row {rows + 1} exceeds the login length limits.");
                string label = title < 0 || Value(title).Length == 0 ? "Roblox" : Value(title);
                if (label.Length > 200) label = label[..200];
                result.Add(new() { Name = label, Website = "https://www.roblox.com", Username = Value(user).Trim(), Password = Value(password) });
            }
            return new(result, skipped);
        }
        catch (MalformedLineException) { throw new InvalidDataException("The CSV contains malformed quoting. No logins were imported."); }
    }
}
