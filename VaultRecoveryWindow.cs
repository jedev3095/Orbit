namespace Orbit;

public sealed class VaultRecoveryWindow : Form
{
    public VaultRecoveryWindow(Vault vault)
    {
        Text = "Recover your Orbit vault"; ClientSize = new(560, 240); FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen; BackColor = Color.FromArgb(24, 26, 29); ForeColor = Color.Gainsboro; Font = new("Segoe UI", 10);
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        var message = new Label { Location = new(24, 24), Size = new(510, 125), Text = "Orbit could not read your vault. The file has been preserved.\n\nYou can restore the previous local backup. Windows-encrypted vaults need the original Windows user account. If you have a portable encrypted export, keep it for recovery; no empty vault will overwrite this one." };
        var restore = new Button { Text = "Restore previous backup", Location = new(24, 174), Size = new(210, 34), Enabled = File.Exists(vault.FilePath + ".backup"), BackColor = Color.FromArgb(48, 52, 60) };
        restore.Click += (_, _) => { try { vault.RestorePrevious(); DialogResult = DialogResult.OK; Close(); } catch { message.Text = "The previous backup could not be decrypted or read. Both files are preserved. Use the original Windows account or recover from an encrypted export."; } };
        var close = new Button { Text = "Close", Location = new(420, 174), Size = new(110, 34), BackColor = Color.FromArgb(48, 52, 60) }; close.Click += (_, _) => Close();
        Controls.AddRange([message, restore, close]);
    }
}
