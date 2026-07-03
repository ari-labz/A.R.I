<img width="1812" height="868" alt="df027e78-f791-4199-b9d0-9ea10fb47813" src="https://github.com/user-attachments/assets/7190726f-cc06-4dc8-8f17-f91a1ef6d262" />

### A Local-First Personal Intelligence Framework

**A·R·I** is a locally hosted intelligence framework built on one core principle: **an AI can only be truly personal if it's truly private.**

The most useful assistant is one that knows everything about you — your habits, your history, your health, your relationships, your work. That's exactly the data no corporation should ever hold, and exactly the data cloud assistants can't ethically collect. A·R·I resolves the contradiction by being local-only: no corporate servers, no external calls, no telemetry. You own every byte. Because nothing leaves your machine, there is no privacy line to cross — so A·R·I can go as deep as you let her.

---

## 🧭 What A·R·I Actually Is

An honest framing, because the AI space is full of overclaims:

- **Not a novel memory paradigm.** Graph-based agentic memory exists elsewhere (GraphRAG, Zep, structured-note systems). A·R·I's implementation — an LLM actively traversing a self-curated Trilium wikilink graph — is a deliberate, minimal-infrastructure take on that idea, not an invention of it.
- **Not a frontier model.** It runs Qwen3.6-35B-A3B, a fast MoE model widely regarded as too lean for serious agentic work.
- **What it *is*:** an architecture built around a thesis — that deep personalization requires local-only privacy, and that a mid-tier model with decomposed responsibilities, curated memory, and automated verification can produce frontier-quality output. Most visibly in coding, where a two-agent plan→execute pipeline delivers frontier-competitive results on small and medium complexity tasks.

---

## 💪 Strengths

### 1. Deeply personal, by design
- A·R·I's memory is a persistent knowledge graph she writes and curates herself — the **Engram** agent extracts facts from every conversation; the **Refactor** agent restructures the graph for better recall.
- Over time she builds a genuine model of your life: the kind of understanding that would be invasive for a company to hold, but is simply *useful* when it never leaves your desk.
- Retrieval is agentic: she reads a note and *decides* which links to follow, rather than trusting embedding similarity. High signal, no vector DB, no embedding models.

### 2. Genuinely local and private
- Everything runs on-device. No cloud APIs, no external calls, no telemetry — the privacy guarantee isn't a policy, it's an architecture.
- Cross-platform, designed to run on anything from a workstation to a modest home server.

### 3. Frontier-level coding from a mid-tier model
The coding pipeline splits work across two specialized agents:
- **CodeArchitect** — reasons, explores the codebase, decomposes the task into ordered atomic steps, and orchestrates.
- **Coder** — a lean executor with no search tools and no room to drift: read the pinpointed range, apply the change, stop.

Combined with an automated build-verify-fix loop (compile → parse errors → targeted fix → rebuild), the pipeline produces answers comparable to frontier coding assistants on small-to-medium tasks — just slower. The insight isn't the model; it's that **tight responsibility decomposition substitutes for raw capability**.

### 4. Model-agnostic serving layer
- Multiple llama.cpp servers managed simultaneously, with live model hot-swapping — agents reference named servers, not hardcoded endpoints.

---

## ⚠️ Weaknesses & Trade-offs

Being honest about the costs:

- **Speed.** The plan→execute→verify loop is thorough, not fast. Hard tasks can take minutes where a frontier API answers in seconds.
- **Task ceiling.** Frontier-competitive holds for small and medium complexity. Large multi-file refactors and architecturally ambiguous tasks still favor bigger models.
- **Memory requires curation.** A graph-native memory is high-signal *because* it's curated — retrieval quality depends on graph quality. A neglected graph degrades gracefully, but it degrades.
- **Graph traversal trades recall for precision.** Explicit links can't surface a connection nobody wrote down; vector search sometimes can. A·R·I chooses precision.
- **Single-user by design.** A deeply personal AI is, by definition, not a multi-tenant product.

---

## 🆚 How It Differs From Other Approaches

| | Cloud assistants (ChatGPT, Claude) | Local wrappers (Ollama + UI) | GraphRAG-style systems | A·R·I |
| :--- | :--- | :--- | :--- | :--- |
| **Privacy** | Data leaves device | Local | Varies | Local-only, architectural |
| **How well it can know you** | Limited by what's ethical to collect | Not at all | Document-level | Everything you allow — it all stays home |
| **Memory** | Session/opaque | None or naive | Auto-built graph | Self-curated, agent-maintained graph |
| **Coding** | Frontier, fast | Raw model output | N/A | Frontier-competitive via decomposition, slower |
| **Infrastructure** | None (theirs) | Minimal | Graph/vector DB stack | Trilium + llama.cpp only |
| **Improvement over time** | No | No | Reindexing | Graph grows and restructures itself |

The differentiator isn't any single component — it's the combination: **a privacy architecture that makes deep personalization ethical, plus decomposed agent pipelines that make a local model capable of acting on it.**

---

## 🛠️ Tech Stack

- **Language:** C# (.NET)
- **Model:** Qwen3.6-35B-A3B MoE (via llama.cpp), hot-swappable
- **Memory:** Trilium Notes knowledge graph (self-hosted)
- **Interfaces:** ASP.NET web panel, Discord bot, client harness (remote filesystem access), on-device voice synthesis
- **Hardware:** Apple Silicon (developed on M4 Max)

---

## 📈 Philosophy

> "The AI that knows you best should be the one that answers to you alone."

Cloud assistants are capped not by intelligence but by trust — there is data you will never give them, and data they should never take. A·R·I removes that cap. Self-hosted, local-only, and personal to a degree no corporate product can ethically match — and an ongoing experiment in how far decomposition, verification, and curated context can push a small local model. The answer so far: further than expected.

---

## 🤝 Contributing

This is a personal project built for specific use cases, but the architecture — particularly the plan→execute coding pipeline — is open for discussion and adaptation.
