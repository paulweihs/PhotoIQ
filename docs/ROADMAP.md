# PhotoWell — Roadmap & Backlog

Items are grouped by theme. Each entry notes the source request, current status, and implementation notes for when the work is picked up.

---

## UI / UX

### Thumbnail pending-regen indicator
**Source:** User request 2026-04-26
**Priority:** High
**Status:** In progress
**Description:** Photos whose `AnalysisStatus == VisionPending` (or `ThumbnailStatus == Pending`) should show a small overlay badge in the gallery thumbnail so the user can see what is still queued.
**Implementation notes:**
- Add a `DataTrigger` on `AnalysisStatus` in the gallery `ItemTemplate` (MainWindow.xaml).
- Show a small spinner or clock icon overlaid on the thumbnail `Border`.
- Reuse the existing `AI` badge position (bottom-left) with a distinct colour/icon.

---

### Sort button tooltips
**Source:** User request 2026-04-26
**Priority:** Low
**Status:** Done 2026-04-26
**Description:** All five sort buttons now have rich multi-line `<Button.ToolTip>` elements. The Date button explains the `DateTaken ?? DateImported` scan fallback. All buttons describe what they sort on and mention the Shift-click secondary-sort behaviour.

---

### Contact sheet
**Source:** User request 2026-04-26
**Priority:** Medium
**Status:** Not started
**Description:** Generate a printable grid of thumbnails with captions (filename, date, tags) for selected photos or an entire album.
**Implementation notes:**
- New `ContactSheetWindow` with configurable columns (2/4/6), paper size, caption fields.
- Use WPF `PrintDialog` + `FixedDocument`.
- Consider export to PDF via `XpsDocument`.

---

## Analysis / AI

### Retry and priority queue for "Re-analyze with AI" during Update Outdated
**Source:** User request 2026-04-26
**Priority:** High
**Status:** Done 2026-04-27
**Description:** While "Update Outdated" is running, clicking "Re-analyze with AI" on a selected image inserts it at the front of the batch queue and skips it when the normal batch reaches it.

---

### Vision "no result" — understand and reduce frequency
**Source:** User request 2026-04-26
**Priority:** High
**Status:** Partially addressed (logging improved 2026-04-26)
**Description:** `ParseResponse` now logs the specific rejection reason (empty, loop, too short). Next step is to analyse the vision.log to find the most common cause and address it at the prompt or pipeline level.
**Implementation notes:**
- Run `grep "final reason" vision.log | sort | uniq -c | sort -rn` periodically.
- Common causes and fixes:
  - **too short (<15 words):** Prompt needs stronger instruction to write at least 2 full sentences.
  - **repetition loop:** Model is context-saturated; reduce image size sent or add `[INST]` stop token.
  - **empty response:** Ollama timed out or returned HTTP error — add explicit timeout + error code logging in `OllamaClient`.

---

### Perceptual-hash dedup for AI descriptions
**Source:** User request 2026-04-26
**Priority:** Medium
**Status:** Not started
**Description:** When importing or re-analyzing, compute a perceptual hash (pHash) for each image. If a matching hash already exists in the library, reuse the existing AI description instead of calling Ollama again. Saves GPU time for duplicates and near-duplicates (e.g., burst sequences).
**Implementation notes:**
- Add `PerceptualHash` (ulong) column to `MediaFile` via EF migration.
- Compute using `SixLabors.ImageSharp` DCT-based pHash at import time.
- Hamming distance ≤ 8 → consider "same scene" → copy description + tags.
- Creates a soft 1-to-many: one description shared by N photos with matching hash.
- Decision: keep per-photo tags (user may have different tags on duplicates), share only `AiDescription`.

---

### Adult/child confusion in descriptions
**Source:** User request 2026-04-26
**Priority:** Medium
**Status:** Deferred pending more minicpm-v harness data
**Description:** The vision model sometimes confuses adults and children (or swaps the dominant subject when both are present). Options:
1. Prompt instruction: "State the number of people (e.g. 'two people') rather than guessing ages."
2. Post-processing: Replace age-guessing phrases with neutral counts where detectable.
3. Wait for harness n≥200 run to see if minicpm-v has this problem at the same rate as llama3.2-vision.
**Implementation notes:** See `AppSettings.VisionAnalysisPrompt` and `TextNormalizer`.

---

## Data / Database

### Store PhotoWell version per library record
**Source:** User request 2026-04-26
**Priority:** Medium
**Status:** Not started
**Description:** Each `MediaFile` row should record which version of PhotoWell imported/analyzed it. Enables upgrade migrations and export compatibility checks.
**Implementation notes:**
- Add `ImportedWithVersion string?` and `AnalyzedWithVersion string?` columns to `MediaFile`.
- EF migration: `ALTER TABLE MediaFiles ADD COLUMN ImportedWithVersion TEXT`.
- Populate `ImportedWithVersion = AppSettings.Version` at import time.
- Populate `AnalyzedWithVersion = AppSettings.Version` at analysis time.
- Use in export (see below) to flag records from older schema versions.

---

## Export / Sharing

### Library export and backup
**Source:** User request 2026-04-26
**Priority:** High
**Status:** Not started
**Description:** Two related but distinct operations:
1. **Backup** — ZIP of `photowell.db` + `thumbnails/` folder. Point-in-time snapshot. No photos copied (they stay on disk at original paths).
2. **Share/export** — Creates a portable bundle: `photowell.db` + thumbnails + a manifest JSON mapping original paths to relative paths. The recipient can import this bundle into their PhotoWell to get all descriptions, tags, faces, albums.
**Implementation notes:**
- New `ExportService` with `CreateBackupAsync(string destPath)` and `CreateShareBundleAsync(IReadOnlyList<Guid> photoIds, string destPath)`.
- Use `System.IO.Compression.ZipArchive` for packaging.
- Share bundle schema: `{ "version": "0.2.3", "photos": [ { "hash": "...", "description": "...", "tags": [...] } ] }`.
- Import side: `ImportBundleAsync` matches by file hash, applies descriptions/tags if the photo already exists.
- Version field (see above) enables forward-compatibility checks.

---

## Face Recognition

### Face recognition — remaining gaps
**Source:** User request 2026-04-26
**Priority:** Medium
**Status:** Partially implemented
**Description:** The ONNX face detection and embedding pipeline is in place. The face-naming popup and sidebar are implemented. Remaining gaps:
1. **Auto-cluster tuning:** The Hamming/cosine threshold for auto-linking faces to persons needs calibration against a real photo set.
2. **"Who is in this photo?" prompt:** Currently shown once per photo; needs a "Never ask again for this person" option.
3. **Person filter in search:** Clicking a person in the People view filters the gallery — this works. Ensure the filter survives a LoadAsync triggered by background analysis.
4. **Manual unlink:** The "Unlink face from person" path exists in the repo but has no UI entry point other than the face popup.

---

## Geotagging

### Map display for geotagged photos
**Source:** User request 2026-04-26
**Priority:** Low
**Status:** Not started
**Description:** Show geotagged photos on a map. `MediaFile.Latitude`/`Longitude` fields and a `(Latitude, Longitude)` DB index exist, and `GetNearbyLocationAsync()` (Haversine) is implemented in the repo — but **GPS EXIF extraction is not yet wired up**, so the fields are always null today.
**Implementation notes:**
- First prerequisite: extract GPS EXIF tags in `ImportService.ExtractMetadata()` (see Reverse Geocoding entry below — both share this step).
- A "Show on Map" button opening `https://maps.google.com/?q=lat,lon` in the default browser is a one-liner once coordinates are populated.
- A full embedded map (WebView2 + OpenStreetMap/Leaflet) is a larger stretch goal.
- Consider a "Map" gallery view that plots all geotagged photos on an interactive map.

---

### Reverse geocoding and manual location (v2)
**Source:** User request 2026-05-02
**Priority:** Medium
**Status:** Designed — not started
**Description:** Convert GPS coordinates to human-readable place names (address/neighborhood/city/country) and display them in the metadata panel. For photos without GPS, allow the user to set a location manually. Must be cross-platform (Windows, macOS, Linux — no Windows-only APIs).

**New MediaFile columns (migration required):**
- `LocationName TEXT NULL` — formatted display string
- `LocationSource INTEGER NOT NULL DEFAULT 0` — `0=None, 1=GpsAuto, 2=Manual`

**Address format — assembled from components, degrade gracefully:**
```
"412 NW 10th Ave, Pearl District, Portland, OR, US"   ← full (online)
"Pearl District, Portland, OR, US"                    ← no street
"Portland, OR, US"                                    ← no neighborhood / offline
"Oregon, US"                                          ← rural, no nearby city
```

**Geocoding service — two-tier, cross-platform:**
| Tier | Provider | Online? | Key? | Detail |
|------|----------|---------|------|--------|
| Primary | Nominatim (OpenStreetMap) | Online | None | Full address |
| Offline fallback | Embedded GeoNames KD-tree | Offline | None | City-level |

- `IReverseGeocodingService` interface (swappable)
- `NominatimGeocodingService` — HTTP, 1 req/sec rate limit, structured address assembly
- `GeoNamesOfflineGeocodingService` — bundles `cities1000.txt` (~8 MB gzip); KD-tree nearest-neighbor lookup; city + region + country only
- `CompositeGeocodingService` — tries Nominatim, catches network failure, falls back to GeoNames

**Pipeline:**
1. `ImportService.ExtractMetadata()` — add GPS EXIF extraction (shared with map feature above)
2. `GeocodingBackfillService` — background batch: photos with lat/lon but null `LocationName`; rate-limited to respect Nominatim ToS
3. `AppSettings.GeocodeOnImport` (bool, default `false`) — power-user toggle to geocode immediately at import time
4. `ImportService.BackfillGeocodingAsync()` — manual trigger exposed in Settings page

**Manual location UI:**
- Metadata panel: "Set location…" link when `LocationName` is null
- Typeahead search: fires Nominatim search-by-name as user types, shows dropdown of candidates
- On pick: sets `LocationName`, `LocationSource = Manual`; lat/lon remain null (no GPS)
- GPS photos with auto-geocoded name: show edit icon — user override sets `LocationSource = Manual`

**Settings additions:**
- `GeocodeOnImport` toggle (off by default, clearly labeled as power-user / slow-import warning)
- "Re-geocode library" button triggers `BackfillGeocodingAsync`

---

### Keep selected photo visible on window resize
**Source:** User request 2026-04-27
**Priority:** Medium
**Status:** Backlog
**Description:** When the user resizes the main window, the VirtualizingWrapPanel re-flows columns and the currently-selected thumbnail can scroll out of view. The gallery should re-scroll to keep it visible after each resize.
**Implementation notes:**
- Handle `SizeChanged` on the gallery's `ItemsControl` (or `ScrollViewer`) in `MainWindow.xaml.cs`.
- Debounce with a short timer (~200 ms) — `SizeChanged` fires on every pixel during live resize.
- On the debounced tick, call `ScrollIntoViewRequested?.Invoke(SelectedMediaFile)` via `MainViewModel`.
- Alternatively expose a `KeepSelectionVisible()` method on the view and call it from the handler without going through the VM.
- Must be a no-op when `SelectedMediaFile == null` to avoid exceptions on startup.

---

## Infrastructure

### ETA display in progress panels
**Source:** User request 2026-04-26
**Priority:** High
**Status:** Done 2026-04-26
**Description:** `BatchRateText` now shows `"ETA 4m 32s"` alongside photos/min and avg seconds/photo.

### Grammar: compound predicate agreement after gender neutralisation
**Source:** User request 2026-04-26
**Priority:** High
**Status:** Done 2026-04-26
**Description:** `"and is wearing"` → `"and are wearing"` after `he/she → they` substitution. Fixed in `TextNormalizer.NeutraliseGender`.

### Vision rejection reason logging
**Source:** User request 2026-04-26
**Priority:** High
**Status:** Done 2026-04-26
**Description:** Each rejected response now logs the specific reason (empty, loop detected N words, too short N words). Enables analysis of failure distribution from `vision.log`.
