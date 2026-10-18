namespace Orbit;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (args.Contains("--self-test")) { Environment.ExitCode = SelfTests.Run(args); return; }
        if (args.Contains("--quicklogin-probe"))
        {
            // No existing account or browser storage is opened. Do not print the generated code/key.
            var outputIndex = Array.IndexOf(args, "--output");
            var output = args[outputIndex + 1];
            using var quick = new QuickLogin();
            try
            {
                string code = quick.Create().GetAwaiter().GetResult();
                string status = quick.Status().GetAwaiter().GetResult();
                bool passed = status == "Created" && quick.Expires > DateTimeOffset.UtcNow;
                File.WriteAllText(output, Json.Write(new { passed, codeCreated = code.Length > 0, status, expiresInFuture = quick.Expires > DateTimeOffset.UtcNow }));
                if (!passed) Environment.ExitCode = 1;
            }
            catch (Exception ex) { File.WriteAllText(output, Json.Write(new { passed = false, error = ex.Message })); Environment.ExitCode = 1; }
            finally { quick.Cancel().GetAwaiter().GetResult(); }
            return;
        }
        try
        {
            bool extensionSmoke = args.Contains("--bitwarden-smoke");
            bool smoke = args.Contains("--smoke") || extensionSmoke;
            var path = smoke ? Path.Combine(Path.GetTempPath(), "OrbitSmoke", Guid.NewGuid().ToString("N")) : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Orbit");
            using var instance = new Mutex(true, smoke ? "Orbit.Smoke." + Guid.NewGuid() : "Orbit.AccountManager." + Environment.UserName, out bool created);
            if (!created) { MessageBox.Show("Orbit is already open.", "Orbit"); return; }
            var vault = new Vault(path); vault.Load();
            using var app = new AppService(vault, smoke);
            if (args.Contains("--smoke-empty")) { vault.Data.Accounts.Clear(); vault.Data.Games.Clear(); app.Activity.Clear(); }
            if (extensionSmoke)
            {
                int outputIndex = Array.IndexOf(args, "--output");
                string output = outputIndex >= 0 ? args[outputIndex + 1] : Path.Combine(path, "verification");
                Application.Run(new AccountBrowser(app, smokeOutput: output));
            }
            else Application.Run(new MainWindow(app, smoke, args));
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Orbit could not start", MessageBoxButtons.OK, MessageBoxIcon.Error); Environment.ExitCode = 1; }
    }
}
