# Agent Playbook

This file is the shared policy for all agents working on PathShortener.

## Working Rules

- Work directly in `D:\Projects\PathShortener` by default.
- Inspect `git status --short` before editing.
- Keep changes scoped to the requested task.
- Preserve user changes and inspect the current diff before editing files that may already be modified.
- Do not create worktrees, duplicate project copies, agent-specific folders, or switch branches unless the user explicitly asks for that in the current task.
- Do not run `git add .`; stage only explicit files when staging or committing is requested.
- Do not commit, push, deploy, rotate secrets, or change external infrastructure without explicit approval.
- If `PathShortener_SKILL.md` is present, read it before changing project behavior.

## Parallel Agents

- Multiple agents may work in the same project folder only when the user assigns non-overlapping files or areas.
- Before editing, identify the files you plan to change.
- Avoid simultaneous edits to the same files.
- Never revert, overwrite, or normalize changes made by another agent or the user.
- Use branches, worktrees, or pull requests only after explicit user approval for that project or task.

## Completion Report

Every agent must report:

- Files changed.
- Commands run.
- Test results.
- Risks or unverified areas.
- Required human approvals.
