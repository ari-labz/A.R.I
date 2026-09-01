Changes since v0.6.0:

- Engram (the background process that files conversations into Ari's memory) redesigned around three short, focused steps instead of one long wandering pass — a sweep that used to take 15+ minutes now finishes in a fraction of that.
- Background memory work (Engram, Refactor, Curiosity) now runs on a real priority queue and always yields to a live conversation — it only fills genuine idle gaps, never delays a reply.
- Private or sensitive memories are now kept in their own separate, linked notes rather than mixed into an entity's normal note — Ari won't bring one up in conversation unless you go there first, and it's never surfaced when talking to anyone other than the owner.
- Fixed a bug where a fully-written reply could occasionally be discarded and silently regenerated from scratch after it had already finished streaming.
- Fixed Engram getting permanently locked out of reading any brain note over 100 lines partway through a sweep.
- Curiosity and ProactiveMessage are gone, replaced by Dreaming — every agent-initiated thread now sends a push notification.
- Logs restructured: ChatLogs (human-readable transcripts), SystemLogs (per-run copies), DTILogs (full reasoning/tool trace, renamed from Sessions), DreamLogs.
- Image generation: added img2img support (reference images + denoise control), removed all video generation, images attached to messages are now available to image-gen tools automatically.
- DTI panel: completed sessions (like Engram sweeps) now show under a collapsible section; live auto-refresh when threads are created or deleted.
- Fixed a couple of chat rendering bugs (images appearing twice, a spurious "connection closed" message on minimize).
