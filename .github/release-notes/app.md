Changes since v0.4.2 (released as v0.5.0):

- Web search via a self-hosted SearXNG instance — ARI provisions and starts the Docker container automatically on first launch, no configuration needed. Results are filtered for relevance before being returned to the agent.
- Web page reading replaced with a local Mozilla Readability extractor (SmartReader) — no external service required, no rate limits.
- Research agent now tracks a search/read budget to prevent runaway tool loops.
- SearXNG secret key is generated randomly on first run; a placeholder key is replaced automatically on upgrade.
