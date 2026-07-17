Changes since v0.1.10:

- **Breaking:** config no longer lives in a `PersistentData` subfolder — servers, models, agents, persona, scheduler and coding-conventions state now sit directly under the server's app-data root. Existing 0.1.x installs will not find their old config here and start clean.
- Agent prompts moved out of code and into `Agents.json`, editable from a rebuilt Agents tab — every prompt each agent sends, grouped by agent, with the tokens it uses explained above each field. Bindings (which server and slot an agent runs on) are now separate from the prompt itself and default to the first server if unset.
- Discord's token, whitelist, watched channels and allowed guilds are now configured from the control panel instead of environment variables and a config file.
- The scheduler runs jobs at their actual scheduled time instead of firing everything that was missed while ARI was off; each job can be stopped from the panel mid-run.
- A fresh install now seeds a demo server and a small model (Qwen3.5-9B) instead of starting with empty lists — nothing is downloaded or started automatically. The chat composer greys out with an explanation while no model server is running, instead of accepting a message nothing can answer.
- Removed an unused second code-execution path (the old `Coder` agent), folding its still-used pieces (coding-conventions injection, thread budgets) into the agent that now does all the work.
- Voice: StyleTTS2 no longer reinstalls its Python dependencies on every launch, and its diagnostic output no longer floods the log at warning level.
- Removed personal/example data that had leaked into shipped default prompts.
