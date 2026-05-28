# pbw Progress

## Checkpoints

- [x] 1. Solution/project skeleton and build pipeline
- [x] 2. Core models, errors, config, snapshot store
- [x] 3. CLI framework and command parsing
- [x] 4. Windows window/app/clipboard services
- [x] 5. capture + see vertical slice
- [x] 6. input/action routing vertical slice
- [x] 7. remaining CLI commands
- [x] 8. MCP stdio server and tool mapping
- [x] 9. tests and diagnostics
- [x] 10. docs and final self-review

## Notes

- The repository did not include `docs/pbw-stable-spec.md`; it was created from the task prompt before implementation.
- A repo-local `NuGet.config` clears inherited user feeds and uses `nuget.org`.
- The host initially had no .NET SDK installed. Validation was run with a local .NET 8 SDK installed under the user temp directory for this session.
- UI Automation tree reading and common pattern execution are implemented through real Windows UI Automation APIs. Integration tests launch a real WPF process and exercise ValuePattern and InvokePattern.
- Window capture is implemented through Windows.Graphics.Capture using HWND interop, with `PrintWindow` and `BitBlt` fallback paths.
- Desktop capture is implemented through Windows.Graphics.Capture using monitor interop, with `BitBlt` fallback. Annotated BMP snapshots are written for successful captures.
- OCR is implemented through Windows.Media.Ocr. If the Windows OCR engine is unavailable for the current user languages, the service returns an empty result and doctor reports a warning.
- Clipboard uses Win32 APIs with an in-process fallback for locked/non-interactive clipboard sessions.
- MCP is stdio-only and does not expose shell execution or a remote listener.
- MCP tools now expose command-specific JSON schemas and reject additional properties at the schema level.
- Snapshot redaction is implemented for configured text patterns in element names and OCR text.

## Validation Results

- `dotnet restore`: passed
- `dotnet build --configuration Release`: passed with 0 warnings and 0 errors
- `dotnet test --configuration Release`: passed, 67 passed, 0 failed, 0 skipped
- CLI help: passed, returned a structured JSON envelope
- `pbw doctor`: passed, returned structured JSON checks
- MCP tools/list smoke: passed through stdio JSON-RPC and is covered by tests
- `pbw see`: passed, created a JSON snapshot and annotated BMP image using Windows.Graphics.Capture monitor capture; OCR returned text on this host
- Windows.Graphics.Capture integration: passed against a real WPF window
- Windows OCR integration: passed against a controlled BMP with rendered text
- Formatter/analyzer setup: added `.editorconfig`, repo-wide .NET analyzers, and CI formatting verification
- GitHub Actions CI: added Windows workflow using `actions/checkout@v6` and `actions/setup-dotnet@v5`, with restore, format verify, Release build, and Release tests
- `dotnet format --verify-no-changes --verbosity minimal`: passed
- GitHub Actions workflow linting: CI now runs actionlint, pinact, and ghalint from pinned GitHub Releases binaries with checksum verification, plus the official zizmor action; all workflow `uses:` refs are pinned to full commit SHAs with version comments
- `actionlint`: passed
- `pinact run --check`: passed
- `ghalint run`: passed
- `zizmor --format=plain .`: passed, no findings reported
- Release packaging: added a tag-triggered GitHub Release workflow and `scripts/package.ps1` for Windows x64 self-contained ZIPs
- Scoop support: added `scoop/pbw.json` for installing the GitHub Release ZIP directly
- `scripts/package.ps1 -Version 0.1.0`: passed, produced `pbw_0.1.0_windows_x64.zip`
- Release ZIP smoke test: passed, extracted `pbw.exe --help` returned structured JSON
- Capture quality diagnostics: capture results now carry additive `captureDetails` metadata with fallback attempts, BMP quality/mostly-black classification, DWM extended-frame bounds where available, desktop-crop occlusion checks, and minimized/no-pixels unavailable results.
- Window capture now uses `DwmGetWindowAttribute(DWMWA_EXTENDED_FRAME_BOUNDS)` for visual capture/crop bounds when available, falling back to Win32 window rectangles.
- Mostly-black BMP detection is intentionally strict so all-zero DirectComposition-style failures are degraded without flagging merely dark nonblack UIs.
- Real-machine capture validation uses the existing WPF TestHost; arbitrary z-order occlusion is not exercised as an e2e test because it would be flaky, so occlusion is reported through the guarded desktop-crop branch and covered by lower-level metadata/attempt tests.
- Input dispatch policy is implemented for click/type/press/hotkey/scroll/drag/move with `auto`, `background`, and `foreground` modes exposed through CLI and MCP schemas.
- UI Automation semantic click actions remain preferred for element targets. Background Win32 message dispatch is attempted where feasible; explicit background requests return structured `background_unavailable` instead of silently using foreground/global input.
- Foreground/global dispatch results include additive details for requested dispatch, actual dispatch, target HWND, foreground set/restore attempts, and background fallback diagnostics when auto mode falls back.

## Capture Quality Goal Validation

- PATH `dotnet` on this host still has no SDK; validation used the repo's local .NET 8 SDK at `%TEMP%\dotnet-sdk-local\dotnet.exe`.
- `dotnet restore`: passed
- `dotnet build --configuration Release`: passed with 0 warnings and 0 errors
- `dotnet test --configuration Release`: passed, 74 passed, 0 failed, 0 skipped
- `dotnet format --verify-no-changes --verbosity minimal`: initial run found line-ending normalization only; `dotnet format --verbosity minimal` normalized files, and final verify passed
- `dotnet run --project src/Pbw.Cli -- doctor`: passed, all checks returned `ok`
- `dotnet run --project src/Pbw.Cli -- see`: passed, created a snapshot and BMP image with `captureStatus: ok`, `captureMethod: Windows.Graphics.Capture`, and additive `captureDetails` quality/attempt metadata
- WPF TestHost e2e-style validation: passed through `WindowsCaptureService`, asserted capture metadata, and verified minimized-window capture returns `unavailable` with `minimized` and `noPixels`
- Deterministic metadata coverage now includes desktop-crop occlusion `unavailable` reporting and invalid-window bounds failures carrying `qualityStatus: unavailable`.

## Input Dispatch Goal Validation

- PATH `dotnet` on this host still has no SDK; the literal `dotnet restore` failed with "No .NET SDKs were found", so validation used `%TEMP%\dotnet-sdk-local\dotnet.exe` version 8.0.421.
- `dotnet restore`: passed with the local SDK
- `dotnet build --configuration Release`: passed with 0 warnings and 0 errors
- `dotnet test --configuration Release`: passed, 95 passed, 0 failed, 0 skipped
- `dotnet format --verify-no-changes --verbosity minimal`: initial run found line-ending normalization only; `dotnet format --verbosity minimal` normalized C# files, and final verify passed
- `dotnet run --project src/Pbw.Cli -- doctor`: passed, all checks returned `ok`
- `dotnet run --project src/Pbw.Cli -- click --help`: passed, returned structured JSON with `--dispatch auto|background|foreground`
- `dotnet run --project src/Pbw.Cli -- type --help`: passed, returned structured JSON with `--dispatch auto|background|foreground`
- WPF TestHost e2e-style validation: passed through the guarded Windows integration test, including real `WindowsInputService` returning `background_unavailable` for WPF background drag instead of sending foreground/global input.

## UIA Robustness Goal Validation

- PATH `dotnet` on this host still has no SDK; the literal `dotnet restore` failed with "No .NET SDKs were found", so validation used `%TEMP%\dotnet-sdk-local\dotnet.exe` version 8.0.421.
- `dotnet restore`: passed with the local SDK
- `dotnet build --configuration Release`: passed with 0 warnings and 0 errors
- `dotnet test --configuration Release`: passed, 98 passed, 0 failed, 0 skipped
- `dotnet format --verify-no-changes --verbosity minimal`: initial run found line-ending normalization only; `dotnet format --verbosity minimal` normalized C# files, and final verify passed
- `dotnet run --project src/Pbw.Cli -- doctor`: passed, all checks returned `ok`
- `dotnet run --project src/Pbw.Cli -- see`: passed, created snapshot `snapshot-1779990173471` with `captureStatus: ok`, `captureMethod: Windows.Graphics.Capture`, `qualityStatus: ok`, and `ocrStatus: ok`
- WPF TestHost e2e-style validation: passed through the guarded Windows integration test, including real `WindowsElementAutomationService` detecting `RangeValue`, setting `RangeSlider` to 73 through `RangeValuePattern`, and returning structured invalid/out-of-range details.
- UIA timeout/degraded behavior is covered by deterministic unit seams rather than a deliberately hung real provider.
- Known limitation: a timed-out provider call returns a degraded placeholder to the caller, but the underlying UIA call may continue on the worker thread until Windows/UIA returns.

## Semantic Action Coverage Goal Validation

- PATH `dotnet` on this host still has no SDK; the literal `dotnet restore` failed with "No .NET SDKs were found", so validation used `%TEMP%\dotnet-sdk-local\dotnet.exe` version 8.0.421.
- `dotnet restore`: passed with the local SDK
- `dotnet build --configuration Release`: passed with 0 warnings and 0 errors
- `dotnet test --configuration Release`: passed, 102 passed, 0 failed, 0 skipped
- `dotnet format --verify-no-changes --verbosity minimal`: initial run found line-ending normalization only; `dotnet format --verbosity minimal` normalized C# files, and final verify passed
- `dotnet run --project src/Pbw.Cli -- doctor`: passed, all checks returned `ok`
- `dotnet run --project src/Pbw.Cli -- see`: passed, created snapshot `snapshot-1779990995663` with `captureStatus: ok`, `captureMethod: Windows.Graphics.Capture`, `qualityStatus: ok`, and `ocrStatus: ok`
- WPF TestHost e2e-style validation: passed through the guarded Windows integration test, including real `WindowsElementAutomationService` and `ActionRouter` routes for button InvokePattern, checkbox TogglePattern, textbox ValuePattern, slider RangeValuePattern, and coordinate hit-test semantic routing.
- Deterministic seam coverage verifies coordinate clicks try semantic routing before input dispatch and report fallback reasons when semantic routing is unavailable.
- Known limitation: coordinate hit-test semantic routing depends on the current topmost UIA element at the point; when a different window owns the point, pbw falls back to input dispatch with structured fallback details rather than forcing a semantic action.

## MSAA Fallback Goal Validation

- PATH `dotnet` on this host still has no SDK; the literal `dotnet restore` failed with "No .NET SDKs were found", so validation used `%TEMP%\dotnet-sdk-local\dotnet.exe` version 8.0.421.
- `dotnet restore`: passed with the local SDK
- `dotnet build --configuration Release`: passed with 0 warnings and 0 errors
- `dotnet test --configuration Release`: passed, 112 passed, 0 failed, 0 skipped
- `dotnet format --verify-no-changes --verbosity minimal`: initial run found line-ending normalization and one whitespace formatting change; `dotnet format --verbosity minimal` normalized files, and final verify passed
- `dotnet run --project src/Pbw.Cli -- doctor`: passed, all checks returned `ok`
- `dotnet run --project src/Pbw.Cli -- see`: passed, created snapshot `snapshot-1779992317879` with `captureStatus: ok`, `captureMethod: Windows.Graphics.Capture`, `qualityStatus: ok`, and `ocrStatus: ok`
- UIA remains the primary accessibility path. MSAA is implemented as a bounded additive fallback for empty, degraded, wrapper-only, or known legacy UI trees, and for semantic action lookup after UIA cannot perform the action.
- MSAA elements use the existing `ElementSnapshot` shape and carry additive metadata such as `source: msaa`, `msaaRole`, `msaaState`, `msaaDefaultAction`, and `windowHandle`.
- MSAA traversal and action dispatch are bounded by depth, child count, total element count, and a fallback timeout. Provider exceptions and empty MSAA results return degraded metadata instead of raw exceptions.
- Deterministic unit coverage verifies role/state mapping, source metadata, UIA-ok/no-MSAA fallback decisions, UIA empty/degraded/wrapper-only fallback decisions, known-legacy append behavior, MSAA action success details, and MSAA unavailable details.
- Guarded WPF TestHost validation exercises the real `WindowsMsaaAutomationAdapter` against a repo-controlled process. This validates real oleacc/IAccessible tree access and source metadata, while UIA remains the primary route for WPF semantic actions.
- Known limitation: the repo does not currently contain a deterministic legacy MSAA-only control whose `accDoDefaultAction` behavior is independent of the UIA provider, so MSAA action routing is covered through seam tests rather than a legacy-app e2e action target.
