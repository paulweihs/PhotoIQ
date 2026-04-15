# PhotoIQPro UI Review
_Generated 2026-04-06_

## Executive Summary

| Window | Status | Key Issues | Priority |
|--------|--------|-----------|----------|
| **MainWindow** | Needs Work | Missing disabled states on buttons during loading; complex multi-selection logic; hardcoded colors in theme; unclear empty states | High |
| **ScanDrivesWindow** | Ready | Well-structured with good edit modes and state management; minor hardcoding concerns | Low |
| **FindDuplicatesWindow** | Needs Work | Delete confirmation missing; Delete button only enabled when files marked (could confuse users); no undo mechanism | High |
| **SettingsWindow** | Needs Work | Theme inconsistency vs other windows; no save validation feedback; unclear field requirements | Medium |
| **PhotoViewerWindow** | Ready | Excellent keyboard navigation and accessibility; clean metadata display; good error states | Low |
| **SetupWindow** | Ready | Minimal but effective splash screen; error handling present | Low |

---

## Detailed Findings by Window

### 1. MainWindow.xaml

**Purpose:** Primary application window serving as the photo library browser with search, filtering, multi-selection, and detailed metadata display.

**Key Interactions:**
- Search box with semantic search indicators
- Multi-action toolbar (Scan Drives, Duplicates, Import, Re-analyze, Remove, Delete)
- Left sidebar: Navigation (All Photos, Favorites, Albums, AI Views, Insights, Tools, Settings)
- Center: Photo gallery grid with lazy-loaded thumbnails
- Right sidebar: Photo details (metadata, EXIF, description, tags, related photos)
- Top bar: Progress/status bar with cancel capability
- Context menus on photos with various actions

**UX Issues & Missing Affordances:**

1. **Button State Management During Operations**
   - Multi-action buttons (Re-analyze, Remove, Delete) use `Visibility` binding tied to `HasMultiSelection`
   - No explicit disabled state while operations are running
   - Users may expect buttons to gray out during processing
   - **Action:** Add `IsEnabled` binding to prevent repeated clicks during active operations

2. **Hardcoded Color Values in Theme**
   - Static resource colors defined in MainWindow (e.g., `#1a2a1a`, `#2d5a2d` for metrics borders)
   - Metrics panel background/colors are not part of theme resources
   - Makes theme switching difficult
   - **Action:** Move all colors to centralized theme resources

3. **Empty State Clarity**
   - "Your library is empty" shows buttons (Scan Drives, Import Folder, Import Files)
   - "No results" state shows "Clear filter" button
   - These are clear, but missing explicit guidance on "no selected photo" state in right sidebar
   - Right sidebar visibility toggles on `HasSelection` but no empty placeholder shown

4. **Semantic Search Indicators**
   - Two hint borders shown conditionally: one for active semantic search, one for unavailable CLIP
   - These appear in the search box area but only visible after searching
   - New users won't discover semantic search feature
   - **Action:** Add help tooltip or icon to search box explaining semantic search capability

5. **Navigation State Inconsistency**
   - Album active state uses `#1e3a5f` background + `#89B4FA` foreground
   - Main nav buttons (All Photos, Favorites) use `AccentBrush` background + white foreground
   - Subtle but inconsistent visual language
   - **Action:** Standardize active state styling across all navigation buttons

6. **Multi-Selection Visual Feedback**
   - Dim border + bright border + checkbox overlay system works but is complex
   - Uses three different signals: `BorderBrush`, opacity, and `IsInCollection` converter
   - May be confusing for users to distinguish selected-for-detail vs. selected-for-batch
   - **Action:** Consider simplifying or adding labels: "Viewing" vs. "Selected for batch action"

7. **Disabled Album Count Display**
   - "Tags" and "People" buttons marked as disabled with opacity 0.45
   - Users may not realize these are coming features vs. errors
   - **Action:** Add "(Coming soon)" label or use different visual treatment

8. **Edit Mode Keyboard Bindings**
   - Caption & Description edit modes have `KeyBinding` for Enter (Save) and Escape (Cancel)
   - Add Tag uses `OnNewTagBoxKeyDown` code-behind handler
   - Inconsistent approach between sections
   - **Action:** Standardize all inline edits to use `KeyBinding` in XAML

9. **Metrics Panel No Data State**
   - Shows "No analysis data yet" text clearly
   - But live batch status shown above when data exists
   - Inconsistent ordering: batch status before data cards
   - **Action:** Anchor batch status at top or bottom consistently

10. **Offline Photo Indication**
    - Amber overlay + offline badge system works
    - But offline drive root shown in tooltip only (not visible by default)
    - Status button in status bar ("___ files offline") requires click to see details
    - **Action:** Consider adding count/summary in a collapsible panel or persistent indicator

**Keyboard Accessibility:**
- ✓ `Ctrl+A` for Select All
- ✓ `Escape` for Clear Selection
- ✓ Tab order implicit from XAML structure
- ✓ Photo navigation (arrow keys) delegated to PhotoViewerWindow
- ✗ No keyboard shortcut to open Scan Drives, Duplicates, or Import
- ✗ No keyboard shortcut to toggle favorite (only mouse right-click menu or button click)
- **Action:** Add keyboard shortcuts for frequent actions (Ctrl+S for Scan, Ctrl+D for Duplicates, Ctrl+F to toggle favorite)

**Empty/Error States:**
- ✓ Library empty: Shows helpful guidance with action buttons
- ✓ No search results: Shows search icon + message + clear button
- ✓ No selection: Right sidebar collapses (implicit via `HasSelection` binding)
- ✓ Offline files: Shows amber overlay on thumbnails + status indicator
- ✗ Missing: "Re-analyze all" running state feedback beyond progress bar
- ✗ Missing: "No tags available yet" state if user tries to search by tag (coming feature)

**Data-Binding Issues:**
- ✓ Most UI bound correctly via `{Binding PropertyName}`
- ✓ One-way bindings used for display-only fields (FilePath, FileName)
- ✓ Converters used appropriately (IsInCollection, GrayscaleImage, InverseBool)
- ✗ `Tag` attribute on SearchBox contains hardcoded placeholder text (should be bound to `SearchPlaceholderText` property)
- ✗ "Add a tag…" placeholder in NewTagBox hardcoded instead of bound

---

### 2. ScanDrivesWindow.xaml

**Purpose:** Multi-tab interface to scan drives for photos, configure exclusions, and import results.

**Key Interactions:**
- **Tab 1 (Drives):** ListBox of drives with checkboxes; Refresh button
- **Tab 2 (Exclusions):** Add/Edit/Remove exclusion paths; inline edit mode for each item
- **Tab 3 (Results):** Summary cards; folder list with selection checkboxes; Import button
- Toolbar: Select/Deselect all; Sort by (Path, Files, Size)
- Status bar: Current progress, scan summary
- Bottom: Start Scan, Cancel Scan, Close buttons

**UX Strengths:**
- ✓ Tab interface clear and logical (Drives → Exclusions → Results)
- ✓ Excellent edit mode for exclusions (inline TextBox with Save/Cancel/Edit/Remove)
- ✓ Good visual feedback: Read view vs. Edit view with appropriate button visibility
- ✓ Type badges ("Full Path" / "Folder Name") help users understand exclusion types
- ✓ Status badges (Excluded, All imported, Partially imported) on result items clear
- ✓ Context menu on results for quick exclude/include actions
- ✓ Sort buttons show indicator text (implied sort direction via binding)
- ✓ Import button only visible when scan complete

**UX Issues:**

1. **Status Badges with Triggers**
   - Three separate triggers (Excluded, Fully imported, Partially imported) manage badge visibility
   - Correct logic but complex XAML
   - **Action:** Document multi-trigger behavior or consider combining into single `StatusDisplay` binding

2. **Strikethrough for Excluded Items**
   - Strikethrough applied in XAML trigger when `IsExcluded=True`
   - Works well visually, but opacity 0.45 + strikethrough may be redundant
   - **Action:** Consider removing one (redundant accessibility cue)

3. **Import Button Disabled State**
   - "Import Selected Folders" button only visible when `ScanComplete=True`
   - If no folders are selected, button is still visible but should be disabled
   - **Action:** Bind `IsEnabled` to `HasAnyFolderSelected`

4. **Missing Empty States**
   - No "No drives found" message when drive list is empty
   - No "Add your first exclusion" guidance text
   - **Action:** Add placeholder text for both empty states

**Keyboard Accessibility:**
- ✓ `KeyBinding` for Return in NewExclusion TextBox
- ✓ `KeyBinding` for Return/Escape in edit mode TextBox
- ✗ No keyboard shortcut to open Browse dialog for exclusion
- **Action:** Add mnemonic underlines to "Browse" and "Refresh" buttons

**Hardcoded Values:**
- ✗ Color codes in ContextMenu: `Background="#FF313244"` — should use `StaticResource` brushes
- ✗ Inline color: `#FFE8A020` on amber border — should use `YellowBrush` from resources

---

### 3. FindDuplicatesWindow.xaml

**Purpose:** Identify and mark duplicate photos (exact copies and RAW+JPG pairs) for batch deletion.

**Key Interactions:**
- Status bar: Shows marked count and combined size
- Groups ItemsControl: Each group shows duplicate set with quick-action buttons
- File cards: Thumbnails with metadata, extension badge, checkbox for "Mark for deletion"
- Quick actions: Keep RAW, Keep JPG, Keep Newest, Keep Oldest, Clear marks
- Bottom: Scan button, Delete button (shows marked count), Close button

**UX Issues:**

1. **Delete Confirmation Missing** ⚠️ HIGH PRIORITY
   - "Delete [N] files ([SIZE])" button at bottom has no confirmation dialog
   - Tooltip says "Permanently delete all checked files from disk — this cannot be undone"
   - Users can click Delete without seeing a confirmation prompt
   - **Action:** Add explicit confirmation dialog before deletion with list of files to be deleted

2. **No Empty State After Scan**
   - No "No duplicates found!" message after scan completes with zero results
   - **Action:** Add empty state: "✓ No duplicates found in your library"

3. **No Feedback After Quick Actions**
   - Keep RAW / Keep JPG / Keep Newest / Keep Oldest fire without visible notification
   - **Action:** Add brief notification "Marked X files for deletion" after batch action

4. **Extension Badge Colors**
   - Extension badges (.CR2, .NEF, .JPG) all use same `AccentBrush` (blue)
   - RAW and JPG files should be visually distinct
   - **Action:** Color code badges: Orange for RAW, Green for JPG

5. **No Global Unmark All**
   - "Clear" button only clears marks within one group
   - No "Unmark all" button at window level
   - **Action:** Add "Unmark all" to toolbar area

**Keyboard Accessibility:**
- ✗ No keyboard shortcuts for Keep RAW / Keep JPG / Keep Newest / Keep Oldest
- ✗ No keyboard navigation between groups or cards
- **Action:** Add keyboard shortcuts (K+R, K+J) and `Delete` key to toggle mark on focused card

**Data-Binding Issues:**
- ✓ Groups properly bound via `ItemsSource="{Binding Groups}"`
- ✓ Marked count in status bar uses `{Binding MarkedCount, Mode=OneWay}`
- ✗ Deletion should snapshot marked items in ViewModel before beginning, not read live state

---

### 4. SettingsWindow.xaml

**Purpose:** Configure AI engine (Ollama), performance settings, and folder exclusions.

**Key Interactions:**
- Three tabs: AI Engine, Performance, Exclusions
- **AI Engine:** Ollama URL + Test button; Vision Model dropdown; CLIP path + Browse; Restart warning
- **Performance:** Analysis mode (radio buttons); Import threads (slider); Thumbnail quality (checkbox)
- **Exclusions:** List of excluded folders; Add/Browse/Remove controls
- Bottom: Close, Save buttons

**UX Issues:**

1. **Theme Inconsistency** ⚠️ HIGH PRIORITY
   - SettingsWindow uses `#1a1a1a` background and `#0078d4` accent (Windows blue)
   - All other windows use Catppuccin palette (`#1E1E2E`, `#89B4FA`)
   - **Action:** Apply Catppuccin theme: Background `#FF1E1E2E`, Accent `#FF89B4FA`, Surfaces `#FF313244`

2. **No Save Confirmation Feedback**
   - Save button triggers `SaveCommand` with no visible success notification
   - **Action:** Add transient "✓ Settings saved" status text for 2 seconds after save

3. **Unclear Field Requirements**
   - "Ollama Base URL" TextBox has no placeholder
   - "CLIP Models Path" TextBox has no hint about expected folder structure
   - **Action:** Add `Tag` placeholder text to both fields; add help tooltip icon for Vision Model

4. **No Unsaved Changes Guard**
   - User can click Close with pending changes and lose them silently
   - **Action:** Detect unsaved changes and show confirmation dialog on Close

5. **Slider Lacks Tick Marks**
   - Import threads slider (1–8) has `IsSnapToTickEnabled="True"` but no visible ticks
   - **Action:** Add `TickPlacement="BottomRight"` to slider

**Keyboard Accessibility:**
- ✓ Tab navigation through all fields
- ✓ Radio buttons and checkboxes use standard keyboard interaction
- ✗ No Enter key to Save
- ✗ Slider requires arrow key or mouse (no direct numeric input)
- **Action:** Add `KeyBinding` for Enter to trigger SaveCommand

**Empty/Error States:**
- ✓ Ollama not responding: `ConnectionStatus` shows error text
- ✗ Missing: "No vision models found" if Ollama runs but has no models installed
- ✗ Missing: "CLIP models folder not found" if path is invalid

---

### 5. PhotoViewerWindow.xaml

**Purpose:** Full-screen (or windowed) image viewer with detailed metadata, AI tags, and navigation.

**UX Strengths:**
- ✓ Excellent keyboard navigation: Left/Right arrows + Escape (all defined in `InputBindings`)
- ✓ Zoom via scroll wheel with `RenderTransformOrigin="0.5,0.5"` to keep zoom centered
- ✓ Clean right pane with well-organized metadata sections (Details, Exposure, AI Description, Tags)
- ✓ Selectable text in metadata (TextBox styled as TextBlock with `IsReadOnly="True"`)
- ✓ Path text scrolls to end on change so filename always visible
- ✓ Clear error state: "⚠ No description" shown if vision model failed
- ✓ Good visual hierarchy: File name (large) → Camera → Sections

**Minor Issues:**
- ✗ No keyboard shortcut to re-analyze or toggle favorite (right-click menu only)
- ✗ AI description section has redundant visibility bindings (`HasAiDescription` and `DescriptionAttemptedButFailed` in two separate StackPanels — could be combined)

**Empty/Error States:**
- ✓ Image loading: Indeterminate progress bar centered
- ✓ Description failed: Shows warning with explanation
- ✓ No tags: Tags section hidden
- ✓ No GPS / no EXIF: Respective sections hidden

---

### 6. SetupWindow.xaml

**Purpose:** Startup splash screen that checks Ollama/AI engine availability.

**UX Strengths:**
- ✓ Minimal, focused design
- ✓ Status updates during check ("Checking Ollama connection…")
- ✓ Graceful fallback: "Continue Without AI" allows app to run without AI engine
- ✓ Transparent background with rounded border; professional appearance

**UX Issues:**
- ✗ Error message lacks actionable guidance: "Tags and descriptions won't work until the AI engine is available" — no link to setup guide or Settings
- **Action:** Add "Open Settings" button or link when check fails

---

## Cross-Window Issues

### Color/Theme Inconsistency
- **MainWindow, ScanDrivesWindow, PhotoViewerWindow:** Catppuccin palette (`#1E1E2E` bg, `#89B4FA` accent)
- **SettingsWindow:** Windows blue theme (`#1a1a1a` bg, `#0078d4` accent)
- **SetupWindow:** Generic dark theme (`#1a1a1a` bg, gray accents)
- **Recommendation:** Standardize all windows on Catppuccin palette

### Hardcoded Colors in XAML
Multiple windows have inline color values instead of centralized theme resources:
- MainWindow: `#1a2a1a`, `#2d5a2d`, `#1a8fe0`, `#6fcf6f`, `#eb8a4a` (metrics panel)
- PhotoViewerWindow: `#55000000`, `#CCE53935`
- **Action:** Extract all colors to a centralized ThemeResources.xaml

### Button Style Inconsistencies
Different button styles used across windows: `PrimaryButtonStyle`, `ActionButton`, `NavButtonStyle`, `AccentButton`, `DangerButton`, etc. Some are defined locally, others app-level.
- **Action:** Consolidate all button styles to app-level resource dictionary with consistent naming

### Missing Global Keyboard Shortcuts
- No shortcut to open Scan Drives, Duplicates, or Import
- No shortcut to toggle favorite on selected photo
- **Action:** Add at minimum `Ctrl+Shift+S` (Scan), `Ctrl+Shift+D` (Duplicates), `F` (Favorite)

---

## Recommendations Summary

### High Priority
1. **FindDuplicatesWindow:** Add deletion confirmation dialog with file list preview
2. **SettingsWindow:** Apply Catppuccin theme for visual consistency
3. **MainWindow:** Add keyboard shortcuts for Scan, Duplicates, and Favorite toggle

### Medium Priority
1. **MainWindow:** Standardize active navigation state styling across sidebar
2. **ScanDrivesWindow:** Add empty state guidance for drives list and exclusions list
3. **FindDuplicatesWindow:** Add "no duplicates found" empty state after scan
4. **SettingsWindow:** Add "Settings saved" notification; add unsaved changes guard
5. All windows: Extract hardcoded colors to theme resources

### Low Priority
1. **MainWindow:** Simplify multi-selection visual feedback
2. **FindDuplicatesWindow:** Color-code RAW vs JPG extension badges
3. **PhotoViewerWindow:** Combine redundant AI description visibility bindings
4. **SetupWindow:** Add "Open Settings" link when Ollama check fails

---

## Accessibility Checklist

| Criterion | Status | Notes |
|-----------|--------|-------|
| Keyboard Navigation | Partial | Arrows/Tab work; missing shortcuts for common actions |
| Screen Reader Support | Unknown | XAML lacks `AutomationProperties` in many places |
| Color Contrast | Good | Dark theme with light text; high contrast maintained |
| Focus Indicators | Implicit | WPF default focus rect not customized; consider explicit `FocusVisualStyle` |
| Error Messages | Good | Clear, actionable error states in most places |
| Disabled State Clarity | Partial | Opacity changes used but not always accompanied by grayed text |
