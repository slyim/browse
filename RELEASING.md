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
4. Zips the published files (flat archive layout matching `v0.0.1`, excluding `.pdb` and `.xml`) into `browse-X.Y.Z-win-x64.zip` and computes its SHA256.
5. Creates the GitHub Release `browse X.Y.Z` with `browse-X.Y.Z-win-x64.zip` attached and the SHA256 in the release notes. GitHub automatically computes the asset `digest` used by the app's auto-updater.
