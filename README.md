# pbw

pbw is a Windows-native C#/.NET desktop automation toolkit with a JSON CLI and MCP stdio server.

## Build

```powershell
dotnet restore
dotnet build --configuration Release
dotnet test --configuration Release
```

## Install

Download `pbw_<version>_windows_x64.zip` from GitHub Releases, extract it, and place `pbw.exe` on `PATH`.

Scoop can install the release ZIP directly from this repository manifest:

```powershell
scoop install https://raw.githubusercontent.com/mkusaka/pbw/main/scoop/pbw.json
```

## CLI

Run from source:

```powershell
dotnet run --project src/Pbw.Cli -- --help
dotnet run --project src/Pbw.Cli -- doctor
dotnet run --project src/Pbw.Cli -- see
```

All commands return structured JSON envelopes with `schemaVersion`.
