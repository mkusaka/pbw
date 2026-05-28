# Goal Prompt: UIA Robustness And Cache Requests

```text
/goal Implement pbw UI Automation robustness improvements, including cache requests, timeout/degraded handling, root-by-process fallback, RangeValue support, tests, and documentation.

Objective:
Make pbw's UI Automation tree reading and semantic actions more reliable on slow, partial, or unusual Windows accessibility providers. The feature should improve robustness without changing pbw's stable JSON contract unnecessarily.

Read first:
1. docs/pbw-stable-spec.md
2. docs/progress.md
3. src/Pbw.Windows/WindowsServices.cs
4. src/Pbw.Core/PbwCore.cs
5. tests/Pbw.Tests/CoreTests.cs
6. tests/Pbw.TestHost/Program.cs
7. CUA reference code from a local ghq checkout. If missing, run: ghq get https://github.com/trycua/cua
8. CUA files:
   - blog/inside-windows-computer-use.md
   - libs/cua-driver/rust/crates/platform-windows/src/uia/mod.rs
   - libs/cua-driver/rust/crates/platform-windows/src/uia/cache.rs
   - libs/cua-driver/rust/crates/platform-windows/src/tools/impl_.rs

Current pbw context:
- WindowsElementAutomationService reads UIA elements through managed System.Windows.Automation.
- It detects common patterns and performs Invoke, Value, Toggle, SelectionItem, ExpandCollapse, ScrollItem, and Focus paths.
- It uses a fixed MaxDepth and per-element current property/pattern reads.
- It does not yet use UIA CacheRequest, root-by-process fallback, explicit UIA timeout/degraded results, or RangeValuePattern.

Implementation requirements:
1. Add UIA CacheRequest usage for tree reads:
   - cache the properties pbw serializes into ElementSnapshot.
   - cache supported patterns needed by pbw actions.
   - keep behavior safe if a provider rejects cached access.
2. Add robust tree fallback behavior:
   - if ElementFromHandle or a window-scoped query returns an empty wrapper, attempt a desktop-root walk filtered by process id/window relationship where safe.
   - preserve depth and total element limits to prevent runaway traversal.
3. Add timeout/degraded handling around expensive UIA reads:
   - return a structured degraded element or metadata rather than raw exceptions.
   - avoid hanging the whole `pbw see` path on a bad provider.
4. Add RangeValuePattern support:
   - expose "RangeValue" in detected patterns.
   - implement set-value for range-capable controls when the input can be parsed as a number.
   - return structured errors for invalid values or unsupported ranges.
5. Keep existing ElementSnapshot JSON compatible. Add metadata/details only when needed.
6. Do not add MSAA fallback in this goal. MSAA is a separate later feature.
7. Do not introduce a daemon, remote listener, or non-.NET runtime dependency.

Tests:
1. Unit tests for pattern detection including RangeValue where feasible with test seams.
2. Extend the WPF TestHost with a deterministic range control such as Slider.
3. Add guarded Windows integration coverage for setting the slider through RangeValuePattern.
4. Add tests for UIA error/degraded behavior using a fake or seam if direct provider timeout is not deterministic.
5. Ensure existing snapshot, CLI, and MCP tests still pass.
6. Match the style of the existing tests in `tests/Pbw.Tests/CoreTests.cs`: guarded real Windows integration, deterministic WPF TestHost controls, and no flaky sleeps without bounded wait helpers.
7. Add at least one real-machine e2e-style validation path that uses the real `WindowsElementAutomationService` against the WPF TestHost and verifies the UI state or output file changed through UIA, not only through fakes.
8. If UIA timeout behavior cannot be deterministically triggered, cover the timeout/degraded mapping with unit seams and document the integration limitation in `docs/progress.md`.

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
1. Update docs/progress.md with UIA robustness notes and validation results.
2. Update docs/pbw-stable-spec.md if new degraded metadata or RangeValue behavior becomes part of the stable contract.

Stopping condition:
Stop only when cache/fallback/degraded behavior and RangeValue support are implemented, tests pass, docs are updated, and the final diff has been self-reviewed for hangs, broad exception swallowing, JSON compatibility, and Windows-only guard correctness.

Blocked behavior:
If a timeout or fallback cannot be deterministically integration-tested on the current host, add unit coverage through seams/fakes, document the limitation in docs/progress.md, and continue until the implementation and available validation are complete.
```
