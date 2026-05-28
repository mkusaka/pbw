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

## Input Dispatch Policy

Input commands (`click`, `type`, `press`, `hotkey`, `scroll`, `drag`, and `move`)
accept an optional `dispatch` mode through both CLI (`--dispatch`) and MCP tool
schemas. Supported values are:

- `auto`: the default. Semantic UI Automation actions are preferred for element
  targets. Win32 background message dispatch is attempted where it is likely to
  work, with foreground/global fallback only where the command path allows the
  existing behavior.
- `background`: never steals foreground. If pbw cannot safely resolve or deliver
  background input, the command fails with structured error code
  `background_unavailable`.
- `foreground`: explicitly allows foreground/global input. Result details report
  the requested dispatch, actual dispatch, target HWND where known, whether
  foreground changed, whether restore was attempted, and whether restore
  succeeded.

Background fallback diagnostics are additive `details` fields and do not change
the `pbw.stable.v1` envelope. Consumers must ignore unknown action result
details. Known dispatch detail keys include `dispatch`, `actualDispatch`,
`eventKind`, `targetHwnd`, `rootHwnd`, `targetClass`, `foregroundChanged`,
`foregroundRestored`, and optional `backgroundFallback`.

Click and semantic action results may include additive routing diagnostics.
Known semantic detail keys include `semanticPattern`, `semanticAttempted`,
`semanticPerformed`, `semanticMethod`, `fallbackReason`, `finalMethod`, and
optional `preActions`. Consumers must ignore unknown action result details.

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

Capture metadata is additive and must remain compatible with `pbw.stable.v1`.
Known capture keys include `captureMethod`, `captureStatus`, `captureMessage`,
and optional `captureDetails`. `captureStatus` is one of `ok`, `degraded`, or
`unavailable`. `captureDetails` may include fallback `attempts`, BMP `quality`
diagnostics, `qualityStatus`, `captureBounds`, `win32Bounds`,
`dwmExtendedFrameBounds`, `boundsSource`, `occluded`, `occlusionCheck`,
`minimized`, and `noPixels`. Consumers must ignore unknown metadata keys.

Elements include stable IDs, names, roles, bounds, automation IDs, state, supported patterns, children, and optional metadata. Element metadata is additive; consumers must ignore unknown keys. UI Automation degraded placeholder elements may set metadata keys such as `degraded`, `degradationReason`, `message`, and `details`.
MSAA fallback elements are represented with the same `ElementSnapshot` shape and set additive metadata such as `source: "msaa"`, `elementSource: "msaa"`, `msaaRole`, `msaaRoleName`, `msaaState`, `msaaStateNames`, `msaaDefaultAction`, and `windowHandle`. Consumers must treat these keys as optional diagnostics.

## Safety and Configuration

Default behavior is local-only. MCP uses stdio only; remote listeners are out of scope for v1. Configuration controls tool allow/deny lists, destructive-action confirmation, snapshot retention, and redaction settings.

## Windows Behavior

The Windows layer should prefer native APIs in this order:

1. UI Automation patterns for semantic actions.
2. Win32 window/app/clipboard services for shell integration.
3. Background Win32 message dispatch where feasible and safe for input commands.
4. Explicit foreground/global input fallback when requested or required by the command path.
5. Capture through Windows Graphics Capture where feasible, `PrintWindow`, then desktop crop fallback.
6. OCR through Windows OCR where feasible, with a safe degraded no-op fallback.

Unavailable capabilities must be reported through structured degraded results rather than raw exceptions.
UI Automation tree reads use bounded cache requests for serialized element properties and common action patterns, falling back to uncached reads if providers reject cached access. Expensive tree reads are bounded by a timeout and may return a degraded placeholder element instead of blocking the whole snapshot path. Window-scoped UIA lookup may retry from the desktop root filtered by HWND/process relationship when a direct window root does not expose the requested descendants.
UI Automation remains the primary accessibility provider. MSAA is an additive Windows fallback for empty, degraded, wrapper-only, or known legacy provider cases. MSAA traversal is bounded by depth, child, total element, and timeout limits; unavailable MSAA fallback is reported as degraded metadata rather than raw provider exceptions.
Semantic `set-value` supports `ValuePattern` for text-like controls and `RangeValuePattern` for numeric range controls. RangeValue results include additive details such as requested value, current range bounds, and `errorCode` for invalid numeric values, read-only controls, out-of-range values, or provider errors.
Element-targeted clicks and coordinate clicks prefer UI Automation hit-test and semantic click patterns before input dispatch. Semantic click routing tries `InvokePattern`, `TogglePattern`, `SelectionItemPattern`, and `ExpandCollapsePattern`; `ScrollItemPattern` may be used as a pre-action to bring the target into view. Input fallback reports the semantic fallback reason in additive action details.
If UIA semantic action lookup is unavailable, MSAA may perform a safe default action through `accDoDefaultAction` before input dispatch. MSAA action details are additive and may include `elementSource`, `semanticPattern: "MSAA.DefaultAction"`, `uiaFallbackReason`, `msaaFallbackReason`, and `finalMethod`.
Background input that cannot be delivered safely is reported as
`background_unavailable` with a reason and retry hint instead of silently sending
foreground/global input.
Window capture should use DWM extended frame bounds for visual capture/crop
bounds when available, then fall back to Win32 window rectangles. BMP captures
are checked for all-black or mostly-black output before accepting a backend.
Desktop-crop fallback reports occlusion metadata when a safe `WindowFromPoint`
sample can determine that another root window covers the target. Minimized
window capture reports `unavailable` with `minimized` and `noPixels` metadata
instead of returning a misleading image.

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
