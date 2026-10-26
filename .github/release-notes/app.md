Changes since v0.8.3 (v0.8.5 was never published as a Docker image, so 0.8.6 is the first):

- A·R·I can now run in Docker, including on a DGX Spark: a published image (`ghcr.io/ari-labz/ari`) built for ARM64 and AMD64 with GPU-enabled llama.cpp, plus ready-made Docker Compose files.
- First-run setup wizard and a guided tour: create the Master account, pick a model and start the server from the browser.
- Privacy and security: guests can no longer see other people's threads or the admin controls, the Master account owns the brain, pushes only go to the person they are for, slash commands are Master-only unless listed as open to anyone, and refused requests now return 403.
- Persistent Threads (off by default): expired conversations can be kept as snapshots, shown greyed in the sidebar and reopened by sending a message.
- `/purge notes` now backs up the brain first, and backups live in the app data folder.
- Control panel: a reworked overview with an AI-RAM limit line, one Users page, a new Settings page, per-agent on/off switches, prompts on their own page, and colour and logo customisation.
- Docker: a `UMASK` setting makes everything ARI creates readable and writable by everyone, so a shared data folder (for example over SMB) can be edited. The DGX Spark compose file (`docker/compose.dgx-spark.yml`) keeps all data in plain folders under `/ARI`.
