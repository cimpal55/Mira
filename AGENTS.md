# Agent Memory

This repository is a .NET 9 personal Telegram assistant named Mira.

## Workflow preferences

- Read `CLAUDE.md` when it exists locally; it contains the detailed project brief and Git workflow, but it may be intentionally untracked.
- Track multi-step work with todos.
- After completing each meaningful todo batch, run the relevant checks and create a local Conventional Commit.
- Always report the commit hash after committing.
- Do not push commits unless explicitly requested.

## Quality gates

- For .NET code changes, consult the relevant dotnet-skills guidance before editing.
- Run `slopwatch analyze --no-baseline` after LLM-authored .NET source or project-file changes.
- Prefer focused tests for changed behavior, then a Release build/test pass before yielding on non-trivial changes.
