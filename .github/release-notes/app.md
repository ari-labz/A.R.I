Changes since v0.1.5:

- Homebrew is no longer required to start the server on macOS. It's now detected reliably (by its real install path, not PATH) and treated as optional — a missing Homebrew is a warning, not a fatal error.
- On macOS without Homebrew (e.g. a non-admin account), llama.cpp is now downloaded as a prebuilt binary instead of failing. The server no longer attempts the interactive Homebrew installer, which could never succeed from a GUI-launched app.
