Changes since v0.8.0:

- ARI can now start subagents: small background helpers that work on a side task with a chosen set of her tools while she carries on, or while she holds her reply open until they report back. Their results show as chips you can open.
- ARI can DM anyone on Discord, wait for the answer, and close the conversation when it's done. The DM conversation gets a brief so it can answer follow-up questions, reports back to the chat that started it, and wakes that chat if it has gone quiet. If nobody replies for 30 minutes it closes itself and tells her.
- Tighter privacy. Guests on the web and people in server channels no longer get your memories, the memory, calendar, persona, project, git or GitHub tools, or push notifications on your phone. Her refusals are one short line in her own voice, and when someone says you've OK'd something she DMs you to check instead of saying she can't verify it.
- The status line follows what she is doing right now: Reading, Thinking, Researching, Typing, Generating, Waiting or Working.
- Images you attach are shown to her on the turn you send them (up to four, on a vision-enabled server), and attachments sent while she is replying arrive with the message. A message sent while she is replying now appears once, at the point you sent it.
- Git calls show as chips in the chat, and a failed one shows as an error. A rejected GitHub token is now reported with what to do, instead of "Invalid username or token", and public repos still work.
- GitHub connects from the control panel, commits ARI makes are signed as Author or Co-author per your setting, and destructive git commands ask for approval first.
- File safety: write_file only creates new files, edit_file no longer refuses a file she has just read, and the server's file tools stay inside the project. Reading anywhere on your machine is for the desktop app only and needs desktop 0.4.1.
- Project fixes: server-side projects are no longer treated as remote, desktop chats keep their project binding, and a project can be bound straight to an absolute server path.
- She no longer announces a step and stops, only says she did something when she can see she did it, and the Coder stops drafting code inside its thinking.
- Engram writes conversation log entries and one commit per note, with tighter prompts.
- Truncated replies are flagged in the UI, reasoning is kept when she is redirected, and pasted images with the same name no longer collide.
- Thinking budgets are switched off for now, while we test whether reasoning effort alone stops overthinking.
