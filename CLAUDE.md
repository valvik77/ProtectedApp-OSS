# Session hygiene (token efficiency)

Measured on this project: single-day sessions running 300-500+ turns caused
per-turn cache-read cost to grow roughly linearly (one session went from ~26K
to ~513K tokens/turn before auto-compact finally fired, hundreds of turns too
late). Follow these to keep sessions cheap without losing quality.

## Session lifetime
- Scope a session to one task (one feature/bugfix/chore). Once its change is
  committed, don't keep working in the same session for unrelated work —
  start a new one instead.
- Don't let a single session run unbounded across a whole day. If a session
  has done a couple hundred turns or clearly covered several unrelated
  pieces of work, suggest wrapping up or running `/compact` rather than
  waiting for auto-compaction to trigger on its own — by the time it fires
  automatically, the session has usually already paid for a lot of avoidable
  growth.

## Tool output
- Prefer targeted reads (Read with offset/limit, Grep with head_limit/glob)
  over dumping whole files or whole-tree output — this is already the
  default tool behavior here; keep it that way rather than falling back to
  raw `cat`/`Get-Content` of large files via Bash.

## Subagents
- Only spawn a subagent (Task/Agent) when the work genuinely benefits from
  isolation or parallelism; a subagent starts cold and re-derives context
  you already have, so don't use one as a substitute for just doing the work
  inline.
