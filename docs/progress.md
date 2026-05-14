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
