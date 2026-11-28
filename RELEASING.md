# Releasing browse

## Maintainer steps

1. Bump `<Version>` in `Search.Windows.csproj` to `X.Y.Z` (for example, `0.2.0`).
2. Commit the version bump on `main`:
   ```powershell
   git add Search.Windows.csproj
   git commit -m "Release vX.Y.Z"
   ```
3. Create a matching tag:
   ```powershell
   git tag vX.Y.Z
   ```
4. Push `main` and the tag:
   ```powershell
   git push origin main vX.Y.Z
   ```

## What the release workflow does

Pushing a `v*.*.*` tag triggers `.github/workflows/release.yml` on `windows-latest`:

1. Verifies the tag matches `vX.Y.Z` and equals `<Version>` in `Search.Windows.csproj` (fails immediately on mismatch).
2. Builds the release output with `dotnet publish Search.Windows.csproj -c Release -warnaserror -o publish`.
3. Runs `python test_smoke.py publish/browse.exe`.
4. Builds the installer `artifacts/browse-X.Y.Z-win-x64-setup.exe` from `packaging/browse.iss` with Inno Setup 6 (installed with Chocolatey if the runner lacks it).
5. Zips the published files (flat archive layout matching `v0.0.1`, excluding `.pdb` and `.xml`) into the portable `browse-X.Y.Z-win-x64.zip`. Keep that name, because the in-app updater downloads exactly this asset.
6. Writes `SHA256SUMS.txt` and creates the GitHub Release `browse X.Y.Z` with the installer, the zip and the sums attached. GitHub automatically computes the asset `digest` that the in-app updater verifies.

CI (`ci.yml`) also compiles the installer on every push and PR, so a broken `browse.iss` shows up before a release.

## Building the installer locally

Install [Inno Setup 6](https://jrsoftware.org/isinfo.php) (`winget install JRSoftware.InnoSetup`), then from the repo root:

```powershell
dotnet publish Search.Windows.csproj -c Release -warnaserror -o publish
& "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe" /DAppVersion=X.Y.Z packaging\browse.iss   # or "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
```

The result is `artifacts\browse-X.Y.Z-win-x64-setup.exe`. Never change `AppId` in `browse.iss`: upgrades and uninstall find the existing install by it.
