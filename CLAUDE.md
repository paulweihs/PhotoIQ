# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Run

```bash
# Build
dotnet build PhotoWell.sln

# Run (Windows only - WPF app)
dotnet run --project src/PhotoWell.Desktop/PhotoWell.Desktop.csproj

# Test
dotnet test src/PhotoWell.Tests/PhotoWell.Tests.csproj

# Single test
dotnet test src/PhotoWell.Tests/PhotoWell.Tests.csproj --filter "FullyQualifiedName~TestName"
```

Requires: Windows 10/11, .NET 8.0 SDK, Visual Studio 2022 ("`.NET desktop development`" workload for WPF designer).

## Architecture

Six-project solution with strict layering — dependencies flow inward toward Core:

```
PhotoWell.Desktop  →  PhotoWell.Services  →  PhotoWell.Core
PhotoWell.Desktop  →  PhotoWell.Data      →  PhotoWell.Core
PhotoWell.Desktop  →  PhotoWell.AI        →  PhotoWell.Core
PhotoWell.Desktop  →  PhotoWell.Common
```

**Core** — Domain models (`MediaFile`, `Tag`, `Person`, `Face`, `Collection`, `ExclusionRule`) and interfaces (`IImportService`, `IThumbnailService`, `IMediaFileRepository`, `IDriveService`). Zero external dependencies.

**Common** — Static `AppSettings` with data paths (`%LOCALAPPDATA%\PhotoWell\`).

**Data** — EF Core 8 + SQLite. `PhotoIQContext` configures all relationships. `MediaFileRepository` handles CRUD and filtered queries (by path, hash, favorites, unanalyzed). DB at `%LOCALAPPDATA%\PhotoWell\photoiq.db`.

**Services** — Three services:
- `ImportService` — hashes files (SHA-256), extracts EXIF via MetadataExtractor, deduplicates, persists
- `ThumbnailService` — generates 150px/400px/800px thumbnails via SixLabors.ImageSharp into `%LOCALAPPDATA%\PhotoWell\thumbnails\`
- `DriveService` — scans drives for media files (photos/video/RAW), filters default system exclusions plus user-defined rules

**AI** — `ClipEngine` loads `clip-vit-base-patch32-vision.onnx` via ONNX Runtime, preprocesses images (224×224, ImageNet normalization), returns 768-dim float embeddings. Model stored at `%LOCALAPPDATA%\PhotoWell\models\`.

**Desktop** — WPF + MVVM (CommunityToolkit.MVVM). DI wired in `App.xaml.cs`. Two main windows:
- `MainWindow` / `MainViewModel` — photo grid gallery with sidebar and details panel
- `ScanDrivesWindow` / `ScanDrivesViewModel` — drive scanning dialog with exclusion rules, result filtering, and batch import with progress

## Key Entity Relationships

- `MediaFile` ↔ `Tag` (many-to-many)
- `MediaFile` ↔ `Collection` (many-to-many)
- `MediaFile` → `Face` (one-to-many)
- `Face` → `Person` (many-to-one)
- `Collection` → `Collection` (self-referential parent/child)

## Import Pipeline

`DriveService.ScanForMediaAsync()` → `ImportService.ImportFolderAsync()` (hash + EXIF + dedupe) → `ThumbnailService.GenerateThumbnailsAsync()` → CLIP tagging → llama3.2-vision analysis → `MediaFileRepository.AddAsync()`

**Vision model:** `llama3.2-vision` via Ollama (confirmed over moondream after 8-photo gate evaluation). Prompt v5 — per-person arm/hand decomposition. Gates: G1 no hallucination ✅, G2 no activity inversion ✅, G3 no filler ✅, G4 no inferred relationships ⚠️ (prompt v5 targets this), G5 determinism ✅, G6 gesture detection ✅.

AI steps degrade silently — a tagging or vision failure never aborts an import.

## AI Chat Assistant

"✨ Ask AI" button in the toolbar (or Ctrl+Enter from the search box) opens a sliding `ChatPanel` below the top bar.

**Architecture:**
- `IChatAssistantService` / `ChatAssistantService` (Services layer) — multi-turn conversation via Ollama `/api/chat` with tool calling. Agentic loop: up to 6 iterations per user turn.
- `IAssistantActions` (Core interface) — UI dispatch contract so Services layer can update the gallery without a circular dependency. `MainViewModel` implements it. Wired in `App.xaml.cs` via `chatService.SetActions(mainVm)` after `mainWindow.Show()`.
- `ChatViewModel` (Desktop) — `ObservableCollection<ChatMessageViewModel>`, send/clear commands, 120 s timeout, thinking indicator.
- `ChatPanel.xaml` / `ChatPanel.xaml.cs` — bubble chat UI (user right/purple, assistant left/dark). Auto-scrolls on new messages. `ChatMessageTemplateSelector` picks user vs assistant template.

**7 tools the assistant can call:**

| Tool | What it does |
|---|---|
| `search_photos` | Sets `SearchQuery` + runs `ExecuteSearchCommand` |
| `filter_by_person` | Looks up `Person` by name, union of confirmed + high-confidence (≥0.7) faces, replaces `MediaFiles` |
| `filter_by_date` | Filters gallery to a year or year+month range |
| `clear_filters` | Resets person filter, search, restores full library |
| `get_library_stats` | Returns total/analyzed/favorite counts from DB |
| `open_photo` | Selects a photo by filename in the current gallery view |
| `show_people` | Navigates to the People sidebar view |

**Model:** `llama3.1:8b` default (configurable in Settings → AI Engine → Chat Assistant Model). `llama3.2:3b` is the lighter/faster alternative. Model is auto-pulled via `OllamaSetupService.EnsureChatModelAsync` as a fire-and-forget background task after the vision model reaches Ready — no separate install step required.

**Tier gating:**
- Express: "Ask AI" button shown at 65% opacity. Clicking opens the chat panel and shows an inline upsell message. The button is never hidden — the tooltip and click handler both communicate the Standard requirement.
- Standard: full chat functionality.
- `UserPreferences.Current.IsExpressMode` is the gate; checked in `ChatViewModel.SendAsync`.

**Key files:**
- `src/PhotoWell.Core/Interfaces/IChatAssistantService.cs` — interface + `IAssistantActions`
- `src/PhotoWell.Services/Chat/ChatAssistantService.cs` — tool loop implementation
- `src/PhotoWell.Desktop/ViewModels/ChatViewModel.cs`
- `src/PhotoWell.Desktop/Views/Controls/ChatPanel.xaml[.cs]`

## Product Tiers

| | Express | Standard |
|---|---|---|
| AI engine | CLIP/ONNX (CPU only) | llama3.2-vision via Ollama (GPU) |
| Library size | ~25,000 images | Unlimited |
| Search | Tag-based | Full natural language |
| AI Chat | — (upsell shown) | ✓ llama3.1:8b |
| People detection | — | ✓ |
| Price | ~$39 | ~$79 |

Upgrade prompt appears **only** when a user hits an Express ceiling — never on startup or a timer.

## Non-Negotiable Rules

- **Zero cloud AI calls.** All inference is local/offline (ONNX Runtime + Ollama). No Azure/Google/AWS Vision.
- **Never modify originals.** Preprocess to a temp JPG for analysis; delete temp after. Originals are read-only.
- **Perpetual license only.** No subscription model. No SaaS. Tiers are Express and Standard — no third tier.
- **Never auto-delete files.** Every destructive action requires explicit user confirmation.
- Add new interfaces in `PhotoWell.Core` before implementing in Services/AI.
- Register all new services in `App.xaml.cs`. Run `dotnet build` after every meaningful change.
- Add EF Core migrations for any schema changes — never hand-edit the `.db` file.

## Build Troubleshooting

OneDrive syncs this folder and causes DLL file locks during builds. If `MSB3027` errors appear:
1. Kill any running PhotoIQ process
2. Delete `src/PhotoWell.Desktop/bin/` and rebuild

For XAML type resolution errors: `dotnet build --no-incremental`

## Quick Reference

```bash
dotnet build --no-incremental          # full rebuild
dotnet ef migrations add Name --project src/PhotoWell.Data
del %LOCALAPPDATA%\PhotoWell\photoiq.db   # reset DB after schema changes
```

Model files: `%LOCALAPPDATA%\PhotoWell\models\` (vocab.json, merges.txt, vision ONNX, text ONNX)
Ollama: `http://localhost:11434` — check with `curl http://localhost:11434/api/tags`

## Tech Stack

| Concern | Library |
|---|---|
| UI | WPF (.NET 8, Windows-only) |
| MVVM | CommunityToolkit.MVVM 8.4 |
| ORM | EF Core 8 + SQLite |
| Image processing | SixLabors.ImageSharp 3.1 |
| EXIF metadata | MetadataExtractor 2.9 |
| ML inference | ONNX Runtime 1.24 |
| DI | Microsoft.Extensions.DependencyInjection 8 |
| Tests | xUnit 2.9 |

## Product Philosophy

    PhotoIQ Pro is an **end-user product**. It must be fully self-contained:

    - No manual codec or extension installs (no Microsoft Store prompts, no dcraw, no runtime dependencies the user must
     find themselves)
    - No developer tooling required to run
    - All format support must ship inside the app (bundled NuGet packages, embedded native DLLs, or pure .NET
    implementations)
    - If a feature cannot be delivered self-contained, it either uses best-effort fallback with a clear in-app
    explanation, or is not shipped

    When evaluating solutions: prefer a larger NuGet dependency that just works over any approach that puts burden on
    the user.