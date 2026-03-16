# PhotoIQ

**Your photos, understood.**

PhotoIQ is a privacy-first, AI-powered photo management application for Windows. Every analysis runs locally on your machine — no cloud, no subscriptions, no data leaving your hands.

---

## What It Does

Most photo AI sees objects. PhotoIQ understands moments.

- **AI-powered tagging** — CLIP vision model analyzes your photos and generates semantic tags automatically
- **Natural language search** — find photos by describing what's in them, not by remembering filenames or dates
- **100% local processing** — all AI runs on your GPU via Ollama + LLaVA; your photos never leave your machine
- **Non-destructive** — original files are never modified

---

## Status

> 🚧 **Active development — pre-alpha**
> Core AI pipeline is functional. UI and full feature set are in progress.

| Feature | Status |
|---|---|
| CLIP-based image tagging | ✅ Working |
| Local LLaVA integration (Ollama) | ✅ Working |
| Natural language search | 🔄 In progress |
| Photo library management | 🔄 In progress |
| Windows installer | ⏳ Planned |

---

## Tech Stack

- **UI** — WPF / .NET 8 (Windows-first)
- **AI Vision** — [Ollama](https://ollama.com) + LLaVA (local inference)
- **Semantic Tagging** — CLIP ViT-B/32 via ONNX Runtime
- **Database** — SQLite

---

## Requirements

- Windows 10/11
- .NET 8 Runtime
- [Ollama](https://ollama.com) installed and running locally
- NVIDIA GPU recommended (RTX series tested); CPU fallback supported

---

## Getting Started

### 1. Clone the repo

```bash
git clone https://github.com/paulweihs/PhotoIQ.git
cd PhotoIQ
```

### 2. Install Ollama and pull LLaVA

```bash
ollama pull llava
```

### 3. Download CLIP model files

Place the following in `%LOCALAPPDATA%\PhotoIQ\models\`:

| File | Source |
|---|---|
| `clip-vit-base-patch32-vision.onnx` | Required by existing code |
| `clip-vit-base-patch32-text.onnx` | [HuggingFace: openai/clip-vit-base-patch32](https://huggingface.co/openai/clip-vit-base-patch32) (ONNX export) |
| `vocab.json` | Same HuggingFace repo |
| `merges.txt` | Same HuggingFace repo |

> Without the text model/vocab files the app still runs — AI tagging is simply skipped.

### 4. Build and run

Open `PhotoIQPro.sln` in Visual Studio 2022 and run.

---

## Privacy

PhotoIQ is built on a single principle: **your photos belong to you**.

- No account required
- No telemetry
- No internet connection needed after setup
- All AI inference runs on your local hardware

---

## Roadmap

- [ ] Natural language photo search
- [ ] Full photo library browser
- [ ] Batch import and tagging
- [ ] Express and Standard editions
- [ ] Windows installer / MSIX packaging

---

## License

Proprietary. All rights reserved. © 2025–2026 Paul Weihs.

This repository is publicly visible for transparency and portfolio purposes. It is not open source — see [LICENSE](LICENSE) for details.

---

*Built by a photographer, for photographers.*
