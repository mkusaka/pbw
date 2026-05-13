# pbw Stable Specification

pbw is a Windows-native C#/.NET desktop automation toolkit inspired by Peekaboo. Version 1 is local-only and exposes the same automation surface through a command-line interface and an MCP stdio server.

## Architecture

- `Pbw.Core` owns stable JSON models, structured errors, configuration, target matching, coordinate conversion, snapshot storage, and service interfaces.
- `Pbw.Windows` owns Windows-specific adapters for Win32 window/app operations, input, clipboard, capture, OCR, UI Automation, and doctor checks.
- `Pbw.Cli` owns command parsing and JSON output.
- `Pbw.Mcp` owns a stdio JSON-RPC MCP server with tools aligned to CLI commands.
- Tests cover core behavior, CLI JSON shape, MCP tool listing/calls, and guarded Windows integration.

## JSON Contract

All CLI and MCP tool payloads use `schemaVersion: "pbw.stable.v1"`. Successful envelopes use:

```json
{"schemaVersion":"pbw.stable.v1","ok":true,"data":{}}
```

Failures use:

```json
{"schemaVersion":"pbw.stable.v1","ok":false,"error":{"code":"...","message":"..."}}
```

## Required CLI Commands

The CLI supports:

- `pbw see`
- `pbw image`
- `pbw click`
- `pbw type`
- `pbw press`
- `pbw hotkey`
- `pbw scroll`
- `pbw drag`
- `pbw move`
- `pbw set-value`
- `pbw perform-action`
- `pbw window list/focus/move/resize/set-bounds/minimize/maximize/restore/close`
- `pbw app list/launch/focus/switch/quit`
- `pbw menu list/click`
- `pbw dialog list/click/input/dismiss`
- `pbw clipboard get/set/clear/paste`
- `pbw snapshot list/show/inspect/clean`
- `pbw config init/show/validate/get/set`
- `pbw doctor`
- `pbw mcp`

Every command returns a structured success envelope or structured error envelope.

## Snapshot Model

Snapshots contain:

- `schemaVersion`
- `id`
- `createdAt`
- `display`
- `windows`
- `elements`
- `ocrText`
- optional `imagePath`
- optional `metadata`

Elements include stable IDs, names, roles, bounds, automation IDs, state, supported patterns, and children.

## Safety and Configuration

Default behavior is local-only. MCP uses stdio only; remote listeners are out of scope for v1. Configuration controls tool allow/deny lists, destructive-action confirmation, snapshot retention, and redaction settings.

## Windows Behavior

The Windows layer should prefer native APIs in this order:

1. UI Automation patterns for semantic actions.
2. Win32 window/app/clipboard services for shell integration.
3. `SendInput`-style input fallback.
4. Capture through Windows Graphics Capture where feasible, `PrintWindow`, then desktop crop fallback.
5. OCR through Windows OCR where feasible, with a safe degraded no-op fallback.

Unavailable capabilities must be reported through structured degraded results rather than raw exceptions.

## MCP Behavior

The MCP server uses stdio JSON-RPC, registers tools aligned to CLI commands, returns structured tool content, and does not expose shell execution or remote daemon behavior.

## Validation

The stable-ready implementation must pass:

```powershell
dotnet restore
dotnet build --configuration Release
dotnet test --configuration Release
```

Windows integration tests must be guarded or skippable when outside Windows or without a desktop session.
