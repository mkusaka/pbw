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

## Windows Support

pbw is currently built as a self-contained Windows x64 desktop app targeting:

```xml
<TargetFramework>net8.0-windows10.0.22621.0</TargetFramework>
```

The tested support target is Windows 11 version 22H2 or later. Windows 10 may work, especially through UI Automation, Win32, PrintWindow, and BitBlt fallbacks, but it is not yet a guaranteed support target. Windows 7, Windows 8, and Windows 8.1 are out of scope.

Server editions are not a primary target. pbw requires an interactive desktop session, so Server Core and non-interactive service sessions are out of scope even when the underlying .NET runtime supports that OS.

The Windows version in the target framework is the Windows API set used at build time, not by itself the runtime minimum. Supporting older Windows versions would require setting `SupportedOSPlatformVersion` / `TargetPlatformMinVersion`, guarding newer APIs such as Windows Graphics Capture at runtime, and adding CI or integration coverage for those OS versions.

Moving from .NET 8 to .NET 10 does not automatically make the Windows target narrower. .NET 10 has its own supported OS matrix, and the practical pbw support boundary would still be determined by the chosen Windows target framework, guarded API usage, and tested operating systems.

## CLI

Run from source:

```powershell
dotnet run --project src/Pbw.Cli -- --help
dotnet run --project src/Pbw.Cli -- doctor
dotnet run --project src/Pbw.Cli -- see
```

All commands return structured JSON envelopes with `schemaVersion`.

Input commands (`click`, `type`, `press`, `hotkey`, `scroll`, `drag`, and `move`)
accept `--dispatch auto|background|foreground`; command-specific help such as
`dotnet run --project src/Pbw.Cli -- click --help` shows the current options.

## Agent Skill

This repository includes a pbw agent skill at `skills/pbw`. The easiest install path is the universal skills installer:

```powershell
npx --yes skills add mkusaka/pbw --skill pbw -y
```

For a global install, add `-g`:

```powershell
npx --yes skills add mkusaka/pbw --skill pbw -g -y
```

To inspect the skills exposed by the repository without installing:

```powershell
npx --yes skills add mkusaka/pbw --list
```

To install manually in an agent project, copy that directory into the client's skill directory, for example:

```powershell
New-Item -ItemType Directory -Force .claude\skills\pbw | Out-Null
Copy-Item -Recurse -Force .\skills\pbw\* .claude\skills\pbw\
```
