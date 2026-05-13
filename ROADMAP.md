# PhotoIQ Pro — Roadmap

## Pending / Backlog

### Selection & Batch Operations
- **Multi-select in gallery** — Ctrl+click to toggle individual photos, Shift+click to select a range
- **Re-analyze selected** — toolbar button to re-run AI on selected photos only (complement to existing single-photo and re-analyze-all)

### EXIF & Location
- **EXIF backfill** — re-extract aperture, shutter speed, ISO, focal length, dimensions for photos already in the library (imported before EXIF fix)
- **Geo location** — extract GPS coordinates from EXIF (GPSLatitude, GPSLongitude), store on MediaFile, display in sidebar and photo viewer; consider reverse geocoding to a place name (offline or on-demand)

### Search
- **Natural language search** — CLIP text embeddings for Standard tier search bar (e.g. "people at a party", "red car outdoors")

### AI / Vision
- **Gesture detection** — dedicated second vision pass for gestures (bunny ears, peace sign, etc.); deferred until core description quality is stable
- **minicpm-v eval** — run evaluation harness against minicpm-v (pulled, ready); compare against llama3.2-vision across all 8 gates

### UI
- **Stat label context** — progress window labels ("Imported", "Skipped") should change to match the operation (Re-analyzing vs Importing)

## Completed
- Import progress modal
- Photo viewer / lightbox with keyboard navigation
- Re-analyze: single photo + batch with confirmation
- CLIP tag vocabulary cleanup (false positive tags removed/tightened)
- indoor/outdoor mutual exclusion
- Work-context suppression (working/renovation suppresses dancing/running/sports)
- `children playing` removed (demographic fabrication)
- EXIF extraction: aperture, shutter speed, ISO, focal length, dimensions
- Model evaluation harness (PhotoWell.Eval) — 6 gates, SQLite
- Vision model selection: llama3.2-vision promoted (moondream eliminated for fabrication)
- Prompt v4: physical contact rule (unambiguous-only), bunny ears Gate 6 PASS
- Vision logging (vision.log)
- EF Core reload fix for re-analyze
- Version number in status bar
- Selectable text (filename, AI description, file path) in main window and photo viewer
- File path field in sidebar and photo viewer (left-aligned, resets on navigation)
- Progress window stat labels context-aware (Imported/Re-analyzed, Already in library/Skipped)
- Smart App Control: disable on dev machine (unsigned build output blocked SAC)
