using System.Text;

namespace Orbit;

public record CredentialImportRow(int Number, SavedLogin? Login, string Name, string Username, string Website, string Reason, bool Selected, bool CanSelect);
public record CredentialImportPlan(List<SavedLogin> Logins, int Skipped, List<CredentialImportRow>? Rows = null);
public static class CredentialCsv
{
    public static CredentialImportPlan Read(string csv)
    {
        if (Encoding.UTF8.GetByteCount(csv) > 8 * 1024 * 1024) throw new InvalidDataException("CSV imports are limited to 8 MB.");
        var records = Parse(csv);
        if (records.Count == 0) throw new InvalidDataException("The CSV has no header.");
        var headers = records[0];
        string Normalize(string value) => value.Trim().TrimStart('\uFEFF').ToLowerInvariant().Replace("_", "").Replace(" ", "");
        var names = headers.Select(Normalize).ToList();
        if (names.GroupBy(n => n).Any(g => g.Count() > 1)) throw new InvalidDataException("Duplicate CSV headers are ambiguous. No logins were imported.");
        int Find(string canonical, params string[] aliases)
        {
            int exact = names.IndexOf(canonical); if (exact >= 0) return exact;
            var found = names.Select((name, index) => (name, index)).Where(n => aliases.Contains(n.name)).ToList();
            if (found.Count > 1) throw new InvalidDataException("Multiple columns match one login field. Use an unmodified password-manager export.");
            return found.Count == 1 ? found[0].index : -1;
        }
        int user = Find("loginusername", "username", "user", "login"), password = Find("loginpassword", "password"), website = Find("loginuri", "url", "website", "loginurl"), title = Find("name", "title"), type = Find("type");
        if (user < 0 || password < 0 || website < 0) throw new InvalidDataException("Expected website/url, username and password columns (or Bitwarden login_uri, login_username and login_password).");
        var rows = new List<CredentialImportRow>();
        for (int index = 1; index < records.Count; index++)
        {
            var fields = records[index]; int number = index + 1;
            if (fields.Count != headers.Count) throw new InvalidDataException($"CSV record {number} has an unexpected number of fields. No logins were imported.");
            string Value(int i) => i < 0 ? "" : fields[i];
            string label = Value(title); if (label.Length == 0) label = "Roblox"; if (label.Length > 200) label = label[..200];
            string username = Value(user).Trim(), secret = Value(password), sites = Value(website).Trim();
            string reason = "Roblox login"; bool canSelect = true, selected = true;
            // Bitwarden serializes its URI array into a comma-separated CSV cell.
            bool roblox = sites.Split([',', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Any(s => CredentialVault.RobloxWebsite(s.Trim()));
            if (type >= 0 && !Value(type).Trim().Equals("login", StringComparison.OrdinalIgnoreCase)) { reason = "Not a login item"; canSelect = selected = false; }
            else if (username.Length == 0) { reason = "Missing username/email"; canSelect = selected = false; }
            else if (secret.Length == 0) { reason = "No password (possibly passkey-only)"; canSelect = selected = false; }
            else if (username.Length > 512 || secret.Length > 4096) { reason = "Username/password exceeds limits"; canSelect = selected = false; }
            else if (sites.Length == 0) { reason = "No website: tick only if this is a Roblox login"; selected = false; }
            else if (!roblox) { reason = "Website is not Roblox"; canSelect = selected = false; }
            var candidate = canSelect ? new SavedLogin { Name = label, Username = username, Password = secret } : null;
            rows.Add(new(number, candidate, label, username.Length <= 512 ? username : "[too long]", sites.Length == 0 ? "[none]" : roblox ? "roblox.com" : "[non-Roblox site]", reason, selected, canSelect));
        }
        return new(rows.Where(r => r.Selected).Select(r => r.Login!).ToList(), rows.Count(r => !r.Selected), rows);
    }
    // Strict RFC-style CSV state machine: preserve every character inside a
    // field, including CR/LF sequences, blank lines, spaces, Unicode and quotes.
    private static List<List<string>> Parse(string csv)
    {
        var records = new List<List<string>>(); var fields = new List<string>(); var value = new StringBuilder();
        bool quoted = false, closed = false; int start = csv.StartsWith('\uFEFF') ? 1 : 0;
        void Field() { fields.Add(value.ToString()); value.Clear(); closed = false; if (fields.Count > 256) throw new InvalidDataException("The CSV has too many columns."); }
        void Record()
        {
            Field(); if (fields.Count != 1 || fields[0].Length != 0) records.Add(fields); fields = [];
            if (records.Count > 5001) throw new InvalidDataException("CSV imports are limited to 5,000 rows.");
        }
        for (int i = start; i < csv.Length; i++)
        {
            char c = csv[i];
            if (quoted)
            {
                if (c != '"') value.Append(c);
                else if (i + 1 < csv.Length && csv[i + 1] == '"') { value.Append('"'); i++; }
                else { quoted = false; closed = true; }
                continue;
            }
            if (c == ',') { Field(); continue; }
            if (c is '\r' or '\n') { Record(); if (c == '\r' && i + 1 < csv.Length && csv[i + 1] == '\n') i++; continue; }
            if (closed) throw new InvalidDataException("Unexpected characters after a quoted CSV field. No logins were imported.");
            if (c == '"') { if (value.Length != 0) throw new InvalidDataException("Malformed CSV quoting. No logins were imported."); quoted = true; }
            else value.Append(c);
        }
        if (quoted) throw new InvalidDataException("The CSV contains an unclosed quoted field. No logins were imported.");
        if (fields.Count > 0 || value.Length > 0 || closed) Record();
        return records;
    }
}
