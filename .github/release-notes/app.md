Changes since v0.1.6:

- Fixed the server failing to start on macOS while installing llama.cpp. Homebrew is now invoked by its absolute path (a GUI-launched app can't resolve the bare "brew" or "llama-server" name off a login PATH it never inherited), and the resulting llama-server path is resolved absolutely.
- If the Homebrew install of llama.cpp fails for any reason, the server now falls back to downloading a prebuilt llama.cpp binary instead of aborting.
