namespace Orbit;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        try
        {
            using var instance = new Mutex(true, "Orbit.AccountManager." + Environment.UserName, out bool created);
            if (!created) { MessageBox.Show("Orbit is already open.", "Orbit"); return; }
            var vault = new Vault(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Orbit"));
            try { vault.Load(); }
            catch { using var recovery = new VaultRecoveryWindow(vault); if (recovery.ShowDialog() != DialogResult.OK) return; }
            using var app = new AppService(vault);
            Application.Run(new MainWindow(app));
        }
        catch { MessageBox.Show("Orbit could not start. Your vault has been preserved. Check the Windows user account and available storage.", "Orbit"); Environment.ExitCode = 1; }
    }
}
