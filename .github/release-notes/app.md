Changes in v0.11.3 (includes v0.11.1 and v0.11.2):

- Python: ARI downloads her own Python instead of using the one on the machine, so voice, listening and the document tools set themselves up on a clean computer with no developer tools installed (a fresh Mac used to stop at an "install developer tools" dialog). Parts that need a particular Python version fetch that version and keep their own environment.
- Listening: speech is detected with the Silero model that comes with faster-whisper instead of webrtcvad, so nothing has to be compiled during setup (no C++ build tools needed on Windows). Turns are split the same as before, and steady hum or a door slam no longer counts as speech.
- Dreams: a Dreamer entry in the Agents tab chooses which server and slot dreams run on, so they can use a different model from chat. Without it, dreams run where Dialogue does, as before.

Changes in v0.11.0:

- Dreams: Ari can decide at night what to tell you and hold it until a sensible time (wake with send_at): nothing else runs while it waits, and if you talk to her first she tells you straight away. A new sleep tool lets a dream go quiet until a set time and carry on from a note to itself, instead of passing the night re-reading.
- Documents: Word lists, Word titles and PowerPoint slide titles reach Ari as Markdown, so she sees a document's structure instead of flat lines.
- Licence: ARI is now licensed under AGPLv3 with credit terms (see LICENSE and ADDITIONAL-TERMS.md). The startup log and a new About tab in the control panel credit the original author, and third-party components (including IndexTTS2) are listed in THIRD_PARTY.md.
- Agents: the default Engram and Subagent prompts are the newer ones.

Changes in v0.10.0 (replaces v0.9.7 and v0.9.8):

- Dreams: a dream runs as long as Ari likes and moves through subjects one at a time; finishing a subject without waking you moves her on to a new one. Long dreams are compacted into her own summary instead of running out of room, and the dream log shows the real duration and every tool call.
- Reflector: it no longer treats an ordinary chat as a work report just because Ari read a file, never tacks "(not verified)" onto a sentence, and leaves writing you asked for and her warmth alone.
- Plugins: on/off settings are checkboxes and can actually be switched on.
- Projects: sync between the desktop app and the server works both ways and on its own: a save on your computer syncs a few seconds later, and changes Ari makes on the server come down within a minute. When both sides changed, they are merged, and a real clash names the files instead of overwriting either. Ari's unsaved edits on the server are never lost to a sync. Each project can choose whether Ari keeps a copy on her server (off for projects made in the desktop app, so files aren't stored twice). Needs A·R·I Desktop v0.5.0.
- Pages Ari makes (charts, tools, games) open in a window beside the chat, and she runs them first so errors that only show while a page plays come back to her. Every file she edits opens a window too, in any chat.
- Memory: sweeps never hold up a reply; they wait in a queue and run when Ari is idle. New ask_memory lets her ask her memory a question mid-reply. Events are never duplicated, and a brain curator suggests merges, types, links and corrected "can't do" sentences for you to approve in the Brain tab, which also has a brain browser.
- Personas: Ari can have several personas and switch per conversation; choose one in the control panel's Persona tab. A Reflector reads finished replies and leaves suggestions to approve or dismiss.
- Coding: a review loop checks the Coder's diff against your rules (code_rule saves what you teach her), she checks in after six file changes, edit_files changes several files all-or-nothing, and she follows a project's ARI.md, AGENTS.md or CLAUDE.md. A shell stays open between commands.
- New tools and plugins: MCP servers, a 3D model viewer, frame-by-frame animation checks, sound and video inspection (ffmpeg plugin), Unity, SearXNG and Cephalon plugins, and plugin hooks.
- Calendar: Remind me on events, and each morning Ari looks at the week ahead for things worth preparing.
- Updates: with Update automatically switched on, ARI installs a new stable release once she has been idle for ten minutes. Start at login keeps her running after a restart.
- Chat logs are readable: tool calls as plain lines, attachments listed, and messages sent while Ari is replying recorded.
- Control panel reorganised into clearer groups (nothing removed), a servers page with a memory bar and start/stop for everything, and a settings backup that saves everything except the brain.
- Fixes: the sidebar only indents a thread while it shows a state icon, links are readable in code mode, push notifications show only what Ari said, a message that can't reach Ari says so, the server version shows beside the logo, and the Docker image reports the right version.
- Memory (Engram) audit fixes: Engram now keeps only lasting facts, not passing moments, and replaces a fact when it changes rather than only when it's wrong; it reads what was actually said, not voice notes or resends. Day-log entries get room to finish and never end mid-sentence, and dead links are cleaned up after an entry is written. A reopened thread is swept from where Engram actually got to, and messages stay unswept when the model server doesn't answer. Person notes are written as short sections by topic, and events notes record happenings that matter.
- Threads: rename and hide. Renaming sets a custom title that stops auto-renaming; hiding is per-user and filters the thread from the list.
- UI: the command prompt height is capped so approve buttons stay visible, and code-run approval can be allowlisted per thread.

Changes in v0.9.5:

- Memory: A·R·I remembers again. Before she replies, Recall follows the links between the notes in her brain, several at a time, so what you told her in earlier conversations comes back when it matters, and her replies stay quick.
- Within a conversation, Recall keeps the notes it has already opened instead of reading them again, and on each new message it tells her only what she hasn't been told yet.
