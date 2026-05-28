# Goal Prompt: MSAA Fallback

```text
/goal Add a targeted MSAA fallback for pbw UI discovery and actions on legacy Windows apps, with tests, guarded integration behavior, and documentation.

Objective:
Improve pbw coverage for legacy or non-standard Windows applications whose UIA trees are empty, slow, misleading, or incomplete by adding a focused Microsoft Active Accessibility fallback. This should be additive and should not replace UIA as the primary path.

Read first:
1. docs/pbw-stable-spec.md
2. docs/progress.md
3. src/Pbw.Core/PbwCore.cs
4. src/Pbw.Windows/WindowsServices.cs
5. tests/Pbw.Tests/CoreTests.cs
6. CUA reference code from a local ghq checkout. If missing, run: ghq get https://github.com/trycua/cua
7. CUA files:
   - blog/inside-windows-computer-use.md
   - libs/cua-driver/rust/crates/platform-windows/src/msaa.rs
   - libs/cua-driver/rust/crates/platform-windows/src/uia/mod.rs
   - libs/cua-driver/rust/crates/platform-windows/src/tools/impl_.rs

Worker rules:
1. You are not alone in the codebase. Do not revert edits made by others; adapt to existing changes if present.
2. Do not commit. The orchestrator will review, request follow-up fixes if needed, and commit.
3. Keep edits within the ownership scope implied by this goal unless a narrowly-scoped supporting change is required.
4. Return with: files changed, implementation summary, tests/commands run and results, skipped or guarded e2e reasons, and known limitations.

Scope:
1. Add an MSAA adapter behind a Windows-specific interface:
   - use oleacc/IAccessible through .NET interop.
   - read name, role, state, bounds/location, child relationships, and default action where available.
   - map MSAA nodes into existing ElementSnapshot-compatible data without breaking schemaVersion.
2. Use MSAA as fallback only when appropriate:
   - UIA tree is empty or degraded for a target window.
   - known legacy provider classes/processes benefit from MSAA.
   - explicit future opt-in flag if needed.
3. Support safe semantic actions through MSAA where feasible:
   - default action/invoke equivalent.
   - expand/dropdown roles only if deterministic and safe.
4. Add metadata identifying element source as `msaa` or equivalent.
5. Guard against hangs, recursive loops, missing locations, and provider exceptions.
6. Do not introduce remote daemon behavior, non-.NET runtime dependencies, or broad rewrites of the UIA layer.

Tests:
1. Unit tests for MSAA role/state mapping using fakes or seamable adapters.
2. Unit tests for fallback decision logic: UIA ok -> no MSAA; UIA empty/degraded -> MSAA attempted.
3. Tests for ElementSnapshot compatibility and source metadata.
4. Guarded Windows integration tests only if a deterministic local MSAA target is available. If not, document the limitation and keep deterministic unit coverage.
5. Match the style of the existing tests in `tests/Pbw.Tests/CoreTests.cs`: guarded Windows checks, deterministic assertions, and no unbounded provider traversal.
6. Add a real-machine e2e-style MSAA validation only if a deterministic local target can be created or launched in-repo. Prefer a repo-controlled target over relying on Notepad, Office, LibreOffice, or other host-specific apps.
7. If no deterministic MSAA target exists, add a clearly skipped/guarded test or progress note explaining why, and cover adapter/fallback behavior through fakes.

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
1. Update docs/progress.md with MSAA fallback notes and validation results.
2. Update docs/pbw-stable-spec.md to state that UIA remains primary and MSAA is an additive fallback for degraded/legacy providers.

Stopping condition:
Stop only when MSAA fallback is implemented behind interfaces, fallback decisions are tested, validation passes, docs are updated, and the final diff has been self-reviewed for COM lifetime, recursion limits, provider hangs, and JSON compatibility.

Blocked behavior:
If no deterministic MSAA integration target is available, implement the adapter and fallback logic with unit tests/fakes, document the missing integration target, and continue until all other success criteria pass.
```
