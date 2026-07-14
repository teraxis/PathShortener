# Agent Instructions

Read `docs/agent-playbook.md` before changing files.

## Required Behavior

- Work directly in `D:\Projects\PathShortener` by default.
- Do not create worktrees, duplicate project copies, agent-specific folders, or switch branches unless the user explicitly asks for that in the current task.
- Inspect `git status --short` before editing.
- Keep changes scoped to the requested task and preserve unrelated work.
- Do not run `git add .`; stage only explicit files when the user asks for staging or commits.
- If `PathShortener_SKILL.md` is present, read it before changing project behavior.
- After code changes, run the relevant verification commands or state why they could not run.
- Report changed files, checks run, unverified areas, and required approvals.
