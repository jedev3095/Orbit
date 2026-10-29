# Current verification (0.3.0)

29 October 2026. Replaced embedded Bitwarden integration with an opt-in local Roblox password manager, separate DPAPI CurrentUser password storage, CSV previews/import, encrypted portable password exports, and saved-login session refresh. No real credential CSV was imported during development. Browser extensions are disabled in the new profiles; old extension profiles were not deleted.

Local validation: 59 xUnit cases passed with native UI enabled; the two live account-launch/public-search cases were skipped. Application dependencies had no reported NuGet vulnerabilities. Nine extension-specific checks were removed with the retired integration; the remaining legacy regression suite continues in the test assembly.

Storage tests cover encrypted current/backup files, purpose-separated DPAPI decryption, corrupted-file preservation and recovery, authenticated portable export/wrong-password/tamper rejection, additive duplicate handling, validation before writes, metadata-only app state and password redaction. CSV tests cover Bitwarden and common aliases, UTF-8 BOM, quoted commas/quotes/multiline fields, unrelated/host-spoofed websites and sanitized malformed-input errors. Daily backup tests cover a separate encrypted password snapshot.

Native tests use synthetic credentials and locally intercepted login/home responses. They exercise filling special-character passwords into controlled inputs, delayed button readiness, a single form submission, session validation for the selected user ID, retained aliases, credential linking and browser close. The initial login navigation is cancelled by an exact test URL gate so test requests cannot escape to the real login page. The actual Roblox login form and live CAPTCHA/2FA were not tested with real passwords. Native manager rendering was checked with a synthetic entry and no password grid column. Original game-launch tests remain scoped to their earlier versions.

# Verification (0.2.3)

29 October 2026. Fixed account-table scrolling jumping to the top when background state notifications rebuilt the dashboard. Render now captures and restores table, sidebar and page scroll offsets after restoring focus, using preventScroll when focusing inputs. Table position resets when the user changes the account search, group or sorting; sidebar position remains stable within the same page.

Native WebView regression coverage uses 127 synthetic accounts and enough groups to scroll both panes. It verifies exact table/sidebar positions across three native state notifications, preserves table position when selecting an account, and verifies an intentional table reset on search changes without resetting the sidebar. The full local test run passed 33 xUnit cases, including the existing 42-condition UI suite at normal and narrow widths. Live launch and public search integration tests were skipped because this change is limited to dashboard rendering.

# Verification (0.2.2)

The embedded add-account browser now validates and saves automatically on the signed-in Roblox home page, while retaining the manual Use this account fallback. It waits for browser cookies, tries distinct session candidates only for rejected sessions, avoids repeated automatic attempts for unchanged cookies, and cancels saving if the window is closed. Failures are displayed in a larger banner and logged with candidate-session redaction.

All three sign-in entry points share acceptance logic. A validated refreshed entry has its error cleared and status updated instead of being reset to Unchecked. Aliases, groups and notes survive. Acceptance returns the dashboard to Accounts, clears filters and selects the saved account so it cannot remain hidden by the previous view.

Local tests: 32 xUnit cases passed, including the 42-condition UI suite at both widths, successful/refused/cancelled sign-in acceptance, metadata preservation, landing-page checks, and native table visibility with stale filters. An isolated native AccountBrowser test used a synthetic HttpOnly cookie, a locally intercepted home page and mocked identity responses to exercise automatic capture through vault save and browser close. It did not use a real password or sign in to a live account. The two opt-in live launch/public search cases were skipped for this fix; previous scoped results remain below.

# Verification (0.2.1)

Fixed the Places search input being restored to its previous submitted query during background state refreshes. The input now maintains a separate draft on each input event. Search completion updates result state without replacing newer typing, and redraws preserve the full selection range.

Native WebView regressions reproduce clearing the field, replacing the query across a native state push, validation of a short draft, and typing while a search response is pending. All 42 UI checks passed at normal and narrow widths. The full local run passed 22 xUnit cases; live account launch and public API integration tests were skipped because this change only affects input state. Their prior results below remain scoped to 0.2.0.

# Verification (0.2.0)

18 October 2026, Windows x64, .NET 8, installed WebView2 runtime.

- Local test run: 23 xUnit cases passed with native UI and public search enabled; the one live-launch case passed in its separate explicitly authorized run. The UI harness checked 38 conditions at both widths. NuGet reported no vulnerable application dependencies, including transitive packages. Release build has no warnings or errors. The production assembly excludes test runner classes, test fixtures, command-line test modes and JS smoke functions. Version metadata comes from the assembly.
- xUnit regressions cover PID reuse/name/start-time rejection, unrelated-process stop prevention, independent relaunch delays and a three-attempt backoff cap, listener survival after bad requests, Host rejection, token invalidation, listener failure state, password-field discard, activity debounce, backup retention/encryption/reload, vault recovery atomicity and place search/pagination. The earlier 72 checks now run inside the test assembly.
- Native WebView checks pass at normal and narrow widths, including selected-account browsers, place search/results and choosing a launch target, token rotation/backup controls and the accounts-only import warning. Screenshots remain in ignored test output, not the repository.
- The public search integration test returned the requested Natural Disaster Survival result with root place 189707. This used no account cookies. Search results and optional thumbnails remain dependent on Roblox's public APIs.
- **Live two-client test passed:** two explicitly selected saved sessions were authenticated before either launch. Both launched distinct RobloxPlayerBeta processes, matched to Orbit's trackers with start-time/name identity checks, and stayed alive concurrently for a 15-second observation. The full test lasted 26 seconds. The guard held both singleton names. Afterward only the two verified test-owned clients were stopped; their temporary encrypted test vault and activity were removed. Account identifiers, cookies, tickets and process command lines were excluded from this record. Process survival does not independently prove in-game state or private/follow mode correctness.

# Historical verification (0.1.x)


18 October 2026, Windows x64, .NET SDK 8.0.422, WebView2 Runtime 155.0.4283.60.

- Release build: zero warnings and zero errors.
- Packaged executable: 72 offline backend checks passed. Includes DPAPI vault reload, backup authentication/tamper rejection, import atomicity for invalid records, all three legacy import formats, exact bridge-origin matching, sanitized state, HTTP token enforcement, browser-origin rejection, and authenticated Nexus ping. Launch regressions additionally cover HTTP 401 classification, aborting before ticket/client launch, persisted activity, credential redaction, sign-in recovery, and a successful mocked CSRF/ticket exchange. The ticket test responds with HTTP 415 unless both the initial POST and CSRF retry have `application/json` content type and an empty JSON object body.
- Relay-style native WebView2: 32 populated-workspace UI checks passed at a 1064 × 641 content viewport and again at 864 × 521. Covers the reference palette, top tabs, control/table layout, selection, group/search filtering, page rendering and horizontal fit, HTML escaping, a native bridge edit round trip, secret exclusion, and an actionable expired-session dialog.
- Empty native WebView2 workspace: empty-state, sign-in choices, all pages, and small-width layout passed. Results are in `empty/ui-smoke.json`.
- Dependency audit using NuGet's vulnerability feed reported no vulnerable packages, including transitive dependencies, at the time of the check. This does not constitute an independent dependency security audit.

Screenshots are from isolated sample/empty workspaces, not real account data. Tests did not use saved Roblox account files or perform live Roblox API operations, sign-in, game launching, process termination, or account mutations. The legacy password fixture was generated using the exact recovered file header and crypto parameters, with independent libsodium encryption; it is synthetic.

A separate user-authorized launch investigation checked one imported session against Roblox's authenticated-user endpoint. Roblox returned HTTP 401, error code 9002; no game was launched, no token was displayed, and the vault was not modified by that probe. Other imported sessions were not checked. The user's supplied Activity error independently confirmed an expired session.

A subsequent investigation of a different user-identified account confirmed an HTTP 415 defect in version 0.1.1 and earlier: identity validation returned 200, a bodyless ticket POST received a CSRF challenge, and its bodyless retry returned 415. Resending with an empty JSON object and `Content-Type: application/json` returned 200 with a ticket. Version 0.1.2 supplies that JSON body on the initial request and retry. This live probe did not launch a game, reveal credentials/tickets, or modify the vault.

See the README's remaining-parity section before treating this as a full replacement for the original application.

## Relay style reference

The supplied `Relay.exe` release executable is an app host beside `Relay.dll`. The UI was extracted from that assembly's `relay.ui.index.html` resource using ILSpy, and its resource hash matches the REA metadata inventory. The nearby source HTML has a different hash, so styling was based on the embedded release resource.

- Assembly SHA-256: `8073d534a27a55cd0971dbe61f51309a7b636ab84e1e73e1f04d8ac5b36ec197`.
- Embedded HTML SHA-256: `a30c6be37fbf1ee0eac1dd6b1cda0402a0de5c07ba608cbafadd4730b05acd5b`.
- REA assembly Evidence: `ev_44a69fa2506d1b5ea03d8b1c3bd2067fe2b4209f445f5be9be2177c1a6cbaa8b`.

Matched style measurements include the `#181a1d` background, `#202226` header surface, `#292c31` controls, `#36393f` borders, `#91b6ee` accent, 13px Segoe UI typography, 54px tab bar, 270px control pane, 4px button corners, and flat table styling. Orbit retains its own account-related content and controls. Relay's executable, source, and settings were not modified or run.

## Version 0.1.3 browser choices

Both browser choices dispatch through the native bridge in isolated UI tests. The sign-in choice dialog was rendered and visually checked at normal and narrow widths. Quick Login offline tests cover attempt creation, CSRF retry, JSON bodies, UTC interpretation of Roblox's zone-less expiration time, pending approval rejection, session extraction and cancellation. The live unauthenticated probe created a new code, observed Created status and a future expiry, and cancelled the attempt; no existing account, password, browser database or saved session was used. Results: quicklogin-probe.json. Actual browser approval and resulting real-account session exchange still require a user sign-in; their protocol exchange was tested with synthetic responses.

The optional embedded Bitwarden popup was tested with installed extension code in a new isolated profile: extension enabled and its email sign-in form rendered successfully. No real Bitwarden vault storage or login was opened. Results: bitwarden/bitwarden-smoke.json.

Protocol details were checked against Roblox's current shipped CrossDeviceLoginDisplayCode and ReactLogin bundles, and its Quick Login help page: https://en.help.roblox.com/hc/en-us/articles/360056582012-Quick-Login .
## Version 0.1.4 multi-instance fix

A read-only Win32 object probe on the user's running session found ROBLOX_singletonMutex as a Mutex and ROBLOX_singletonEvent as an Event (opening either under the other type returned Win32 error 6). Orbit 0.1.3 held only the older mutex name. No Roblox process or foreign handle was closed during this check.

The replacement guard reserves both names, checks actual ownership, reports conflicts, releases partial activation and holds/releases on the same worker thread. Launches recheck the guard before obtaining any authentication ticket or stopping a tracked process. A multi-account queue cannot run with the setting disabled. Settings reports disabled, ready or the activation error; failed activation preserves the enabled preference for retry.

Ten additional Windows/backend checks use randomized test-only object names: both names held, Event creation prevented, competing manager rejected, cross-thread disposal, clean release without abandonment, re-enable, existing Event detection, partial acquisition rollback, blocked launch before network access and actionable error state. Three added UI checks cover readiness/disabled status and escaped conflict messages. Release tests passed: 72 backend, 29 UI at both widths, 8 empty-workspace checks. A live two-client game launch has not been retested because existing clients need to be closed before the updated guard can take effect.

Implementation context: https://github.com/Snipez-Dev/Claudestrap/blob/main/Bloxstrap/MultiInstance.cs documents both singleton names. Orbit's implementation uses ordinary named-object creation and thread-bound ownership; it does not close foreign process handles or modify the Roblox binary.
## Version 0.1.5 icon and selected-account browser action

Generated the Orbit icon using the built-in image_gen tool. The original PNG retains alpha transparency outside its charcoal tile. assets/generation.txt preserves the prompt and tool provenance. Converted to a seven-size Windows ICO and embedded via ApplicationIcon; each native app window uses the executable icon. The icon extracted from the published EXE was visually checked at 32 pixels (orbit-executable-icon.png).

Added an Open browser button above the accounts table, enabled when exactly one account is selected. The native bridge validates the account ID and reuses the existing per-account window. The established browser path sets that account's saved session in its isolated WebView2 profile and navigates to Roblox home. Three added UI checks cover selection gating and the selected account ID delivered through the native bridge. Release build and all suites passed: 72 backend, 32 populated UI at normal and narrow widths, 8 empty-workspace checks. No live account session was opened during automated verification.
