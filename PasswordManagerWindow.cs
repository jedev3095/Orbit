namespace Orbit;

internal static class PasswordUi
{
    internal static readonly Color Background = Color.FromArgb(24, 26, 29), Surface = Color.FromArgb(41, 44, 49), Text = Color.FromArgb(225, 228, 234);
    internal static void Theme(Form form) { form.BackColor = Background; form.ForeColor = Text; form.Font = new("Segoe UI", 10); form.StartPosition = FormStartPosition.CenterParent; form.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
    internal static Button Button(string text, Action click) { var button = new Button { Text = text, AutoSize = true, BackColor = Surface, ForeColor = Text, FlatStyle = FlatStyle.Flat, Margin = new(4) }; button.Click += (_, _) => click(); return button; }
    internal static DataGridView Grid() => new()
    {
        Dock = DockStyle.Fill, BackgroundColor = Background, BorderStyle = BorderStyle.None, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        MultiSelect = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, RowHeadersVisible = false,
        EnableHeadersVisualStyles = false, ColumnHeadersDefaultCellStyle = new() { BackColor = Surface, ForeColor = Text },
        DefaultCellStyle = new() { BackColor = Background, ForeColor = Text, SelectionBackColor = Color.FromArgb(46, 62, 84), SelectionForeColor = Text }
    };
    internal static string? Password(IWin32Window owner, string title, bool confirmation)
    {
        using var dialog = new Form { Text = title, ClientSize = new(440, confirmation ? 240 : 175), FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false };
        Theme(dialog); var password = new TextBox { Location = new(22, 48), Width = 392, UseSystemPasswordChar = true };
        dialog.Controls.Add(new Label { Text = confirmation ? "Export password (at least 12 characters)" : "Export password", AutoSize = true, Location = new(22, 20) }); dialog.Controls.Add(password);
        var confirm = new TextBox { Location = new(22, 112), Width = 392, UseSystemPasswordChar = true };
        if (confirmation) { dialog.Controls.Add(new Label { Text = "Confirm password", AutoSize = true, Location = new(22, 84) }); dialog.Controls.Add(confirm); }
        var next = Button("Continue", () => { if (confirmation && (password.Text.Length < 12 || password.Text != confirm.Text)) { MessageBox.Show(dialog, "Use at least 12 characters and matching passwords.", title); return; } dialog.DialogResult = DialogResult.OK; });
        next.Location = new(302, confirmation ? 176 : 108); dialog.Controls.Add(next); dialog.AcceptButton = next;
        return dialog.ShowDialog(owner) == DialogResult.OK ? password.Text : null;
    }
}

public sealed class PasswordManagerWindow : Form
{
    private readonly CredentialVault store;
    private readonly Action<Guid>? use;
    private readonly DataGridView grid = PasswordUi.Grid();
    private readonly TextBox search = new() { Width = 260, PlaceholderText = "Search saved logins" };
    private readonly Label summary = new() { Dock = DockStyle.Top, Height = 60, Padding = new(12) };
    public PasswordManagerWindow(CredentialVault store, Action<Guid>? use = null, Account? target = null)
    {
        this.store = store; this.use = use; PasswordUi.Theme(this); Text = target is null ? "Local password manager — Orbit" : $"Refresh @{target.Username} — saved logins"; Size = new(850, 590); MinimumSize = new(680, 440);
        grid.Columns.Add("name", "Name"); grid.Columns.Add("username", "Username / email"); grid.Columns.Add("website", "Website"); grid.Columns.Add("added", "Added");
        var controls = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new(8) };
        controls.Controls.AddRange([search, PasswordUi.Button("Add login", Add), PasswordUi.Button("Edit", Edit), PasswordUi.Button("Delete", Delete), PasswordUi.Button("Import CSV", ImportCsv), PasswordUi.Button("Encrypted export", Export), PasswordUi.Button("Restore export", Restore), PasswordUi.Button("Recover previous", Recover)]);
        if (use is not null) controls.Controls.Add(PasswordUi.Button("Fill & sign in", Use));
        Controls.Add(grid); Controls.Add(summary); Controls.Add(controls);
        search.TextChanged += (_, _) => Populate(); grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) { if (use is null) Edit(); else Use(); } };
        Populate();
        store.Changed += Populate; FormClosed += (_, _) => store.Changed -= Populate;
        if (target is not null)
        {
            var match = store.ForAccount(target);
            foreach (DataGridViewRow row in grid.Rows) if (row.Tag is Guid id && id == match?.Id) { grid.ClearSelection(); row.Selected = true; grid.CurrentCell = row.Cells[0]; break; }
        }
    }
    private void Populate()
    {
        grid.Rows.Clear();
        foreach (var entry in store.List().Where(e => (e.Name + " " + e.Username).Contains(search.Text, StringComparison.OrdinalIgnoreCase)).OrderBy(e => e.Name).ThenBy(e => e.Username).ThenByDescending(e => e.AddedUtc))
            grid.Rows[grid.Rows.Add(entry.Name, entry.Username, entry.Website, entry.AddedUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "Existing login")].Tag = entry.Id;
        summary.Text = store.Available ? $"{store.Count} Roblox logins · Encrypted for this Windows user. No cloud sign-in. Passwords are used only on Roblox’s login page.\nUse encrypted export to back up or move these logins; account exports are separate." : "This password vault could not be decrypted. The file is preserved. Try Recover previous, or use the original Windows user account.";
    }
    private Guid? Selected => grid.CurrentRow?.Tag is Guid id ? id : null;
    private void Guard(Action action) { try { action(); Populate(); } catch (Exception ex) { MessageBox.Show(this, ex is InvalidDataException or ArgumentException ? ex.Message : "The operation could not finish. Check the file format, password, Windows account and available storage. Existing logins have been preserved.", "Local passwords"); } }
    private void Add() => Guard(() => { using var dialog = new LoginEditor(); if (dialog.ShowDialog(this) == DialogResult.OK) store.Save(dialog.Entry); });
    private void Edit() { if (Selected is not Guid id) return; Guard(() => { using var dialog = new LoginEditor(store.Get(id)); if (dialog.ShowDialog(this) == DialogResult.OK) store.Save(dialog.Entry); }); }
    private void Delete() { if (Selected is not Guid id) return; if (MessageBox.Show(this, "Remove this saved password? Its Roblox session and historical encrypted backups will remain.", "Remove login", MessageBoxButtons.YesNo) == DialogResult.Yes) Guard(() => store.Delete(id)); }
    private void Use() { if (Selected is Guid id && use is not null) { use(id); Close(); } }
    private void ImportCsv() => Guard(() =>
    {
        using var picker = new OpenFileDialog { Title = "Import Roblox logins from a password-manager CSV", Filter = "CSV export|*.csv" };
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        if (new FileInfo(picker.FileName).Length > 8 * 1024 * 1024) throw new InvalidDataException();
        var plan = CredentialCsv.Read(File.ReadAllText(picker.FileName, new System.Text.UTF8Encoding(false, true)));
        using var preview = new CredentialImportPreview(plan, store); if (preview.ShowDialog(this) != DialogResult.OK) return;
        int added = store.Import(preview.SelectedLogins); MessageBox.Show(this, $"Imported {added} logins. Only exact named-login duplicates were skipped. Different passwords and entry names were kept separately; existing passwords were not overwritten.\n\nOrbit encrypted its copy. The original CSV is still plaintext and has not been changed.", "Import complete");
    });
    private void Export() => Guard(() =>
    {
        string? password = PasswordUi.Password(this, "Encrypt password export", true); if (password is null) return;
        using var picker = new SaveFileDialog { Filter = "Encrypted Orbit logins|*.orbitlogins", FileName = "Orbit-passwords.orbitlogins", AddExtension = true };
        if (picker.ShowDialog(this) == DialogResult.OK) File.WriteAllBytes(picker.FileName, store.Export(password));
    });
    private void Restore() => Guard(() =>
    {
        using var picker = new OpenFileDialog { Filter = "Encrypted Orbit logins|*.orbitlogins" }; if (picker.ShowDialog(this) != DialogResult.OK) return;
        if (new FileInfo(picker.FileName).Length > 32 * 1024 * 1024) throw new InvalidDataException();
        string? password = PasswordUi.Password(this, "Unlock password export", false); if (password is null) return;
        var imported = CredentialVault.DecodeExport(File.ReadAllBytes(picker.FileName), password);
        using var preview = new CredentialImportPreview(new(imported, 0), store, preserveLinks: true); if (preview.ShowDialog(this) == DialogResult.OK) store.Import(preview.SelectedLogins, preserveLinks: true);
    });
    private void Recover() { if (MessageBox.Show(this, "Restore the previous encrypted local password vault? The current file will be preserved.", "Recover passwords", MessageBoxButtons.YesNo) == DialogResult.Yes) Guard(store.RestorePrevious); }
}

internal sealed class LoginEditor : Form
{
    private readonly SavedLogin original;
    private readonly TextBox name = new(), username = new(), password = new();
    public SavedLogin Entry => original with { Name = name.Text.Trim(), Username = username.Text.Trim(), Password = password.Text, LinkedUserId = original.Username.Equals(username.Text.Trim(), StringComparison.OrdinalIgnoreCase) ? original.LinkedUserId : null };
    public LoginEditor(SavedLogin? entry = null)
    {
        original = entry ?? new(); PasswordUi.Theme(this); Text = entry is null ? "Add Roblox login" : "Edit Roblox login"; ClientSize = new(470, 310); FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false;
        var fields = new[] { ("Name", name, original.Name), ("Username or email", username, original.Username), ("Password", password, original.Password) };
        int y = 18; foreach (var (label, input, value) in fields) { Controls.Add(new Label { Text = label, Location = new(20, y), AutoSize = true }); input.SetBounds(20, y + 23, 425, 28); input.Text = value; Controls.Add(input); y += 68; }
        password.UseSystemPasswordChar = true;
        var show = new CheckBox { Text = "Show password", Location = new(20, 225), AutoSize = true }; show.CheckedChanged += (_, _) => password.UseSystemPasswordChar = !show.Checked; Controls.Add(show);
        var save = PasswordUi.Button("Save encrypted", () => { if (string.IsNullOrWhiteSpace(username.Text) || password.Text.Length == 0 || username.Text.Length > 512 || password.Text.Length > 4096 || name.Text.Length > 200) { MessageBox.Show(this, "Enter a username/email and password within the supported limits.", "Saved login"); return; } DialogResult = DialogResult.OK; }); save.Location = new(298, 258); Controls.Add(save); AcceptButton = save;
    }
}

internal sealed class CredentialImportPreview : Form
{
    private readonly DataGridView grid = PasswordUi.Grid();
    public List<SavedLogin> SelectedLogins
    {
        get { grid.EndEdit(); return grid.Rows.Cast<DataGridViewRow>().Where(r => r.Cells[0].Value is true && r.Tag is SavedLogin).Select(r => (SavedLogin)r.Tag!).ToList(); }
    }
    public CredentialImportPreview(CredentialImportPlan plan, CredentialVault? store = null, bool preserveLinks = false)
    {
        PasswordUi.Theme(this); Text = "Review password import"; Size = new(1000, 570); MinimumSize = new(760, 440);
        grid.ReadOnly = false; grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "include", HeaderText = "Import", FillWeight = 45 });
        foreach (var (key, title) in new[] { ("row", "CSV record"), ("name", "Name"), ("username", "Username / email"), ("website", "Website"), ("status", "Import status / reason") }) { int index = grid.Columns.Add(key, title); grid.Columns[index].ReadOnly = true; }
        grid.Columns["row"].FillWeight = 55; grid.Columns["status"].FillWeight = 180;
        var rows = plan.Rows ?? plan.Logins.Select((entry, i) => new CredentialImportRow(i + 1, entry, entry.Name, entry.Username, "roblox.com", "Login", true, true)).ToList();
        var seen = store?.List().ToList() ?? [];
        foreach (var item in rows)
        {
            bool duplicate = item.Login is not null && seen.Any(e => CredentialVault.SameImportedLogin(e, item.Login) && (!preserveLinks || item.Login.LinkedUserId is null || e.LinkedUserId == item.Login.LinkedUserId));
            string status = duplicate ? "Exact duplicate (skipped)" : item.Selected && item.Login is not null ? store?.ImportDescription(item.Login) ?? item.Reason : item.Reason;
            int index = grid.Rows.Add(item.Selected && !duplicate, item.Number, item.Name, item.Username, item.Website, status);
            var row = grid.Rows[index]; row.Tag = item.Login; row.Cells[0].ReadOnly = !item.CanSelect || duplicate;
            if (item.Login is not null && item.Selected && !duplicate) seen.Add(item.Login);
        }
        var message = new Label { Dock = DockStyle.Top, Height = 105, Padding = new(12), Text = $"{rows.Count} records reviewed; {plan.Logins.Count} matched Roblox automatically. Every excluded record has a reason below.\nNo-website login rows are unchecked: tick one only if you know it belongs to Roblox. Different passwords/names stay separate; existing passwords are never overwritten.\nPasswords are hidden. Notes, passkeys and TOTP are not imported. CSV originals remain plaintext and unchanged." };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 46, FlowDirection = FlowDirection.RightToLeft };
        var import = PasswordUi.Button("Import selected encrypted", () => DialogResult = DialogResult.OK); buttons.Controls.Add(import); buttons.Controls.Add(PasswordUi.Button("Cancel", () => Close()));
        void UpdateButton() => import.Enabled = SelectedLogins.Count > 0;
        grid.CurrentCellDirtyStateChanged += (_, _) => { if (grid.IsCurrentCellDirty) grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
        grid.CellValueChanged += (_, _) => UpdateButton();
        Controls.Add(grid); Controls.Add(message); Controls.Add(buttons); UpdateButton();
    }
}
