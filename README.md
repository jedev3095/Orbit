# Orbit

A Windows desktop remake of the main Roblox Account Manager workflows, with a compact WebView2 interface styled after the Relay app and a native C# backend. The interface uses Relay's charcoal palette, muted blue accent, horizontal tabs, left control pane, and dense table layout. Version 0.1 is a working foundation; it is not yet a complete replacement for every advanced feature in the old application.

Version 0.1.5 adds the generated Orbit app icon and a direct Open browser action for the selected account. Version 0.1.4 updates multi-instance handling for both Roblox singleton names, detects conflicts and reports readiness in Settings. Version 0.1.3 adds a choice of default browser sign-in or the separate Orbit browser window.

## Start

Extract the **whole Orbit-app ZIP** into a folder and run `Orbit.exe`. Keep its DLLs and `ui` folder alongside it. This build uses .NET Desktop Runtime 8 and Microsoft Edge WebView2 Runtime, both present on the development PC. Run as a standard Windows user.

Your first real workspace is empty. The screenshots under `verification` show explicitly labeled sample accounts. No existing Roblox Account Manager files are opened automatically.

Choose **Add account** and select where to sign in:

- **Default browser tab** opens Roblox Quick Login in your normal browser, where your existing Bitwarden extension is available. Orbit shows a short-lived code. Sign in to Roblox if needed, enter that code, review the account and approve the sign-in. Orbit then adds or refreshes the account automatically. Keep the Orbit sign-in window open while approving. The browser may choose an existing window or a new tab according to its own settings. If the page redirects to sign-in, use **Open browser** again afterward, or open Roblox Settings → Quick Login. Closing an uncompleted attempt cancels its code. Orbit reads no existing browser cookies, password databases or Bitwarden vault data.
- **Orbit browser window** keeps the previous embedded workflow: sign in, then choose **Use this account** in its toolbar. Browser profiles stay separate from your default browser. If Bitwarden's extension code is installed in a supported Chromium browser, Orbit can load it into its own profile; its Bitwarden button opens a separate extension popup where you sign in independently. The original extension data is not imported.
- **Add a session cookie** accepts a session you already own.

Refresh retrieves account details and presence. Roblox can reject a session or require additional verification; Orbit displays the failure rather than bypassing verification.

Select accounts, enter a place ID and optionally a server/job ID, then launch them. The game library can retrieve experience details and public servers from a place ID. Private-server mode takes a link code; current compatibility of the recovered launch protocol needs real-account testing. Queued launches can be cancelled; an in-flight Roblox request may finish first.

If a saved session has expired, the table shows **Needs login**. Click that status for the detailed error and a **Sign in with Roblox** action. A successful sign-in refreshes the existing account entry by user ID; aliases, groups, and notes remain. Decrypting an old import does not guarantee its saved sessions are still valid. Launches validate the session before requesting a ticket or closing a previously tracked process.

## Included

- Account sign-in windows with separate browser profiles, cookie import, aliases, groups, notes, and custom fields.
- Search, selection, presence/balance refresh, batch launching, public server selection, private/follow launch modes, favorites, and a game library.
- Windows-account encrypted storage, password-encrypted portable backups, and explicit import of old JSON/DPAPI/password-encrypted account files.
- Tracked process control, optional multi-instance handling, per-account auto-relaunch settings, and window position storage/restoration.
- Account tools for profile/balance/blocked-user reads, display-name updates, block/unblock, friendship requests, and quick-login authorization. Roblox API changes or verification requirements may limit an operation.
- A session activity log, privacy setting, two accent colors, a token-authenticated local HTTP API, and basic authenticated Nexus messaging.

Open an account's options from its name or the three-dot button. Expand **Relaunch and custom fields** to set its relaunch place ID and job ID. Orbit stops and monitors only processes whose unique browser tracker can be matched to its own launch; if process inspection is denied, it records that limitation instead of guessing which process to close.

Select exactly one account and click **Open browser** above the table to browse Roblox using its saved session. Each account opens in a separate Orbit browser profile; repeating the action brings its existing window forward. The same action is available in Account options. An expired session still requires signing in again. This account browser uses the embedded window so it can apply the selected account's session independently of your normal browser.

## Multiple Roblox clients

Close all Roblox clients and any other multi-instance manager. Open Orbit, enable **Settings → Multiple Roblox instances**, then **Save changes**. Settings must report **Multi-instance ready** before you launch the accounts. Keep Orbit open while playing. If it reports a conflict, close the existing clients/managers and save Settings again. No existing game is automatically stopped to enable this feature. A queue containing several accounts requires this setting.

Orbit holds `ROBLOX_singletonMutex` and reserves `ROBLOX_singletonEvent` as a mutex before Roblox starts. Version 0.1.3 and earlier held only the first name and could silently fail when Roblox already owned an object. The new guard acquires and releases both objects on one dedicated thread. This handles the observed singleton objects on this PC; future Roblox changes may require further compatibility work.

## Vault and imports

The real vault and separate WebView profiles live under `%LOCALAPPDATA%\Orbit`. The vault uses current-user Windows DPAPI. `vault.dat.backup` retains the previous save. The separate browser profiles retain their own browser session data; removing a vault entry does not delete those browser folders.

The last 200 activity entries are retained in `activity.json` in that folder. Saved cookies, passwords, API tokens, and launch tickets are redacted from generated launch errors. Account names and action descriptions remain visible for troubleshooting.

Export creates an `.orbit` backup using PBKDF2-SHA256 (600,000 iterations), a random salt, and AES-256-GCM. Use a password of at least 12 characters. Keep the password separately: a forgotten backup password cannot be recovered through Orbit.

Import prompts for a file rather than scanning for accounts. Supported old formats were reconstructed from the local 3.6.1 executable: plaintext account JSON, Windows DPAPI using the original entropy, and the password format using SHA-512, Argon2i Moderate, and libsodium SecretBox. Synthetic fixtures cover these formats; real 3.7.2 data has not been inspected. DPAPI files may need the original Windows installation/account. Imports merge by Roblox user ID and refresh duplicate session tokens; they do not overwrite existing aliases/groups. Orbit backup imports currently merge accounts only, not game-library or settings data.

## Local control API

Enable **Settings → Local controls** and save. The listener binds only to `127.0.0.1`, default port 7963. Copy its token using the native clipboard button. Every HTTP request and Nexus handshake needs:

```text
Authorization: Bearer <access token>
```

No cookie-export endpoint exists. Browser-origin requests are rejected and no CORS permission is granted.

| Endpoint | Request |
| --- | --- |
| `GET /v1/state` | Returns account metadata, IDs, settings without tokens, and activity. |
| `POST /v1/refresh` | `{"ids":["account-guid"]}` |
| `POST /v1/launch` | `{"ids":["account-guid"],"placeId":4924922222,"jobId":"","mode":"game"}` |
| `POST /v1/stop` | `{"ids":["account-guid"]}` |
| `ws://127.0.0.1:7963/Nexus?accountId=<guid>` | Authenticated WebSocket connection. |

Nexus accepts `{"Name":"ping"}` and answers `pong`; `{"Name":"Log","Payload":{"Content":"message"}}` adds a log entry. The Account control view sends user-entered text commands to selected clients. It does not install a Roblox client, execute code inside Roblox, or implement the old Nexus dynamic-widget/auto-execute protocol. Existing integrations need the new authentication and account GUID.

## Remaining parity work

The old application's automatic updater, custom browser JavaScript actions, bulk username/password login automation, avatar editor, password/email/PIN/privacy utilities, detailed server geolocation, legacy UWP paths, FPS/client-settings patcher, and full Nexus dynamic UI protocol have not been ported. There is no release feed for Orbit yet; it never runs the original app's updater. These are explicit gaps, not nonfunctional buttons in the UI.

This build has not been tested with live Roblox credentials, a game launch, or a real account mutation. The legacy mutex and authentication-ticket launch flow may no longer work with current Roblox clients. A completed feature-by-feature replacement will require those integration checks and the remaining ports.

## Build and verify

Use the .NET 8 SDK on Windows:

```powershell
dotnet build .\Orbit.csproj -c Release
dotnet publish .\Orbit.csproj -c Release -r win-x64 --self-contained false -o ..\Orbit-app
```

Builds normally use standard `bin` and `obj` folders. Optionally pass `-p:OrbitBuildRoot=<absolute-scratch-directory>` to keep all intermediate and compiled files outside the source folder.

The executable also supports `--self-test --output <absolute-json-path>` for offline storage/authentication tests. `--smoke --output <directory>` opens an isolated sample workspace, exercises the actual WebView bridge/UI, captures a screenshot, writes its JSON results, and exits. Add `--narrow` for the smaller layout, or `--smoke-empty` for empty-state checks. These modes never load the real Orbit vault or use Roblox credentials.

## Implementation references

The native bridge uses an exact local origin and a fixed command set, validates message size, prevents dashboard navigation, and never exposes a general host object. Roblox windows have no native message bridge. See Microsoft's [WebView2 security guidance](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/security).

Dependencies: [Microsoft.Web.WebView2](https://www.nuget.org/packages/Microsoft.Web.WebView2/1.0.4191.47), [System.Security.Cryptography.ProtectedData](https://www.nuget.org/packages/System.Security.Cryptography.ProtectedData/8.0.0), and [libsodium](https://www.nuget.org/packages/libsodium/1.0.22), with their respective licenses. Roblox API reference: [Creator Hub account APIs](https://create.roblox.com/docs/cloud/reference/features/accounts). This is an independent local application, unaffiliated with Roblox.
