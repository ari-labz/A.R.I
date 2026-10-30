Changes since v0.9.0:

- Plugin SDK: plugins talk to A·R·I through one standard route (`ARI.Sdk`). A chat plugin hands each incoming message to A·R·I, which works out who it is from, decides whether to answer, runs the normal dialogue and sends the reply back. Core no longer contains any Discord code.
- Discord is now a plugin: install it from the Browse tab of the Plugins page. Direct messages A·R·I starts stay open until they time out, what the other person says is passed back to you, and tool use no longer shows up in Discord.
- Plugin store: a Browse tab with search and featured plugins, installs checked against a checksum, `.ari` files you can export from a plugin's page or drop anywhere in the app to install, and a picture for every plugin.
- Browser: the live view now fills its pane at any size, with no empty strip underneath, and the page is drawn at the pane's size.
- Dev Mode now keeps conversations across restarts like a normal run, but in a throwaway folder that is emptied at every start, so your real saved conversations are never touched.
- Plugins made for older versions of A·R·I (the first Discord release) need updating from the store.
