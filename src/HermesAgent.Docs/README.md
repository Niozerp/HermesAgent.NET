# Hermes Agent — .NET Edition ☤

A complete **.NET 9** implementation of [NousResearch/hermes-agent](https://github.com/NousResearch/hermes-agent). Hermes is a self-improving AI agent capable of persistent memory and autonomous skill creation.

[![Build Status](https://img.shields.io/badge/.NET-9.0-blue.svg)](https://dotnet.microsoft.com/download)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)

## 📖 Extended Documentation

- [**Architecture**](./ARCHITECTURE.md) — How the orchestration loop and memory systems work.
- [**Tool Reference**](./TOOLS.md) — Detailed list of all 35+ capabilities.

---

## 🚀 Quick Start

### Prerequisites
- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
- An API Key for an LLM provider (OpenAI, Anthropic, or OpenRouter)

### Installation
```bash
git clone https://github.com/Niozerp/HermesAgent.NET.git
cd HermesAgent.NET
```

### Configuration
Hermes looks for configuration in several places. The easiest way to start is creating an `appsettings.json` or setting environment variables:

```json
{
  "Hermes": {
    "Llm": {
      "Provider": "openai",
      "Model": "gpt-4o",
      "ApiKey": "YOUR_SK_KEY"
    }
  }
}
```

### Running the Agent
**CLI (Interactive)**:
```bash
dotnet run --project src/HermesAgent.Cli
```

---

## 🛠️ Project Structure

| Project | Description |
|---|---|
| **HermesAgent.Core** | Models, Interfaces, and Configuration logic. No dependencies. |
| **HermesAgent.Agent** | The core orchestrator loop and LLM provider adapters. |
| **HermesAgent.Tools** | Implementation of system, web, and browser tools. |
| **HermesAgent.Memory** | SQLite/FTS5 implementation for conversation persistence. |
| **HermesAgent.Skills** | Markdown-based skill management system. |
| **HermesAgent.Cli** | Terminal-based TUI for local use. |

---

## 🧠 Self-Improvement Loop

Hermes is not just a chatbot; it learns.
1.  **Experience**: It performs a task using existing tools.
2.  **Reflection**: It evaluates if a pattern was successful.
3.  **Learning**: It creates a new `.md` skill in `~/.hermes/skills/`.
4.  **Growth**: In the next session, this skill is automatically loaded into its system prompt.

---

## 🧪 Testing

We use XUnit and FluentAssertions for robust testing:
```bash
dotnet test
```

## 🤝 Contributing

We welcome contributions! Please see our architecture guide before submitting PRs. We follow the Clean Code principles and require XML documentation for all public APIs.

## ⚖️ License

Distributed under the MIT License. See `LICENSE` for more information.

---
*Created with ❤️ by the team at Gravicode Studios.*
