# Training

Harnesses and living logs for iteratively training A.R.I's agents. Same method for each: a **fixed baseline**,
**restore between runs**, **one change per cycle**, **grade from the logs**, and a **living LOG.md** that shows
the whole arc at a glance.

## How to use
When you say **"train refactor"** (or coding / engram), I:
1. Read `Training/<type>/HOWTO.md` — how to run a test, what commands to use, the grading rubric.
2. Read `Training/<type>/LOG.md` — current status + cycle history, to resume where we left off.
3. Run a test, grade it, record the cycle in LOG.md, make one targeted fix, repeat.

## The three agents
| Type | Agent | What we're training | Folder |
|---|---|---|---|
| **refactor** | Refactor (graph-walk) | Tidy the memory graph — route sprawl through hubs, type nodes, merge dupes, one clean commit per epoch, fast | `Training/refactor/` |
| **coding** | CodeArchitect + Coder | Plan + apply code changes that compile and match intent | `Training/coding/` |
| **engram** | Engram (conversation→memory) | Read a conversation, place the right memories in the right notes | `Training/engram/` |

## Shared principles
- **Autonomous runs.** I launch ARI, trigger the agent via the localhost `/commands` endpoint
  (`X-Eval-Token: ari-eval-local-2026`), watch the log, and stop cleanly — no manual reboot needed.
- **Single unit per test** where possible (one epoch / one conversation / one change), and **re-run to filter
  variance** — the local 35B model is non-deterministic; grade the distribution, not one lucky try.
- **Observability first.** If you can't see it in the log, add a log line before iterating blind.
- **Don't break siblings.** The file/git tools are shared with the coding pipeline — when editing a shared
  tool, check `Coder.ToolLoop.cs` for result-string dependencies (e.g. it keys on "Successfully wrote").
