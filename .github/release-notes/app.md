Changes since v0.9.3:

- Rename her: A·R·I is the default, but you can give the AI her own name (and how it's said, for the wake word) in Customisation → Branding. Prompts use an {AI} token wherever they mean her.
- Commands: read-only commands (grep, cat, ls, git log and the like) run without asking. Anything else needs your OK the first time, one command at a time, with Allow once or Allow every time. Chaining commands with &&, |, ; or a new line can't slip one past the check, and dangerous ones ask every time.
- Desktop app: A·R·I asks before changing a file outside the open project.
- Projects: choose what new chats in a project start in: Auto, Dialogue, Code, or a pipeline you've made.
- Voices: Voice Settings now live on each voice's own page. Picking a voice loads the voice module first, and you can test any saved checkpoint before choosing it.
- Attachments: drop files onto one zone to attach them to a message.
- API: a client app can give a thread hidden context she reads every turn (POST /threads with "context", or /threads/{key}/context).
- Logs start with the A·R·I version, Docker or native, the OS and .NET, so a pasted log says which A·R·I it came from.
- Fixes: voice training failing to start for StyleTTS2 or KokoroRVC, KokoroRVC no longer asks for transcripts, the loss graph keeping one point per epoch, the speaker button showing when voice isn't running, the control panel's window buttons on Windows and dragging its header in the desktop app.
