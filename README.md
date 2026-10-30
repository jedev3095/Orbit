# Orbit

A Windows Roblox account manager with a charcoal WebView2 dashboard, an encrypted local vault and separate account browser sessions. The interface is styled after Relay. Orbit is an independent application, unaffiliated with Roblox.

## Run

Download the app ZIP from Releases, extract the **entire archive** into one folder and run `Orbit.exe`. Keep the DLLs and `ui` folder beside it. Requires .NET Desktop Runtime 8 and Microsoft Edge WebView2 Runtime. Local published builds belong in `app/` or `publish/`; they are ignored by Git.

## Accounts and browser sessions

Choose **Add account**:

- **Saved local login** opens the local password manager. Add a login or import a password-manager CSV, then choose Fill & sign in. No cloud password-manager login is needed.
- **Default browser tab** opens Roblox Quick Login in your normal browser, where existing password-manager extensions are available. Enter the code shown by Orbit, review the account and approve. Orbit then saves the resulting session. If Roblox redirects to sign-in first, choose Open browser again afterward or open Roblox Settings → Quick Login.
- **Orbit browser window** opens a separate browser profile. Finish signing in and any verification; when Roblox opens its home page, Orbit validates the session, saves the account and selects it in the table automatically. **Use this account** remains available as a manual retry. Failed validation is shown in the browser and Activity. The embedded browser no longer loads Bitwarden or other browser extensions.
- **Session cookie** accepts an existing session belonging to you.

Select exactly one account and choose **Open browser** above the table to browse using that saved session. Repeating the action brings its account window forward. An expired or revoked session requires signing in again; re-adding the same user refreshes its session while retaining aliases, groups and notes.

Game launching still uses `.ROBLOSECURITY`. Passwords deliberately saved in the new local password manager live in a separate encrypted store; saved passwords found in original RAM account imports can be reviewed and copied into that separate store. Passwords are never added to session records or the local API.

## Local password manager

Open **Settings → Password manager / CSV import**, or **Add account → Saved local login**. This manager is scoped to Roblox logins. Add, edit or delete entries locally; passwords are masked and never included in the dashboard state or local API. There is no cloud account, telemetry upload or separate master-password unlock.

The file `%LOCALAPPDATA%\Orbit\logins.dat` and its previous-save backup are encrypted with Windows DPAPI **CurrentUser**, with purpose-specific entropy. It unlocks through your existing Windows user session. This protects copied files at rest; it does not isolate them from other software already running as the same Windows user. Recovery offers the previous local password-vault backup and preserves unreadable originals. Old extension profiles are left untouched but are no longer opened or loaded.

**Import CSV** accepts UTF-8 comma-separated Bitwarden exports and common browser/LastPass/1Password-style username/password exports with recognized header names. Password characters, spaces, quoted commas, escaped quotes and line breaks (including blank lines) are preserved exactly. Bitwarden entries with multiple websites and the official Roblox Android app URI are supported. The preview shows every record with an import status or exclusion reason. Valid Roblox entries are selected automatically; entries with missing or unrecognized websites stay unchecked unless you explicitly select them as Roblox logins. The preview shows source domains/app identifiers while omitting embedded credentials, paths and URL parameters. Click a checkbox or press Space to select a valid login; exact duplicates and rows missing required credentials cannot be selected. Secure notes, passkeys and TOTP are not imported.

Only exact duplicates with the same name, username and password are skipped. Different names or passwords stay as separate entries, without overwriting existing passwords. The Added column helps distinguish new imports. When several saved logins could match an account, Orbit asks you to choose; successful sign-in verifies that choice. Newly imported password variants require review again. Edit an existing entry explicitly to change its password. Imports are bounded by file/row limits; malformed CSV is rejected before any stored entries change.

If you imported a Bitwarden CSV using an earlier version, re-import the original CSV with version 0.3.1 or later and review the row statuses. Earlier versions could skip shared usernames or multi-website records and lose blank lines inside quoted passwords. Re-import keeps existing entries and adds distinct variants; it does not automatically remove old passwords.

CSV exports are plaintext. Orbit reads your chosen file and encrypts its own stored copy without changing the original. Remove or protect the original export after use; Orbit does not claim to encrypt or erase that source file. See [Bitwarden export formats](https://bitwarden.com/help/export-your-data/) and [CSV field definitions](https://bitwarden.com/help/condition-bitwarden-import/).

For an expired account, open its account options or error dialog and choose **Refresh sign-in**. A matching saved login opens a fresh embedded login profile and fills/submits the official HTTPS Roblox login form once. You complete CAPTCHA or two-step verification if required. After the home page opens, Orbit validates and stores the new session. It checks the returned Roblox user ID against the selected account, so a wrong saved login cannot replace that account. A successful login can be linked by user ID, including logins that use an email address. Autofill is restricted to the top-level Roblox login page and will not submit again automatically after a login failure.

**Encrypted export** creates an `.orbitlogins` file using AES-256-GCM and PBKDF2-SHA256 (600,000 iterations), with independent random salt/nonce and a password of at least 12 characters. Restore export previews and merges these logins without overwriting existing usernames. These password exports are separate from ordinary `.orbit` account exports. Scheduled daily backups now produce an additional encrypted `.orbitlogins` snapshot using the configured backup password, retaining the latest seven of each type. Historical backups can still contain deleted entries; keep a copy of the export password for recovery on another Windows account or PC.

## Places and launching

In **Places**, search by experience name and choose **Use place** to fill the launch target automatically, or **Save** to keep it in your library. Results include thumbnails and player counts, with More results for pagination. Add by ID remains available. Public server selection is available for saved places. Search requests use Roblox's public API without account cookies.

Select accounts and launch the chosen experience. Follow mode has a separate user ID; private-server mode requires a link code. Batch launches can be cancelled. Orbit verifies saved sessions before requesting a launch ticket and before stopping any previous tracked process.

For multiple clients, close existing Roblox clients and other instance managers, enable **Settings → Multiple Roblox instances**, save, and confirm **Multi-instance ready** before launching. Keep Orbit open while playing. The guard holds both singleton names and reports conflicts instead of silently claiming readiness. It never closes foreign process handles or patches Roblox.

Only an exact tracked Roblox PID, process start time and process name can be stopped or have its window moved. Process handles are pinned and checked immediately before acting. Orbit does not guess which client to stop. Unknown launches are marked unverified and do not trigger extra automatic launches.

Automatic relaunches run independently per account, with up to three attempts and exponential delays. A client that runs for two minutes resets its retry budget. Repeated failures show **Relaunch gave up**; manually launching resets the budget. Removing an account, disabling auto-relaunch or stopping it cancels pending retries.

## Vault, recovery and backups

The vault is `%LOCALAPPDATA%\Orbit\vault.dat`, protected by Windows DPAPI for the current user. Separate WebView profiles also live under that local folder. No accounts are read from other applications automatically. Activity retains the latest 200 entries, redacts known credentials, and saves after a 1.5-second debounce with a final shutdown flush.

Each vault save retains a previous `vault.dat.backup`. If the primary vault cannot be opened, the recovery screen offers restoring that backup. Recovery validates it before replacing anything and preserves a copy of the unreadable original. DPAPI vaults require the original Windows account. A lost backup password cannot be recovered.

**Settings → Set up daily backups** is opt-in. Choose a folder and a password of at least 12 characters. Orbit creates encrypted exports daily while open, catching up on its next startup when due, and keeps the latest seven exports belonging to that configured schedule. The schedule password is DPAPI-protected locally and is excluded from the account vault and its portable exports. Back up now and Disable schedule are also available. Keep your own copy of the password for recovery elsewhere.

Portable exports use PBKDF2-SHA256 (600,000 iterations) and AES-256-GCM. They contain the database snapshot, including games and settings. **The ordinary Import dialog merges accounts and leaves games and settings unchanged.** For original RAM files it also offers a native review of any saved passwords, importing selected entries into the separate encrypted local password manager. Existing passwords are not overwritten; distinct variants remain selectable. Account records without a saved password remain session-only. Re-import the original RAM file to recover passwords from accounts already in Orbit; the completion dialog reports account and password counts separately. Orbit account backups do not include the separate password store: use an encrypted password export or the paired daily backups for those logins. This limitation is shown before choosing the file. Existing old JSON/DPAPI/password-encrypted AccountData formats remain supported. Files needing the original Windows account must be imported there. Removing an account from the vault does not remove its browser profile or historical backups.

## Local control API

Enable Local controls in Settings. The listener binds to `127.0.0.1` only, default port 7963. Every HTTP request and Nexus handshake requires `Authorization: Bearer <access token>`. Requests must use `Host: 127.0.0.1:<port>`. Browser Origin headers are rejected. No cookie-export endpoint exists.

Use **Regenerate token** to invalidate the previous token and disconnect existing Nexus clients. Copy the replacement from Settings. A listener failure is logged and reflected in its status; save local control settings to restart it. Bad individual requests do not stop the listener.

| Endpoint | Request |
| --- | --- |
| `GET /v1/state` | Account metadata, settings and activity without credentials. |
| `POST /v1/refresh` | `{"ids":["account-guid"]}` |
| `POST /v1/launch` (game) | `{"ids":["account-guid"],"placeId":189707,"mode":"game"}` |
| `POST /v1/launch` (follow) | `{"ids":["account-guid"],"userId":123,"mode":"follow"}` |
| `POST /v1/launch` (private) | `{"ids":["account-guid"],"placeId":189707,"jobId":"link-code","mode":"private"}` |
| `POST /v1/stop` | `{"ids":["account-guid"]}` |
| `ws://127.0.0.1:7963/Nexus?accountId=<guid>` | Authenticated WebSocket connection. |

Follow requests must omit placeId (or set it to zero); game/private requests must omit userId. Nexus supports ping/pong, log messages and user-entered text commands to connected clients. It does not inject a client or implement the old dynamic-widget/auto-execute protocol.

## Build and tests

Use the .NET 8 SDK on Windows:

```powershell
dotnet build Orbit.csproj -c Release
dotnet test Orbit.Tests/Orbit.Tests.csproj -c Release
dotnet publish Orbit.csproj -c Release -r win-x64 --self-contained false -p:DebugType=None -p:DebugSymbols=false -o publish
```

`Orbit.Tests` contains xUnit regressions, fixtures and the WebView harness; none ship in the executable. Native UI checks are opt-in with `ORBIT_UI_TESTS=1`. Public search integration checks use `ORBIT_SEARCH_TEST=1`. Live launch tests additionally require `ORBIT_LIVE_TEST=1`, `ORBIT_LIVE_ACCOUNTS` with two comma-separated account names, and `ORBIT_LIVE_PLACE` with the authorized target. They use only those selected local sessions, verify both clients, and close only their verified test processes. Live credentials are never used in CI. Screenshots and test results stay in ignored test output directories, outside the source tree.

GitHub Actions runs the offline xUnit suite and a Windows release publish. The application version is read from its assembly. Optional `OrbitBuildRoot` redirects build output under a separate directory per project.

## Architecture and limits

`AppService` dispatches commands and owns application state. `LaunchService` manages launch queues and bounded retries; `ProcessTracker` verifies process ownership; `AccountTools` wraps account operations; `ActivityStore` debounces persistence; `ScheduledBackups` manages opt-in exports. `CredentialVault` protects local logins, `CredentialCsv` validates imports, and native password-manager dialogs keep secrets out of the WebView dashboard. `ControlServer` marshals work through the UI dispatcher. Roblox browser windows have no native message bridge; the dashboard bridge admits only its exact local origin.

A live two-client launch/process observation passed in place 189707 after the safety changes. This verifies concurrent client survival, not every game mode or a permanent guarantee of Roblox compatibility. See `verification/NOTES.md` for evidence and scope. Account mutation tools, private/follow launches and every historical manager feature have not all been live-tested. Avatar editing, bulk password login, updater, legacy UWP paths, client-settings/FPS patching and full Nexus UI parity remain outside this release.

Dependencies: [WebView2](https://www.nuget.org/packages/Microsoft.Web.WebView2/1.0.4191.47), [ProtectedData](https://www.nuget.org/packages/System.Security.Cryptography.ProtectedData/8.0.0), [libsodium](https://www.nuget.org/packages/libsodium/1.0.22). References: [WebView2 security guidance](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/security), [Roblox public search endpoint announcement](https://devforum.roblox.com/t/official-list-of-deprecated-web-endpoints/62889/59).
