---
name: pbw
description: Capture and automate Windows desktop UI with the pbw CLI and MCP stdio server. Use when Codex needs to inspect Windows screens or windows, identify UI elements, click/type/press/hotkey/scroll/drag, manage apps/windows/menus/dialogs/clipboard, work with pbw snapshots, run pbw doctor, or configure pbw automation safely through structured JSON.
---

# pbw

pbw is a Windows-native desktop automation CLI and MCP stdio server inspired by Peekaboo. Prefer it for local Windows UI automation tasks where structured JSON output, snapshot reuse, and native UI Automation/Win32 behavior are useful.

All CLI commands return a JSON envelope with `schemaVersion: "pbw.stable.v1"` and either `ok: true` plus `data`, or `ok: false` plus a structured `error`. Treat `ok: false` as the command result, not as raw process failure.

## Availability

Check pbw before automating:

```powershell
pbw --help
pbw doctor
```

Install from the repository Scoop manifest when `pbw` is missing:

```powershell
scoop install https://raw.githubusercontent.com/mkusaka/pbw/main/scoop/pbw.json
```

If working from the source repository, run:

```powershell
dotnet run --project src/Pbw.Cli -- --help
dotnet run --project src/Pbw.Cli -- doctor
```

## Reliable Flow

Use `see` first to create a snapshot, then target elements by stable IDs or by explicit coordinates:

```powershell
pbw see
pbw click --id B1
pbw type --text "Hello"
pbw press --key tab
```

For workflows involving a specific app or window, enumerate and focus first:

```powershell
pbw app list
pbw window list
pbw app focus --name "Notepad"
pbw see
```

## Command Surface

Inspection and capture:

- `pbw see`: capture desktop/window context, UI Automation tree, OCR text when available, and snapshot metadata.
- `pbw image`: capture an image through Windows Graphics Capture where feasible, then PrintWindow/desktop crop fallbacks.
- `pbw snapshot list|show|inspect|clean`: reuse and manage stored snapshots.
- `pbw doctor`: report OS, current session and Session 0 status, WinSta0/default desktop availability, foreground window availability, UIA, capture, OCR, DPI/display, integrity, snapshot directory, config, and MCP feasibility.

Input and semantic actions:

- `pbw click`: invoke a UIA element or click coordinates.
- `pbw type`: type text through input fallback.
- `pbw press`: press a key.
- `pbw hotkey`: press modifier combinations such as `ctrl+shift+escape`.
- `pbw scroll`: scroll in a direction.
- `pbw drag`: drag from one target or coordinate to another.
- `pbw move`: move the pointer.
- `pbw set-value`: use UIA ValuePattern when available.
- `pbw perform-action`: route semantic UIA patterns such as Invoke, Toggle, Selection, ExpandCollapse, ScrollIntoView, or SetFocus.

Windows shell operations:

- `pbw window list|focus|move|resize|set-bounds|minimize|maximize|restore|close`
- `pbw app list|launch|focus|switch|quit`
- `pbw menu list|click`
- `pbw dialog list|click|input|dismiss`
- `pbw clipboard get|set|clear|paste`
- `pbw config init|show|validate|get|set`
- `pbw mcp`

Run `pbw <command> --help` or `pbw --help` for the exact accepted options in the installed build.

## Targeting Guidance

Prefer targets in this order:

1. Element IDs from the latest `pbw see` snapshot.
2. Window handles after `pbw window list`, or app names after `pbw app list`.
3. Text/name/role matching when exposed by UI Automation.
4. Explicit coordinates only when semantic targeting is unavailable.

After layout changes, run `pbw see` again before using old element IDs or coordinates.

Common target flags are `--id`, `--text`, `--role`, `--automation-id`, `--x`, `--y`, `--hwnd`, and `--index`.

## Safety

pbw is local-only by default and its MCP mode uses stdio only. Do not start or suggest remote listeners.

Before destructive actions such as closing windows, quitting apps, clearing clipboard data, or dismissing dialogs, inspect the current state and make the intended target explicit. Respect configured tool allow/deny rules and destructive-action confirmation settings.

Avoid shell-command execution through pbw. The MCP server intentionally exposes desktop automation tools, not a shell execution surface.

## MCP

Use `pbw mcp` when an MCP client needs the same tool surface over stdio. Tools map to CLI commands and return structured tool results or structured tool errors using the same `pbw.stable.v1` contract.

Smoke-test MCP availability with `pbw doctor`; use MCP only as a local stdio transport.

## Examples

Inspect, click, and type:

```powershell
pbw see
pbw click --id B3
pbw type --text "user@example.com"
pbw press --key tab
pbw type --text "supersecret"
pbw press --key enter
```

Focus a window and set bounds:

```powershell
pbw window list
pbw window focus --hwnd 123456
pbw window set-bounds --hwnd 123456 --x 50 --y 50 --width 1200 --height 800
```

Clipboard workflow:

```powershell
pbw clipboard set --text "Hello from pbw"
pbw clipboard paste
pbw clipboard get
```

Configuration check:

```powershell
pbw config show
pbw config validate
pbw doctor
```
