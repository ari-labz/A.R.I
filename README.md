<img width="1812" height="868" alt="df027e78-f791-4199-b9d0-9ea10fb47813" src="https://github.com/user-attachments/assets/7190726f-cc06-4dc8-8f17-f91a1ef6d262" />


### A Local-First, Graph-Native Personal AI Assistant

**ARI** is a privacy-focused, locally hosted AI assistant designed to bridge the gap between frontier model capabilities and consumer hardware. Unlike traditional LLM wrappers, ARI uses an **Agentic Graph Retrieval** system, allowing it to reason over structured knowledge without the noise and latency of vector embeddings.

---

## 🧠 Core Architecture: The "Neural" Graph

Most AI assistants rely on **Vector RAG** (Retrieval-Augmented Generation), which searches for semantic similarity in unstructured text. This often leads to hallucinated context or irrelevant noise.

ARI takes a fundamentally different approach, mimicking human associative memory:

1.  **Explicit Knowledge Graph:** ARI's memory is stored as a curated note graph in Trilium Notes (wikilinks). Each note is a **neuron**; each link is a **synapse**.
2.  **Agentic Traversal:** Instead of passive vector search, the LLM acts as an **active agent**. It reads a note, understands the context, and *decides* which linked notes to fetch next.
3.  **Small-World Retrieval:** Leveraging the "Six Degrees of Separation" principle, ARI can recursively traverse its graph to find highly specific, high-signal information.

### Why This Matters
*   **High Signal, Low Noise:** ARI only retrieves data explicitly connected to the current topic, eliminating the "vibe matching" errors common in vector databases.
*   **Contextual Reasoning:** By actively choosing what to read, ARI simulates a chain-of-thought process, leading to deeper, more accurate answers.
*   **Minimal Infrastructure:** No Neo4j, no ChromaDB, no embedding models — just a self-hosted Trilium instance and a local LLM.

---

## 🚀 Key Features

### 1. Local-First & Private
*   Runs entirely offline on consumer hardware (e.g., MacBook M4 Max).
*   No data leaves your device. Your knowledge graph remains yours.

### 2. Scalable Intelligence
*   **Model Agnostic:** Currently powered by **Qwen3.6-27B**, ARI delivers approximately 60–70% of the reasoning capability of frontier models like Claude Sonnet, despite running locally.
*   **Continuous Learning:** ARI becomes smarter as your graph grows. The **Engram** agent automatically extracts facts from conversations and writes them to the graph, while **Refactor** continuously restructures it for optimal traversal.

### 3. Multi-Platform Access
*   **Discord Bot:** Seamless integration for daily communication.
*   **Web Interface:** A secure, locally hosted web panel for remote access or offline use.
*   **Voice Synthesis:** On-device text-to-speech output via the ARI.VoiceSynthesis module.

### 4. Future-Proof Design
*   Designed to integrate with file systems and GitHub for advanced coding assistance.
*   Modular architecture allows for easy upgrades to larger models (e.g., M3 Ultra) as needed.

---

## 🆚 ARI vs. Traditional AI

| Feature | Traditional Vector RAG | ARI (Graph-Native) |
| :--- | :--- | :--- |
| **Retrieval** | Semantic Similarity (Fuzzy) | Explicit Links (Precise) |
| **Context** | Often noisy/irrelevant | High-signal, curated |
| **Reasoning** | Passive search | Active, agentic traversal |
| **Maintenance** | Auto-indexed (Black Box) | AI-curated (Transparent) |
| **Hardware** | Requires GPU for embeddings | Runs on CPU/Standard GPU |

---

## 🛠️ Tech Stack

*   **Language:** C# (.NET)
*   **Model:** Qwen3.6-27B-Q6_K-mtp.gguf (via llama.cpp)
*   **Memory:** Trilium Notes knowledge graph (self-hosted)
*   **Interface:** ASP.NET (Web Panel) + Discord.Net
*   **Hardware:** Apple Silicon (M4 Max / M3 Ultra)

---

## 📈 Philosophy

> "Intelligence isn't just about parameter count; it's about access to relevant context."

ARI proves that you don't need a 70B parameter model to have a smart assistant — you need a **smart memory system**. By structuring knowledge explicitly, smaller models can perform complex reasoning tasks that usually require frontier-scale compute.

---

## 🤝 Contributing

This is a personal project designed for specific use cases. However, the architecture of **Agentic Graph Retrieval** is open for discussion and adaptation.
