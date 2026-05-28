# Goal Prompt: Capture Quality And Diagnostics

```text
/goal Implement robust pbw capture quality diagnostics, including black-image detection, DWM-aware window bounds, occlusion metadata, minimized-window handling, tests, and documentation.

Objective:
Improve pbw's Windows capture layer so successful capture results are trustworthy and degraded capture results explain why image quality may be incomplete. This is a targeted feature implementation, not a rewrite of pbw.

Read first:
1. docs/pbw-stable-spec.md
2. docs/progress.md
3. src/Pbw.Windows/WindowsServices.cs
4. src/Pbw.Core/PbwCore.cs
5. tests/Pbw.Tests/CoreTests.cs
6. CUA reference code from a local ghq checkout. If missing, run: ghq get https://github.com/trycua/cua
7. CUA files:
   - blog/inside-windows-computer-use.md
   - libs/cua-driver/rust/crates/platform-windows/src/capture.rs
   - libs/cua-driver/rust/crates/platform-windows/src/wgc.rs
   - libs/cua-driver/rust/crates/platform-windows/src/tools/impl_.rs

Worker rules:
1. You are not alone in the codebase. Do not revert edits made by others; adapt to existing changes if present.
2. Do not commit. The orchestrator will review, request follow-up fixes if needed, and commit.
3. Keep edits within the ownership scope implied by this goal unless a narrowly-scoped supporting change is required.
4. Return with: files changed, implementation summary, tests/commands run and results, skipped or guarded e2e reasons, and known limitations.

Current pbw context:
- WindowsCaptureService already tries Windows.Graphics.Capture for desktop/window capture, then PrintWindow/BitBlt fallbacks.
- CaptureResult currently reports success, method, image path, and message, but does not classify black frames, occlusion, DWM extended-frame bounds, or minimized/no-pixels cases with enough detail.
- Snapshot metadata already records capture method/status/message, so prefer extending metadata without breaking schemaVersion or existing consumers.

Implementation requirements:
1. Add capture quality metadata:
   - black or mostly-black image detection for BMP outputs.
   - occlusion/covered-by-other-window indication for desktop-crop fallback where it can be determined safely.
   - minimized or no-pixels condition for window capture.
   - DWM extended frame bounds where available, with fallback to existing Win32 bounds.
2. Make capture methods report structured details through CaptureResult or an equivalent pbw model:
   - method used.
   - fallback chain attempts and failure reasons.
   - quality status: ok, degraded, unavailable, or equivalent existing vocabulary.
   - non-breaking metadata keys for snapshots.
3. Use DwmGetWindowAttribute(DWMWA_EXTENDED_FRAME_BOUNDS) for window capture/crop bounds when available.
4. Detect all-black/mostly-black captured BMPs and treat them as degraded or as a reason to try the next fallback when appropriate.
5. Keep capture behavior local-only and Windows-native.
6. Do not introduce a daemon, remote listener, Node.js runtime, or non-.NET required runtime.
7. Keep public JSON compatible with pbw.stable.v1. Add fields only in metadata/details unless a model change is necessary and fully tested.

Tests:
1. Add deterministic unit tests for black/mostly-black BMP detection using generated BMP files.
2. Add unit tests for capture result metadata/fallback reporting.
3. Extend guarded Windows integration tests where feasible:
   - a real WPF window capture still succeeds.
   - minimized-window or no-pixels behavior returns a structured degraded/unavailable result if it can be exercised reliably.
4. Avoid flaky tests based on arbitrary z-order unless they are guarded and deterministic.
5. Match the style of the existing tests in `tests/Pbw.Tests/CoreTests.cs`: guard real Windows checks with `OperatingSystem.IsWindows()` and desktop/session capability checks where needed.
6. Add at least one real-machine e2e-style validation path using the existing WPF TestHost or an equivalent deterministic local window. It should exercise the actual `WindowsCaptureService`, not only fakes.
7. If an e2e scenario is impossible to make deterministic on the current host, keep the test guarded/skippable with an explicit reason and add lower-level unit coverage for the same decision branch.

Validation:
Run these commands and fix failures:

dotnet restore
dotnet build --configuration Release
dotnet test --configuration Release

If formatting/analyzers are configured, also run:

dotnet format --verify-no-changes --verbosity minimal

Also perform local smoke checks on Windows when available:

dotnet run --project src/Pbw.Cli -- doctor
dotnet run --project src/Pbw.Cli -- see

Docs:
1. Update docs/progress.md with the completed capture-quality work and validation results.
2. Update docs/pbw-stable-spec.md only if the stable capture contract needs to document new metadata/degraded behavior.

Stopping condition:
Stop only when the feature is implemented, tests are added or updated, validation passes, docs are updated, and the final diff has been self-reviewed for JSON compatibility, Windows API error handling, and flaky tests.

Blocked behavior:
If a Windows API is unavailable on the current host, implement a guarded fallback, return structured degraded metadata, add tests for the fallback behavior, document the limitation, and continue with the rest of the feature.
```
