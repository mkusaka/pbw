# Goal Prompt: Semantic Action Coverage

```text
/goal Expand pbw semantic action coverage for Windows controls, including richer UIA pattern routing, hit-test-assisted pixel clicks, tests, and documentation.

Objective:
Improve pbw's action success rate by routing more user actions through semantic Windows accessibility patterns before falling back to coordinate/global input. This goal should build on the UIA robustness work if it has already landed, and avoid duplicating RangeValue work if that feature is already complete.

Read first:
1. docs/pbw-stable-spec.md
2. docs/progress.md
3. docs/goals/03-uia-robustness-goal.md
4. src/Pbw.Core/PbwCore.cs
5. src/Pbw.Windows/WindowsServices.cs
6. src/Pbw.Cli/Program.cs
7. src/Pbw.Mcp/McpServer.cs
8. tests/Pbw.Tests/CoreTests.cs
9. CUA reference code from a local ghq checkout. If missing, run: ghq get https://github.com/trycua/cua
10. CUA files:
    - blog/inside-windows-computer-use.md
    - libs/cua-driver/rust/crates/platform-windows/src/tools/impl_.rs
    - libs/cua-driver/rust/crates/platform-windows/src/uia/mod.rs

Worker rules:
1. You are not alone in the codebase. Do not revert edits made by others; adapt to existing changes if present.
2. Do not commit. The orchestrator will review, request follow-up fixes if needed, and commit.
3. Keep edits within the ownership scope implied by this goal unless a narrowly-scoped supporting change is required.
4. Return with: files changed, implementation summary, tests/commands run and results, skipped or guarded e2e reasons, and known limitations.

Scope:
1. Prefer semantic UIA actions for element targets:
   - InvokePattern
   - TogglePattern
   - SelectionItemPattern
   - ExpandCollapsePattern
   - ValuePattern
   - RangeValuePattern if not already implemented
   - ScrollItemPattern
   - SetFocus only when it is the intended action or needed safely before a semantic operation.
2. Improve coordinate click routing:
   - when the user clicks by x/y, try UIA hit-test from point first.
   - if a meaningful element with a semantic pattern is found, use the semantic action.
   - fall back to input dispatch only when semantic action is unavailable or explicitly bypassed.
3. Return structured action details:
   - selected semantic pattern.
   - fallback reason.
   - final method used.
4. Preserve existing JSON envelope compatibility.
5. Do not add MSAA fallback in this goal; that is a separate feature.
6. Do not add daemon or remote listener behavior.

Tests:
1. Add or extend ActionRouter tests for semantic preference and fallback reasons.
2. Extend guarded WPF TestHost integration coverage for at least:
   - button invoke.
   - checkbox toggle.
   - textbox set-value.
   - slider range-value if not already covered.
3. Add CLI/MCP JSON shape tests for structured action details if new fields are exposed.
4. Ensure coordinate-click hit-test behavior has deterministic tests through seams/fakes if full Windows integration is flaky.
5. Match the style of the existing tests in `tests/Pbw.Tests/CoreTests.cs`: use bounded wait helpers, guarded Windows checks, and deterministic local UI controls.
6. Add at least one real-machine e2e-style validation path using the real CLI or service path against WPF TestHost for semantic action routing.
7. If coordinate hit-test routing cannot be made deterministic on the current host, unit-test the routing through seams and document the e2e limitation in `docs/progress.md`.

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
1. Update docs/progress.md with semantic action coverage and validation results.
2. Update docs/pbw-stable-spec.md if action details or routing behavior becomes part of the stable contract.

Stopping condition:
Stop only when semantic routing is implemented, fallback behavior is structured and tested, validation passes, docs are updated, and the final diff has been self-reviewed for behavior regressions and JSON compatibility.

Blocked behavior:
If a semantic pattern cannot be exercised reliably on the current host, cover the router behavior with fakes/seams, document the integration-test limitation, and continue.
```
