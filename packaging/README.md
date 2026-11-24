# Packaging `browse`

Staging manifests for Scoop and Windows Package Manager (winget) so users get the .NET 10 Desktop Runtime automatically.

## Publishing

### Scoop

1. **Move `bucket/browse.json` to the repository root** (`<repo-root>/bucket/browse.json`) on the default branch (`main`).
   - Root placement is strongly required in practice: Scoop checks `<repo-root>/bucket/` first, and if that directory is absent it recursively scans the entire repo for `*.json` files.
2. **Verify locally** before pushing:
   ```powershell
   & "$env:USERPROFILE\scoop\apps\scoop\current\bin\checkver.ps1" -Dir bucket -App browse
   ```
3. **Updating for future releases**: run `checkver.ps1 -Dir bucket -App browse -Update` (uses `checkver` + `autoupdate` to bump `version`, `url`, and `hash`) and commit the updated manifest.

**Notes on the .NET 10 runtime dependency in Scoop:**
- `browse.json` depends on `extras/windowsdesktop-runtime-lts` (currently .NET `10.0.15` LTS; note that `extras/windowsdesktop-runtime` is already `.NET 11.0.0`).
- Installing `extras/windowsdesktop-runtime-lts` requires the user to have the `extras` bucket added and to run from an elevated (Administrator) shell (its installer runs system-wide).
- If you want to stay pinned to .NET 10 even after .NET 12 LTS ships (late 2028), switch `depends` to `versions/windowsdesktop-runtime-10.0` (requires `scoop bucket add versions`).
- `extras/webview2` is intentionally not listed as a dependency because it installs a FixedVersionRuntime and overrides the system Evergreen WebView2 globally via `WEBVIEW2_BROWSER_EXECUTABLE_FOLDER`.

### winget (`microsoft/winget-pkgs`)

1. **Validate locally** before submitting:
   ```powershell
   winget validate --manifest .\winget\manifests\s\slyim\browse\0.0.1
   ```
2. **Initial submission** — choose either option:
   - **Via `wingetcreate`** (install with `winget install Microsoft.WingetCreate`):
     ```powershell
     wingetcreate submit .\winget\manifests\s\slyim\browse\0.0.1
     ```
   - **Via GitHub PR**: fork `microsoft/winget-pkgs`, copy `winget/manifests/s/slyim/browse/0.0.1/` to `manifests/s/slyim/browse/0.0.1/` in your fork, and open a PR against `microsoft/winget-pkgs`.
3. **Updating for future releases**:
   ```powershell
   wingetcreate update slyim.browse --version <version> --urls https://github.com/slyim/browse/releases/download/v<version>/browse-<version>-win-x64.zip --submit
   ```

**Note on WebView2:** `PackageDependencies` lists `Microsoft.DotNet.DesktopRuntime.10` only. Microsoft Edge WebView2 Runtime is preinstalled on Windows 11 and most Windows 10 installations (and is available separately on winget as `Microsoft.EdgeWebView2Runtime` if needed).

---

## User-facing install commands (for the main README)

### Install with winget (once merged into `winget-pkgs`)

```powershell
winget install slyim.browse
```

Automatically installs the .NET 10 Desktop Runtime if missing and adds `browse` to your `PATH`.

### Install with Scoop (once `bucket/browse.json` is at repo root)

Run from an elevated (Administrator) PowerShell if the .NET 10 Desktop Runtime is not already installed via Scoop:

```powershell
scoop bucket add extras
scoop bucket add browse https://github.com/slyim/browse
scoop install browse
```

Adds `browse` to your `PATH` and creates a **browse** Start menu shortcut.
