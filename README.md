# pbw

pbw is a Windows-native C#/.NET desktop automation toolkit with a JSON CLI and MCP stdio server.

## Build

```powershell
dotnet restore
dotnet build --configuration Release
dotnet test --configuration Release
```

## CLI

Run from source:

```powershell
dotnet run --project src/Pbw.Cli -- --help
dotnet run --project src/Pbw.Cli -- doctor
dotnet run --project src/Pbw.Cli -- see
```

All commands return structured JSON envelopes with `schemaVersion`.
