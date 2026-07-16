Changes since v0.1.8:

- The published server now includes the web UI. A build-ordering bug (the wwwroot content glob was evaluated before the UI build ran) meant releases shipped without the compiled front-end, so the API worked but the website returned 404 (both in the browser and the desktop app). The build now copies the built UI into the published output explicitly.
- llama-server's very verbose output no longer floods the A·R·I console. It's redirected to its own file (Logs/llama-<server>.log) instead.
