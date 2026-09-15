Changes since v0.7.0:

- ARI now has its own calendar in place of the generic scheduler, and modules can be started/stopped live from the control panel instead of requiring a restart.
- Voice training (StyleTTS2): checkpoints now save into an organised Checkpoints/ folder, the learning-rate schedule and the diffusion/joint-training phase boundaries now correctly persist across pause/resume instead of resetting, and a rare per-epoch validation line no longer gets silently dropped from the training graph. Retrain now genuinely starts from a blank slate (checkpoints, log, and TensorBoard history all cleared) while keeping the dataset and epoch target.
- ARI no longer crashes entirely if a voice model fails to load — it now falls back to running with no voice loaded, same as the existing "no voices found" case.
- Voices tab: the active voice is now highlighted directly instead of a plain badge, and a voice with a saved training run but no trained model yet stays visible instead of disappearing.
- Restored a file explorer for a project's server-side folder — browse, open, upload (including drag-and-drop), and delete files directly from the Projects screen.
- Ari's background dreaming no longer hammers a stopped LLM server with connection-refused retries every few seconds — it backs off after repeated failures and skips entirely while its server is deliberately turned off.
- WebSocket endpoints for the client and listener now require authentication.
- Fixed reminder timing bugs and reminder creation now rejects invalid dates instead of silently accepting them.
- Ari now sends a push notification when she replies to a thread nobody is currently watching.
- Capped Engram's delete-gate so a stuck sweep can no longer leak scratchpads indefinitely.
- Image generation is now gated behind a vision review pass before the result is shown to you.
- Sensitive content is now marked in place with a callout on the ordinary note instead of being split into a separate note.
