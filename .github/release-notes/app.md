Changes in v0.9.7:

- Memory (Engram) audit fixes: Engram now keeps only lasting facts, not passing moments, and replaces a fact when it changes rather than only when it's wrong; it reads what was actually said, not voice notes or resends. Day-log entries get room to finish and never end mid-sentence, and dead links are cleaned up after an entry is written. A reopened thread is swept from where Engram actually got to, and messages stay unswept when the model server doesn't answer. Person notes are written as short sections by topic, and events notes record happenings that matter.
- Threads: rename and hide. Renaming sets a custom title that stops auto-renaming; hiding is per-user and filters the thread from the list.
- UI: the command prompt height is capped so approve buttons stay visible, and code-run approval can be allowlisted per thread.

Changes in v0.9.5:

- Memory: A·R·I remembers again. Before she replies, Recall follows the links between the notes in her brain, several at a time, so what you told her in earlier conversations comes back when it matters, and her replies stay quick.
- Within a conversation, Recall keeps the notes it has already opened instead of reading them again, and on each new message it tells her only what she hasn't been told yet.
