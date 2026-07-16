Changes since v0.1.7:

- The server now finds an existing Homebrew-installed llama-server directly by its path, instead of relying on a PATH lookup that a Finder-launched app doesn't have. This means an installed server reuses the same llama.cpp your dev environment already uses, rather than reporting "not found" and reinstalling it.
