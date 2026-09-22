# Session hygiene (token efficiency)

These rules exist because long-running Codex sessions on this repo have shown
heavy token growth: auto-compaction re-embeds full-resolution screenshots
verbatim instead of dropping them, and unfiltered build/test/search output
piles up turn after turn. Follow these to keep sessions fast and cheap
without losing correctness.

## Session lifetime
- Treat each conversation as scoped to one task (one feature/bugfix/chore).
  Once its change is committed, don't keep chatting in the same thread for
  unrelated work — start a new conversation instead.
- If a thread has already gone through 2+ auto-compactions, finish the
  current task and start a fresh thread rather than continuing further.

## Screenshots / images
- Crop to the relevant UI region before analyzing; avoid full-screen or
  full-resolution captures when a crop suffices.
- Once a screenshot's purpose is served (e.g. confirming a visual fix), treat
  it as consumed — don't keep referencing it as ongoing context.

## Command output
- Run builds/tests at reduced verbosity by default:
  `dotnet build -v minimal`, `dotnet test --logger "console;verbosity=minimal"`.
  Only re-run at full verbosity when actively diagnosing a specific failure.
- Prefer targeted `rg`/`Get-Content` (specific files, line ranges, `-A`/`-B`
  context) over broad whole-file or whole-tree dumps.
- Cap output explicitly (e.g. `max_output_tokens`) for any command that could
  return more than a screenful.

## Avoid redundant re-discovery
- Before re-reading a file or re-running a search, check whether this session
  already has that information from an earlier turn in the same thread.
- Use this file and README.md for architecture context instead of
  re-deriving it via repeated `rg`/tree dumps each session.

## Avoid poll loops
- Don't use `wait`/`sleep` to poll a running process turn-by-turn. Run the
  command synchronously and read its final output once, or use a single
  longer wait instead of repeated short polls.
