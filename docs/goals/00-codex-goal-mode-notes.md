# Codex Goal Mode Notes

This repo uses goal prompts for longer pbw implementation tasks. A goal prompt should define the outcome, success criteria, validation commands, stopping conditions, and blocked behavior in one self-contained request.

## What Was Checked

- `codex features list` reports `goals` as a stable feature and enabled on this machine.
- `~/.codex/config.toml` has `[features].goals = true`.
- `codex --help` does not expose a separate `goal` subcommand; prior local usage used `/goal ...` inside a Codex session.
- OpenAI release notes describe Goal mode as generally available across the Codex app, IDE extension, and CLI, allowing users to define an outcome and success criteria and let Codex continue working toward it.

## Prompt Shape

Use this structure for each feature:

```text
/goal <short feature outcome>

Objective:
...

Read first:
...

Scope:
...

Success criteria:
...

Validation:
...

Stopping condition:
Stop only when all success criteria pass, validation has been run, docs are updated, and the diff has been self-reviewed.
```

## Sources

- OpenAI Help Center release notes: https://help.openai.com/en/articles/6825453-chatgpt-accessibility
- OpenAI Help Center Enterprise/Edu release notes: https://help.openai.com/en/articles/10128477
- CUA Windows article: https://github.com/trycua/cua/blob/main/blog/inside-windows-computer-use.md
- CUA local reference expected at: `C:\Users\masatomo.kusaka\ghq\github.com\trycua\cua`
