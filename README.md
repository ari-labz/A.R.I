<img width="1810" height="869" alt="A·R·I" src="https://github.com/user-attachments/assets/d0743f8e-5a59-4db1-b10c-ee1824aebfbf" />

*A·R·I is a locally-hosted, privacy-first intelligence framework built on one core principle: an AI can only be truly personal if it is truly private.*

## What is A·R·I

A·R·I treats the language model as a component, not the product. She's the orchestration layer around it: persistent memory that survives model swaps, a coding pipeline, voice synthesis, and a continuous personality — all running locally.

## How she works

A·R·I's agents are organized into **pipelines**, each with its own prompt set tuned for a different kind of work. A lightweight classifier reads each incoming message and routes it to the most suitable pipeline — much like a mixture-of-experts model routing a token to the right expert, but at the level of whole conversations.

- **Dialogue pipeline** — companionship and everyday conversation.
- **Code pipeline** — an architect agent explores the codebase, plans the change, and carries it out itself. For non-trivial changes it proposes a plan and waits for approval before editing.
- **Speech pipeline** — spoken, voice-mode conversation. *(in development)*

The same underlying model can serve all three; what changes is the pipeline wrapped around it.

## Memory

She remembers. Facts, events, and preferences go into a knowledge graph she curates herself — deciding what's worth keeping rather than appending to a flat file. The graph is stored as an **Obsidian vault**: each idea is a markdown note, and notes are linked to one another with **wikilinks**. She organizes it on a **small-world** principle, so related ideas stay within a few hops of each other — keeping recall fast and associative.

It works in two passes around a conversation: a **Recall agent** pulls relevant memories from the graph and injects them into the prompt before she responds, and an **Engram agent** reads conversation excerpts afterward, extracts useful information, and saves it to the graph. Because the vault is plain markdown that lives independently of the model, swapping models doesn't reset her — and you can open and browse her memory in Obsidian yourself.

## Coding

**Pair programmer.** The interesting part is the harness, not the model: the Code pipeline runs an architect agent that explores the codebase, plans a change — proposing it for approval when it's non-trivial — and edits the files directly, all with prompts tuned to lift a mid-tier local model's coding above its baseline. Good enough to help with actual work — not a Claude/GPT replacement.

## Voice

A·R·I has a voice — and can make her own. Give her a few minutes of audio and she'll train a StyleTTS2 model, then use it to speak her responses. A built-in dataset builder handles the prep (isolating vocals, splitting, transcribing).

## Other features

Beyond the core pipelines, A·R·I can reach you on **Discord** (servers or DMs) and even **message first** — a proactive system lets her open a conversation when she has something to say, rather than only responding. You can talk to her by voice via local **speech-to-text** (Whisper), and manage everything — models, config, voice training — from a web **control panel**.

The web interface runs anywhere the server does, but there's also a native **[desktop app](https://github.com/ari-labz/A.R.I-Desktop)** that wraps the same interface and connects to your server.

## Multiple models at once

Each agent can be pointed at its own model server, so — if your hardware can handle it — A·R·I can run several LLMs concurrently and give each agent the model best suited to its job: a small fast model for memory recall and classification, a larger model for main tasks like dialogue and coding, and so on, instead of forcing a single model to do everything.

## Limitations

A·R·I is a personal project, and the rough edges show. She's only as good as the model you can run — weaker hardware, weaker A·R·I. She's slower than any cloud assistant because nothing leaves your machine. Her coding is genuinely useful but local-model-tier, not a frontier agent.

## Requirements & setup

Download the latest installer for your platform from the [Installer releases page](https://github.com/ari-labz/Installer/releases) and run it — choose **Server** and it fetches and installs the server for you.

### Getting past the "unverified app" warning

The installers are **not** signed with a paid Apple/Windows certificate, so your OS will warn you the first time you open one. This is expected — the app is safe, it's just unsigned. How to proceed:

- **macOS** — if you see *"A·R·I … can't be opened because Apple cannot check it for malicious software"* (or *"is damaged"*), **right-click (Control-click) the app → Open → Open**. You only need to do this once. (Do not double-click — that offers no bypass.)
- **Windows** — if SmartScreen shows *"Windows protected your PC"*, click **More info → Run anyway**.
- **Linux** — the installer is an AppImage; mark it executable (`chmod +x ARIInstaller-linux.AppImage`, or right-click → Properties → allow executing) and run it.

## Third-party components

A·R·I builds on a lot of other people's work — llama.cpp, StyleTTS2, Whisper, and more. See [THIRD_PARTY.md](THIRD_PARTY.md) for the full list and their licenses.

## License & credit

A·R·I is open source under the [GNU AGPL v3](LICENSE), with [additional terms](ADDITIONAL-TERMS.md) that protect credit. Created by [Xywren](https://github.com/Xywren).

- **Use it freely.** Run it, change it, experiment with it, keep private copies for yourself or your team.
- **Share alike.** If you publish, distribute, sell, or host a version of A·R·I for others, it must be open source under the AGPL v3, so improvements flow back to everyone. Local AI is for everyone.
- **Credit the original, visibly.** Show "Based on A·R·I by Xywren — https://github.com/ari-labz/A.R.I" at the top of your README, on your product's About screen and on any page where it's sold or downloaded, and keep the credit line in the startup log. No fine print.
- **Keep the history.** Don't rewrite the upstream git history or authorship, and keep the [NOTICE](NOTICE) file's credit chain.
- **Your changes are yours.** Call your version whatever you like, but don't present it as the original or main A·R·I.

Plugins and voice modules are add-ons, not A·R·I itself, and keep their own licences. Plugins built against `ARI.Sdk` can use any licence you like.

Please **fork** rather than re-upload. It keeps the credit trail and the network graph pointing home.

Releases up to v0.10.0 were published under the Apache License 2.0.
