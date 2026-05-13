# PhotoIQ Pro — MVP Assessment
_Generated 2026-04-06_

## Summary

PhotoIQ Pro is shippable as an Express MVP today. The Standard tier requires one gate check (Ollama stability under real user load) before it's ready for sale. The product's core differentiator — subject recognition ("That's Bubba") — is not yet implemented and should be treated as a v1.1 milestone, not a v1.0 blocker.

---

## Tier Definitions (from CLAUDE.md)

| | Express ($39) | Standard ($79) |
|---|---|---|
| AI engine | CLIP/ONNX (CPU) | llama3.2-vision via Ollama |
| Library size | ~25,000 images | Unlimited |
| Search | Tag-based | Full natural language |

---

## Express MVP Checklist

### ✅ Core — Done

- [x] **Import pipeline** — SHA-256 hash, EXIF extraction (MetadataExtractor), deduplication, SQLite persistence
- [x] **Thumbnail generation** — 150/400/800px via SixLabors.ImageSharp; no external codec required
- [x] **CLIP tagging** — AI tags generated on import using ONNX Runtime (CPU); no Ollama dependency
- [x] **Photo gallery** — scrollable grid, adjustable thumbnail size, loading states
- [x] **Tag-based search** — FTS5 full-text search on tags, description, filename, camera, date, folder
- [x] **Favorites** — star toggle, Favorites view in sidebar
- [x] **Duplicate detection** — exact hash and RAW/JPEG pair grouping with bulk Keep actions
- [x] **Scan Drives** — discover media on any drive, respect exclusion rules, batch import with progress
- [x] **Cancel import + resume** — cancel preserves queue; resumes on next launch with user confirmation
- [x] **Exclusion rules** — folder exclusion (pattern or full path); removes already-imported photos
- [x] **Libraries & Albums** — create, rename, delete; add photos via picker; album view in sidebar
- [x] **Folder watcher** — auto-imports new files added to known library folders
- [x] **Detail panel** — filename, date, dimensions, file size, path, EXIF exposure, GPS coordinates, tags, caption
- [x] **GPS display** — shows decimal degrees with "Map" button (opens browser maps) when EXIF GPS present
- [x] **Related photos** — "Taken nearby" strip showing photos within ±30 min of selected photo's timestamp
- [x] **Context menu** — View Full Size, Open in Explorer, Re-analyze, Add to Favorites, Add to Album, Copy to Folder, Send To, Exclude Folder, Remove from Library, Delete Permanently
- [x] **Photo viewer** — full-screen viewer with previous/next navigation, favorite toggle
- [x] **Crash recovery** — photos left in Processing state reset to Pending on startup; heal runs at t+10s
- [x] **Startup experience** — splash screen, CLIP model pre-warm, setup gate (Ollama check)
- [x] **Settings** — Ollama URL, vision model dropdown, CLIP models path, express mode toggle, thread count, thumbnail quality, exclusion manager
- [x] **Dark theme** — consistent dark UI with accent color
- [x] **Offline drive handling** — badges on gallery items, warnings in detail panel, commands disabled
- [x] **Error handling** — global DispatcherUnhandledException handler; user-visible error dialog + log entry

### ⚠️ Needs Verification Before Ship

- [ ] **CLIP model cold-start on new machine** — model files must be present in the installer; verify the path resolution works with the default `%LOCALAPPDATA%\PhotoWell\models\` layout on a clean Windows install
- [ ] **Express mode library limit (25,000)** — limit is defined in CLAUDE.md but the enforcement gate is not visible in the codebase; verify an upgrade prompt appears at the ceiling, not before
- [ ] **Test suite coverage** — only `CommonTests.cs` and `ModelTests.cs` exist; import pipeline, repository, and search paths have no automated tests; risk is present for regression
- [ ] **Installer / distribution** — no evidence of an MSIX/Inno Setup/WiX project; self-contained .exe must be verified on Windows 10 22H2 and Windows 11 without .NET runtime pre-installed

### ❌ Not Done — Blockers?

None for Express. The missing features below are either Standard-only or post-MVP.

---

## Standard MVP Checklist

All Express items above, plus:

### ✅ Core — Done

- [x] **Vision descriptions** — llama3.2-vision via Ollama; prompt v5 with gate evaluations (G1–G6 complete)
- [x] **Natural language search** — CLIP semantic embeddings + FTS5 hybrid; "photos of children playing outdoors"
- [x] **Description editing** — inline edit with user-override preserved across re-analysis
- [x] **Caption editing** — separate user caption (max 200 chars); preserved separately from AI description
- [x] **Outdated description detection** — badge + count when model/prompt version changes; bulk re-analyze
- [x] **Re-analyze** — single photo, all photos, or outdated only; background queue with cancel/resume
- [x] **Analysis metrics** — per-photo timing (preprocessing, inference, total), model name, GPU flag
- [x] **Metrics dashboard** — `MetricsViewModel` with batch stats
- [x] **Vision queue priority** — selected photo jumps to front of background vision queue

### ⚠️ Needs Verification Before Ship

- [ ] **Ollama memory pressure** — llama3.2-vision holds ~5 GB VRAM; verify behavior with concurrent system load; consider adding a configurable concurrency limit (currently 1 at a time via serial queue)
- [ ] **Vision worker responsiveness** — Dispatcher now uses `InvokeAsync` at `Background` priority; needs verification under sustained queue load that UI stays responsive
- [ ] **Prompt v5 gate G4** — "no inferred relationships" gate was ⚠️ at last evaluation; verify improvement before ship

### ❌ Not Done — Post-MVP

- [ ] **Subject recognition ("That's Bubba")** — the product's core ADR feature; click subject → type name → tag all visual matches in library. Not implemented. **Plan for v1.1.**
- [ ] **Face / person tagging** — no face detection implemented; the `Face` and `Person` domain models exist but are unused
- [ ] **Reverse geocoding** — GPS coordinates displayed as decimal degrees; city/country lookup not implemented (would require Nominatim or offline DB)
- [ ] **Video support** — `MediaType.Video` enum exists and is detected, but thumbnail generation and description for video are unverified
- [ ] **RAW format completeness** — RAW is preprocessed to JPEG before analysis; verify coverage across Canon CR2/CR3, Nikon NEF, Sony ARW, Fuji RAF on a real install
- [ ] **Export / print** — no export pipeline; no print dialog
- [ ] **Slideshow mode** — not implemented

---

## Risk Register

| Risk | Severity | Mitigation |
|---|---|---|
| No installer project | HIGH | Blocks ship; must create MSIX or Inno Setup before any public release |
| Minimal test coverage | HIGH | Import pipeline regression could go undetected; add at minimum 3 smoke tests for import + FTS |
| Express library limit not enforced | MEDIUM | Users may exceed 25k without seeing upgrade prompt; damages tier economics |
| Ollama not pre-installed on user machines | MEDIUM | Setup wizard already handles this; verify the download link and model pull UX on first-run |
| Subject recognition is the ADR | LOW (for v1.0) | Product can ship without it; market as "coming soon" |

---

## Recommended Ship Order

1. **Express** — ship as soon as installer + library limit enforcement are verified
2. **Standard** — ship 2–4 weeks after Express with Ollama first-run polish

The codebase is solid. The primary blocker to any public release is the missing installer. Everything else is either done or a post-v1.0 concern.
