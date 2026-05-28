# Goal Prompt: Session And Interactive Desktop Doctor

```text
/goal Add pbw doctor diagnostics for Session 0, interactive desktop availability, foreground access, UIA availability, integrity level, tests, and documentation.

Objective:
Make `pbw doctor` clearly report whether the current process can actually automate the interactive Windows desktop. This should catch service/SSH/Session 0 and desktop-access problems early, before users see confusing capture or input failures.

Read first:
1. docs/pbw-stable-spec.md
2. docs/progress.md
3. src/Pbw.Windows/WindowsServices.cs
4. src/Pbw.Core/PbwCore.cs
5. src/Pbw.Cli/Program.cs
6. src/Pbw.Mcp/McpServer.cs
7. tests/Pbw.Tests/CoreTests.cs
8. CUA reference code from a local ghq checkout. If missing, run: ghq get https://github.com/trycua/cua
9. CUA files:
   - blog/inside-windows-computer-use.md
   - libs/cua-driver/rust/crates/platform-windows/src/diagnostics.rs
   - libs/cua-driver/rust/crates/cua-driver/src/serve.rs

Worker rules:
1. You are not alone in the codebase. Do not revert edits made by others; adapt to existing changes if present.
2. Do not commit. The orchestrator will review, request follow-up fixes if needed, and commit.
3. Keep edits within the ownership scope implied by this goal unless a narrowly-scoped supporting change is required.
4. Return with: files changed, implementation summary, tests/commands run and results, skipped or guarded e2e reasons, and known limitations.

Scope:
1. Extend WindowsDoctorCheckService with diagnostics for:
   - current process session id.
   - whether the process is in Session 0.
   - whether WinSta0/default interactive desktop can be opened or queried safely.
   - foreground window availability.
   - current integrity level when feasible.
   - UI Automation availability smoke check.
   - capture support status with existing WGC/PrintWindow/BitBlt information preserved.
2. Return structured checks using existing DoctorCheck shape:
   - status `ok`, `warning`, or `error`.
   - clear message.
   - details dictionary with raw IDs/flags where useful.
3. Keep this goal diagnostic-only:
   - do not add a daemon.
   - do not add named pipes.
   - do not add remote listener behavior.
   - do not try to bypass Session 0 from inside doctor.
4. Ensure doctor remains safe to run in CI, non-Windows, locked desktop, or headless environments.
5. Preserve CLI and MCP JSON envelope compatibility.

Tests:
1. Unit tests for doctor result mapping/status decisions through seamable diagnostic providers.
2. CLI test proving `pbw doctor` still returns a structured JSON envelope with the new checks.
3. MCP tool-call test proving `doctor` includes structured check data.
4. Guarded Windows test for current session/desktop diagnostics where feasible.
5. Non-Windows behavior must remain warning/degraded rather than raw exception.
6. Match the style of the existing tests in `tests/Pbw.Tests/CoreTests.cs`: deterministic status assertions, guarded Windows-only checks, and structured JSON output validation.
7. Add a real-machine e2e-style validation path that runs the actual `WindowsDoctorCheckService` on the current Windows host and asserts the new check names/details are present.
8. If the current host cannot expose a diagnostic safely, assert the structured warning/error branch rather than skipping all coverage.

Validation:
Run these commands and fix failures:

dotnet restore
dotnet build --configuration Release
dotnet test --configuration Release

If formatting/analyzers are configured, also run:

dotnet format --verify-no-changes --verbosity minimal

Also perform the real CLI doctor smoke check on Windows when available:

dotnet run --project src/Pbw.Cli -- doctor

Docs:
1. Update docs/progress.md with doctor diagnostic notes and validation results.
2. Update docs/pbw-stable-spec.md so `pbw doctor` explicitly covers Session 0 and interactive desktop availability.
3. Update README.md or skills/pbw/SKILL.md if user-facing doctor guidance changes.

Stopping condition:
Stop only when the new doctor checks are implemented, deterministic tests cover status decisions, validation passes, docs are updated, and the final diff has been self-reviewed for Windows API safety and CI compatibility.

Blocked behavior:
If a diagnostic cannot be safely queried in the current environment, return a structured warning with details, add tests for that branch, document the limitation, and continue.
```
