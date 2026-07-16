Changes since v0.1.9:

- Fixed the A·R·I console window closing when you stop and then start the server again. Killing the server broke the stdout/stderr pipes the console reads, and the resulting unhandled stream error crashed the console process. Those pipe errors are now handled, and a global safety net keeps the console alive (and logs) instead of closing on any unexpected error.
