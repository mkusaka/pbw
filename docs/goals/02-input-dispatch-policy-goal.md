# Goal Prompt: Input Dispatch Policy

```text
/goal Implement pbw input dispatch policy with semantic/background-first behavior, explicit foreground fallback, structured background_unavailable errors, tests, and documentation.

Objective:
Make pbw input safer and more predictable by separating semantic UIA actions, background Win32 message dispatch where feasible, and foreground/global SendInput-style fallback. The result should avoid silently stealing focus unless foreground dispatch is explicitly selected or required by the command path.

Read first:
1. docs/pbw-stable-spec.md
2. docs/progress.md
3. src/Pbw.Core/PbwCore.cs
4. src/Pbw.Windows/WindowsServices.cs
5. src/Pbw.Cli/Program.cs
6. src/Pbw.Mcp/McpServer.cs
7. tests/Pbw.Tests/CoreTests.cs
8. CUA reference code from a local ghq checkout. If missing, run: ghq get https://github.com/trycua/cua
9. CUA files:
   - blog/inside-windows-computer-use.md
   - libs/cua-driver/rust/crates/platform-windows/src/input/dispatch.rs
   - libs/cua-driver/rust/crates/platform-windows/src/input/mouse.rs
   - libs/cua-driver/rust/crates/platform-windows/src/input/keyboard.rs
   - libs/cua-driver/rust/crates/platform-windows/src/tools/impl_.rs

Worker rules:
1. You are not alone in the codebase. Do not revert edits made by others; adapt to existing changes if present.
2. Do not commit. The orchestrator will review, request follow-up fixes if needed, and commit.
3. Keep edits within the ownership scope implied by this goal unless a narrowly-scoped supporting change is required.
4. Return with: files changed, implementation summary, tests/commands run and results, skipped or guarded e2e reasons, and known limitations.

Current pbw context:
- ActionRouter already prefers UIA Invoke for click targets with Invoke support.
- WindowsInputService currently uses SetCursorPos, mouse_event, and keybd_event directly.
- CLI and MCP expose click/type/press/hotkey/scroll/drag/move, but there is no explicit dispatch mode or structured foreground-required failure.

Implementation requirements:
1. Introduce an input dispatch model that supports at least:
   - auto/default: semantic/UIA first, background where feasible, foreground fallback only when explicitly safe or requested by current behavior.
   - background: do not steal foreground; if background dispatch is not possible, return a structured failure such as background_unavailable.
   - foreground: allow foreground/global input behavior with explicit details.
2. Preserve current CLI behavior for existing commands unless the user passes a new option or target semantics clearly allow safer behavior.
3. Add CLI options for dispatch where appropriate, for example `--dispatch auto|background|foreground`.
4. Add matching MCP input schema fields for the affected tools without breaking existing callers.
5. Implement background click/key/text paths where feasible using Win32 messages:
   - hit-test or target hwnd resolution before posting mouse messages.
   - WM_CHAR or key message dispatch for text/key paths where safe.
   - return structured failure when the target class/app is known to drop background messages.
6. Keep UIA semantic actions preferred for element targets:
   - Invoke, Toggle, SelectionItem, ExpandCollapse, Value, ScrollItem as currently applicable.
7. Foreground/global fallback must report details:
   - whether foreground was changed.
   - whether it was restored when possible.
   - the actual method used.
8. Do not add remote daemon behavior, shell execution tools, or non-.NET runtime dependencies.

Tests:
1. Unit tests for dispatch mode parsing and defaulting.
2. ActionRouter tests proving semantic UIA actions are still preferred when available.
3. WindowsInputService tests around structured result shape. Use test seams/interfaces for Win32 posting where necessary rather than relying only on global input side effects.
4. CLI JSON shape tests for `--dispatch`.
5. MCP schema/tool-call tests for dispatch fields and structured background_unavailable errors.
6. Guarded Windows integration tests for at least one real UI target if deterministic.
7. Match the style of the existing tests in `tests/Pbw.Tests/CoreTests.cs`: guarded real Windows integration, deterministic WPF TestHost interactions, and structured JSON assertions.
8. Add at least one real-machine e2e-style validation path that launches the existing WPF TestHost or an equivalent deterministic local UI, then exercises the actual CLI or service path for semantic/background/foreground behavior as far as safely possible.
9. If background dispatch cannot be made deterministic for the WPF TestHost, include a guarded test that proves `background_unavailable` is returned instead of silently using global foreground input.

Validation:
Run these commands and fix failures:

dotnet restore
dotnet build --configuration Release
dotnet test --configuration Release

If formatting/analyzers are configured, also run:

dotnet format --verify-no-changes --verbosity minimal

Also perform local smoke checks on Windows when available:

dotnet run --project src/Pbw.Cli -- doctor
dotnet run --project src/Pbw.Cli -- click --help
dotnet run --project src/Pbw.Cli -- type --help

Docs:
1. Update docs/pbw-stable-spec.md to describe dispatch modes and background_unavailable behavior.
2. Update docs/progress.md with implementation notes and validation results.
3. Update README.md or skills/pbw/SKILL.md only if the user-facing command usage changes.

Stopping condition:
Stop only when dispatch modes are implemented through CLI/MCP/core where relevant, tests cover success and failure paths, validation passes, docs are updated, and the final diff has been self-reviewed for safety regressions and JSON compatibility.

Blocked behavior:
If reliable background dispatch cannot be implemented for a class of apps, return structured background_unavailable with a clear reason, add tests for that behavior, document the limitation, and continue with independent dispatch work.
```
