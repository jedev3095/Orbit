# Build the offline Windows installer

The installer is a per-user NSIS package. It includes a self-contained Windows x64 publish of Orbit and the Microsoft-signed Evergreen Standalone WebView2 installer. The shared WebView2 runtime is installed only when missing and is never removed by Orbit's uninstaller.

Requirements for building:

- Windows, .NET 8 SDK and NSIS 3.13 (portable compiler is supported).
- Microsoft's full x64 [Evergreen Standalone Installer](https://developer.microsoft.com/microsoft-edge/webview2/), not its small online bootstrapper.

From the repository root:

```powershell
./installer/build.ps1 -NsisPath 'C:\Tools\nsis-3.13\makensis.exe' -WebViewInstaller 'C:\Tools\MicrosoftEdgeWebView2RuntimeInstallerX64.exe'
```

The version comes from Orbit.csproj. A fresh directory under publish is created for each build. Output defaults to publish/installers and includes one setup EXE, its SHA-256 checksum and a build manifest. The build verifies the WebView2 signature, rejects bootstrapper-sized files and private-data/test artifacts, and generates an explicit file list for uninstall. RuntimeVersion pins the bundled .NET servicing version and can be updated deliberately.

Add `-Verify` to run install/reinstall/uninstall checks on a clean Windows test account, with Orbit closed. Verification refuses to overwrite an existing installed Orbit or its shortcuts. It installs into a temporary test folder, checks every published file and shortcut, tests the running-app guard, reinstalls, then uninstalls. It checks that an unknown synthetic file is retained and that any existing account-data files are unchanged. A Windows VM with no WebView2 is still needed to exercise the prerequisite-install branch; local checks on a PC with WebView2 do not cover that branch.

The portable ZIP remains a separate, smaller option requiring the runtimes to be preinstalled. Installer builds do not include any account vaults, passwords, browser profiles or backups. The setup EXE is unsigned unless a separate signing step and certificate are supplied.

Upgrades use the same install location and per-user app identity. Setup and uninstall refuse to run while Orbit's application mutex exists. The uninstaller removes only shipped files and its own shortcuts/registration, leaving data under LocalAppData/Orbit and unknown files in the installation folder intact. No recursive user-data removal is performed.
