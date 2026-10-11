# Self-update, installer, web

Detail notes moved out of `CLAUDE.md` (2026-10-11, size limit). Section names referenced elsewhere ("see 'X'") are listed in `CLAUDE.md`'s index.

## Self-update (player package) - 2026-10-01

User's request: publish new versions for other players; the map checks at startup and can
download them, **without touching players' markers or settings**. The user wants the source
public (so players can see the map doesn't spy on them), so packages live on **GitHub Releases
of `Bengur2/new-uoam`**, not on the relay VM (the user worried downloads would slow the relay;
the VM never serves them). The step-by-step for releasing, the signing key and the settings
compatibility rules are in **`docs/RELEASE.md`** (Czech, user-facing). Read it before releasing
or before changing `AppSettings`.
- **Package.** `tools/Release/release.ps1` publishes `NewUOAM.App` self-contained win-x64 (user's
  pick: players install nothing; zip ~67 MB, 463 files, `SatelliteResourceLanguages=cs`, no pdb).
  `NewUOAM.UoaBridge` is replaced by a **single-file, trimmed, self-contained exe** (~10 MB). The
  bridge runs from its own copy in `%LocalAppData%` and players have no .NET, so it can't use the
  app's runtime. `UoAssistCommands.PrepareBridgeCopy` hashes the dll when there is one (dev build)
  or the exe (package). Verified: the packaged bridge runs with a bogus `DOTNET_ROOT` (started,
  "client exited" in `bridge.log`). `tools/NewUOAM.ReleaseTool pack` writes `NewUOAM.files` (every
  file of the package), zips the folder as `NewUOAM\…` and writes the signed `update.json`.
- **Version** = `<Version>` in `NewUOAM.App.csproj` (`App.CurrentVersion`, normalized to
  major.minor.build; shown as a disabled "Verze x.y.z" item in the Mapa menu and in the main
  window's title, "new UOAM x.y.z", plus " (vývoj)" for a build without `NewUOAM.files`). Tag `v<version>`.
- **Feed.** `UpdateFeed.DefaultFeedUrl` = `…/releases/latest/download/update.json`. GitHub
  redirects it to the newest non-draft, non-prerelease release; no API, no rate limit. It only
  works once the repo is public. `update.json` = `{manifest: base64(JSON), signature: base64}`,
  ECDSA P-256/SHA-256 over the exact manifest bytes. The manifest holds version, notes,
  package URL, SHA-256 and size. The app trusts only `UpdateFeed.PublicKey`. The private key is
  `update-signing.key` in the project root (gitignored via `*.key`; must be backed up like the SSH
  key). From the default feed, the package URL must start with `UpdateFeed.PackageUrlPrefix`.
  Download size and SHA-256 are checked against the signed manifest. Only a version greater than
  the installed one is offered, so an old signed feed can't downgrade anyone.
- **Install (`PackageInstaller.Apply`), no separate updater.** Windows allows *renaming* a running
  exe and its loaded DLLs. Tested on a throwaway self-contained WPF app: all 243 files, 48 of them
  loaded modules, renamed while it ran. So each listed file is renamed to `*.old-update` and the
  new one copied in. Files the old `NewUOAM.files` had and the new one doesn't are moved aside too.
  Any exception rolls back every step. A write probe first turns "no write access" (Program Files
  without admin) into a clear message. Only listed files are touched; settings, markers and caches
  are in `%LocalAppData%`/player folders, never in the app folder.
- **Flow.** `MainWindow.Updates.cs` `CheckForUpdatesAsync`: at startup in `Loaded` (after the
  map/markers load, before auto-connect; failures silent) and from Mapa > Zkontrolovat
  aktualizace (failures in a MessageBox). A build without `NewUOAM.files` (dev build from `bin\`)
  never checks. `UpdateWindow` (version, notes, "markery i nastavení zůstanou", progress,
  Aktualizovat / Později → Zrušit during the download) downloads to
  `%LocalAppData%\NewUOAM\updates\<version>-<pid>`, stages and applies, then sets
  `App.RestartExePath`. The map `Close()`s (the normal save-settings + relay bye path) and
  `App.OnExit` starts the new exe with the same args. If the app isn't elevated, `--no-elevate` is
  added so a declined UAC isn't asked again. The new instance deletes `*.old-update` on a
  background task with retries (0/3/10/30 s), because the old process is still exiting. Leftover
  download dirs older than 1 h are deleted too.
- **Installer + web (2026-10-01, user's request).** `release.ps1` also builds `NewUOAM-Setup.exe`
  from `installer/NewUOAM.iss` (Inno Setup 6, installed per-user via winget; Czech UI from its
  `Czech.isl`) out of the same folder as the zip, `NewUOAM.files` included. Per-user install to
  `%LocalAppData%\Programs\NewUOAM`, `PrivilegesRequired=lowest` (the self-update must be able to
  write there), Start-menu shortcut + optional desktop one, `CloseApplications`, post-install launch
  via `shellexec` (so the app's own UAC relaunch works). **Uninstall deletes only the package's
  files**: the `[Code]` section reads `{app}\NewUOAM.files` (kept current by self-updates, so it
  also covers files a later version added) and deletes each listed file plus its `.old-update`.
  The 1.0.2 installer as first built wiped the whole `{app}` folder (`[UninstallDelete]
  filesandordirs`). The user asked whether uninstall could delete markers, and a player keeping
  marker files in the app folder would have lost them, so it was changed the same day. Never
  `%LocalAppData%\NewUOAM`. The asset name is version-less, so
  `releases/latest/download/NewUOAM-Setup.exe` is a stable link.
  Verified: silent install into a scratch dir (465 files, Start-menu entry, Apps entry "new UOAM
  1.0.1"), then silent uninstall (shortcut and Apps entry gone, settings untouched). For the
  package-files-only uninstall: planted `DP Mesta.map`, `moje markery\stara uoam.map` and
  `Assets\MapIcons\MOJE.png` survived. All 465 package files, two `.old-update` backups and a file
  "added by an update" (appended to `NewUOAM.files`) were removed. App icon `src/NewUOAM.App/Assets/app.ico` (`ApplicationIcon`; also
  `docs/img/icon.png`) is drawn by `tools/Release/make-icon.ps1`. Main window title shows the version.
  The web is `docs/index.html` (single file, Czech: features, install, first run, controls,
  in-game commands, privacy, FAQ), served by GitHub Pages from `main` `/docs` (`docs/.nojekyll`,
  so files are served as-is) at https://bengur2.github.io/new-uoam/. No Actions workflow: the gh
  token has no `workflow` scope, and GitHub refused a push containing one. The download button links to
  the stable installer URL, and JS shows version/date/size from the GitHub API (works without JS).
  `docs/img/map.png` is rendered from the DP client data with the app's Rotated45 math + DP
  marker icons, no player data. Checked in headless Edge at 1280px and in a 390px iframe (headless
  Edge's own minimum window width makes a direct 390px screenshot misleading).
- **Testing:** `--update-feed <url>` (App arg) overrides the feed (then any package URL is
  allowed, the signature is still required). `release.ps1 -PackageBaseUrl http://localhost:27999/`
  builds a package for it.
- **Verified 2026-10-01:**
  - Scratch unit test (21 checks): valid/foreign-key/tampered/garbage/wrong-product feeds, the
    embedded key rejecting a test-signed feed, version compare, rollback leaving the folder
    byte-identical when a file is locked, replace/add/remove-dropped, a player's own file kept,
    held backups surviving cleanup, update with a held stale backup, `..` in the list rejected.
  - E2E with the real packages (scratch install of 1.0.0, 1.0.1 built with a temporary version
    bump, served by a PowerShell `HttpListener` on localhost, UI Automation clicking Aktualizovat).
    The dialog showed (screenshot), the 1.0.0 process exited and 1.0.1 started from the same
    folder. No `*.old-update` was left after 15 s, the file list matched 1.0.1, `mine.txt` stayed,
    `settings.json` was unchanged, and the restarted 1.0.1 fetched the feed again without offering
    anything.
  - v1.0.1 released on GitHub 2026-10-01: the live `latest/download/update.json` verified (signature
    OK, zip matches). **Not yet:** the user seeing the dialog in their installed 1.0.0, and the update of
    an *elevated* instance (the E2E used `--no-elevate`).

