# Release

pbw releases are driven by Git tags. The release workflow builds a Windows x64
self-contained ZIP, uploads it to GitHub Releases, computes the ZIP SHA256, and
updates `scoop/pbw.json` on `main`.

## Prerequisites

- `main` is green in GitHub Actions.
- `src/Pbw.Cli/Pbw.Cli.csproj` has the intended version.
- The release tag does not already exist locally or on GitHub.

## Local validation

```powershell
dotnet restore
dotnet format --verify-no-changes --verbosity minimal
dotnet build --configuration Release --no-restore
dotnet test --configuration Release --no-build
.\scripts\package.ps1 -Version 0.1.1
```

Smoke test the generated ZIP:

```powershell
$dest = Join-Path $env:TEMP 'pbw-release-test'
Remove-Item -Recurse -Force $dest -ErrorAction SilentlyContinue
Expand-Archive -Force .\artifacts\pbw_0.1.1_windows_x64.zip $dest
& (Join-Path $dest 'pbw.exe') --help
```

## Create a release

```powershell
git switch main
git pull --ff-only
git tag -a v0.1.1 -m "v0.1.1"
git push origin main
git push origin v0.1.1
```

Watch the release workflow:

```powershell
gh run list --limit 5
gh run watch <release-run-id> --exit-status
```

After the release workflow succeeds, verify:

```powershell
gh release view v0.1.1 --json url,assets
scoop update pbw
pbw --help
```

The release workflow pushes a follow-up commit to `main` if `scoop/pbw.json`
needs a version, URL, or hash update.
