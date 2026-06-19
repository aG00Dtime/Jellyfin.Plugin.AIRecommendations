# Development

## Build

```powershell
.\build.ps1
```

Linux/macOS:

```sh
./build.sh
```

Output is in `bin/Release/net9.0/`.

## Releases

Every push to `main` runs the pre-push hook, which bumps the patch version, commits it, and pushes a `v1.0.x` tag. The tag triggers the GitHub Actions release workflow, which:

1. Builds the release ZIP on a clean Ubuntu runner.
2. Computes the MD5 checksum.
3. Creates a GitHub Release with the ZIP attached.
4. Updates `manifest.json` on `main` with the real checksum.

## Git hooks

Install with:

```powershell
.\scripts\setup-hooks.ps1
```

- **pre-push**: bumps patch version in `VERSION.txt` and `.csproj`, adds a manifest entry, commits, and pushes the version tag. CI does the actual build and release.
- **pre-commit**: checks that `VERSION.txt`, `AssemblyVersion`, and `manifest.json` all have the same version number.

Skip the version bump for one push:

```powershell
$env:SKIP_VERSION_BUMP=1; git push
```

Manual version bump:

```powershell
.\scripts\bump-version.ps1 -Part minor
```
