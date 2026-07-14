# Third-party components

A·R·I stands on a lot of other people's work. This lists the major third-party
components she uses and their licenses. It is not exhaustive (transitive
dependencies aren't listed), and licenses are as understood at time of writing —
always defer to each project's own license for the authoritative terms.

New components should be appended to the relevant section.

## Models & inference

| Component | Role | License |
|-----------|------|---------|
| [llama.cpp](https://github.com/ggml-org/llama.cpp) | Local LLM inference server (`llama-server`) | MIT |

The underlying language model you run is your own choice and carries its own
license — A·R·I does not bundle one.

## Voice synthesis

| Component | Role | License |
|-----------|------|---------|
| [StyleTTS2](https://github.com/yl4579/StyleTTS2) (via [a fork](https://github.com/Xywren/StyleTTS2)) | Text-to-speech model & training | MIT — © 2023 Aaron (Yinghao) Li |
| [espeak-ng](https://github.com/espeak-ng/espeak-ng) | Phonemization backend (system binary, called at runtime) | GPL-3.0 |
| [phonemizer](https://github.com/bootphon/phonemizer) | Python phonemizer wrapper | GPL-3.0 |
| [PyTorch](https://pytorch.org/) (torch, torchaudio) | ML runtime | BSD-3-Clause |
| [Transformers](https://github.com/huggingface/transformers) | Model utilities | Apache-2.0 |
| [Demucs](https://github.com/facebookresearch/demucs) | Source separation (dataset prep) | MIT |
| librosa, SoundFile, pydub, munch, einops, accelerate, nltk, monotonic_align | Audio/ML support libraries | various (MIT/ISC/BSD) |

## Speech input

| Component | Role | License |
|-----------|------|---------|
| [faster-whisper](https://github.com/SYSTRAN/faster-whisper) | Speech-to-text | MIT |
| [OpenAI Whisper](https://github.com/openai/whisper) | Underlying STT model | MIT |
| [py-webrtcvad](https://github.com/wiseman/py-webrtcvad) | Voice activity detection | MIT / BSD |

## Server (.NET)

| Component | Role | License |
|-----------|------|---------|
| [.NET](https://github.com/dotnet/runtime) | Runtime | MIT |
| [Discord.Net](https://github.com/discord-net/Discord.Net) | Discord integration | MIT |
| [Serilog](https://github.com/serilog/serilog) | Logging | Apache-2.0 |
| [YamlDotNet](https://github.com/aaubry/YamlDotNet) | YAML parsing | MIT |
| [Cronos](https://github.com/HangfireIO/Cronos) | Cron scheduling | MIT |
| [WebPush](https://github.com/web-push-libs/web-push-csharp) | Web Push notifications | MPL-2.0 |
| [SQLitePCLRaw](https://github.com/ericsink/SQLitePCL.raw) / Microsoft.Data.Sqlite | Brain index storage | Apache-2.0 / MIT |

## Frontend

| Component | Role | License |
|-----------|------|---------|
| [React](https://github.com/facebook/react) | Web UI | MIT |
| [three.js](https://github.com/mrdoob/three.js) | Orb / WebGL rendering | MIT |
| [marked](https://github.com/markedjs/marked) | Markdown rendering | MIT |
| [highlight.js](https://github.com/highlightjs/highlight.js) | Syntax highlighting | BSD-3-Clause |
| [Vite](https://github.com/vitejs/vite) / [Bun](https://github.com/oven-sh/bun) | Build tooling | MIT |

## Rendering / editing

| Component | Role | License |
|-----------|------|---------|
| [Obsidian](https://obsidian.md/) | Optional viewer for the memory vault (not bundled; the vault is plain markdown) | Proprietary (free) |
