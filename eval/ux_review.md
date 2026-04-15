# UI/UX Review — PhotoIQ Pro

_Reviewed: April 2026 | Reviewer: Senior UI/UX Engineer_
_Files covered: MainWindow.xaml, MainWindow.xaml.cs, MainViewModel.cs, all Views/*.xaml, all ViewModels/*.cs, App.xaml, Dark.xaml_

---

## Executive Summary

PhotoIQ Pro has a solid MVVM foundation, thoughtful feedback patterns (taskbar progress, live gallery updates, inline editing), and several well-considered details (description freshness badges, offline overlays, persistent import queue). The biggest concerns are a near-complete absence of `AutomationProperties` making the app effectively inaccessible to screen readers, the destructive `ExcludeImage` command firing with no confirmation and no undo path, and a theme/color inconsistency across dialog windows that undermines visual polish. The toolbar also becomes crowded during multi-selection in a way that can obscure primary navigation controls.

---

## Issues by Severity

### Critical (blocking or data-loss risk)

---

**C-1: Exclude Image has no confirmation and is silently irreversible**

`ExcludeImageAsync()` in `MainViewModel.cs` (line ~1267) immediately removes photos from the library and marks them excluded — with no `MessageBox.Show` confirmation. The only feedback is the brief `StatusText` update "Excluded N photo(s) — will not be reimported during future scans". Unlike `DeletePermanentlyAsync`, which guards with a `YesNo` dialog, a single stray Ctrl+click followed by an accidental context-menu click silently and permanently hides photos. In multi-selection mode, this scales to hundreds of files.

**Recommendation:** Add a `MessageBox.Show(YesNo)` confirmation identical in structure to `DeletePermanentlyAsync`. Include file count and a single-sentence note that photos can be re-imported.

---

**C-2: RemoveFromLibrary has no confirmation — data is lost silently**

`RemoveFromLibraryAsync()` (`MainViewModel.cs` line ~1287) deletes all DB records for the selected photos with no confirmation. While it does not touch disk files, all AI descriptions, tags, captions, and album memberships are destroyed. There is no undo.

**Recommendation:** Gate with a `MessageBox.Show(YesNo)` that names the count, explains what is lost (metadata, not files), and defaults to `No`.

---

**C-3: Uncancellable vision worker can process deleted photos**

`VisionBackgroundWorkerAsync` drains a `ConcurrentQueue<Guid>`. When `DeletePermanentlyAsync` or `RemoveFromLibraryAsync` removes a photo, its ID may still be queued. The worker calls `repo.GetByIdAsync(photoId)` which returns `null`, but `import.RunVisionForPhotoAsync(photoId, ct)` is called first — if that service re-queries and throws, the `catch` silently swallows the error. A crash during analysis of a just-deleted entity could leave the DB in an inconsistent state.

**Recommendation:** Add a `_visionSkipSet` (a `ConcurrentDictionary<Guid, bool>`) populated by all delete/remove commands, and skip items found in it at the top of the worker loop.

---

### High (significant friction, common workflows)

---

**H-1: No AutomationProperties anywhere — app is screen-reader blind**

Zero `AutomationProperties.Name` or `AutomationProperties.HelpText` attributes are set on any interactive control across all fourteen XAML files. The gallery `ListBox` (`GalleryListBoxStyle`) sets `Focusable="False"` and the `ListBoxItem` template strips the entire control template to a bare `ContentPresenter`. This means Narrator and JAWS receive no semantic information: photo items, the favorite star, tag chips, nav arrows in the viewer, and all action buttons are announced only by their emoji content or nothing at all.

**Recommendation:** At minimum: set `AutomationProperties.Name` on the search box, favorite button, the gallery `ListBox`, each nav button (sidebar), and the `PhotoViewerWindow` nav arrows. Consider restoring `Focusable="True"` on `ListBoxItem` or providing a separate keyboard-navigable alternative.

---

**H-2: Gallery ListBox is not keyboard-navigable without a mouse**

`GalleryListBoxStyle` sets `Focusable="False"` (line 122). Arrow-key navigation is handled via `OnWindowPreviewKeyDown` in the code-behind, which calls `NavigateNextCommand`/`NavigatePreviousCommand`, but this updates `SelectedMediaFile` only — it does not add items to `MultiSelectedItems`. This means keyboard users cannot build a multi-selection at all, cannot Tab into the gallery to navigate, and have no way to reach the thumbnail context menu without a mouse.

**Recommendation:** Remove `Focusable="False"` from `GalleryListBoxStyle`. Implement `KeyboardNavigation.TabNavigation="Once"` and `DirectionalNavigation`. Add `Shift+Arrow` range selection inside `OnWindowPreviewKeyDown`.

---

**H-3: Multi-selection toolbar appears without warning and can hide the primary search bar**

When `HasMultiSelection` is true, three additional full-width buttons ("Re-analyze selected (N)", "Remove from Library (N)", "Delete Permanently") expand into the top-right action area. At 1400px width these fit, but there is no minimum constraint — at smaller widths the buttons overflow or wrap unpredictably, potentially pushing the search `Grid` off-screen. The `DeletePermanentlyLabel` button is red but shares the same `PrimaryButtonStyle` base and is placed next to non-destructive actions with no visual separator.

**Recommendation:** Move the bulk-action buttons to a contextual strip beneath the main toolbar (hidden until multi-selection is active), or use an `OverflowMode`-capable `ToolBar`. Add a visual separator before the red delete button.

---

**H-4: Settings changes require restart but there is no restart prompt**

`SettingsWindow.xaml` (line 251–253) shows a note: "AI Engine changes (URL, model, CLIP path) require a restart to take effect." But the note is static text that the user can miss, and saving the settings does not prompt a restart or even make the note more prominent. The `ActiveVisionModel` property in `MainViewModel.cs` reads from `UserPreferences.Current` at property evaluation — so the status bar model label updates immediately after save, implying the change is live when it is not.

**Recommendation:** When `SettingsViewModel.Save()` detects a change to `OllamaUrl`, `VisionModel`, or `ClipModelsPath`, show a `MessageBox` asking "Restart now to apply changes?" After save, grey out or clear `ActiveVisionModel` in the status bar and replace it with "Restart required".

---

**H-5: PhotoViewerWindow has no zoom, pan, or rotation support**

The full-screen viewer (`PhotoViewerWindow.xaml`) renders the full-resolution image at `Stretch="Uniform"` with no way to zoom in, zoom out, pan, or rotate. `DecodePixelWidth=1600` in `PhotoViewerViewModel.cs` means images wider than 1600px are downsampled before display. For an AI photo library product where a key use case is inspecting face recognition or AI description quality, the inability to zoom is a significant limitation.

**Recommendation:** Add `ScrollViewer` wrapping the `Image`, a `ScaleTransform` driven by mouse-wheel, and Ctrl+0 to reset. Alternatively expose a minimum "double-click to zoom to 1:1" shortcut.

---

**H-6: Tag remove button is not keyboard-accessible**

The × remove button on each tag chip (MainWindow.xaml line ~1207) sets `Focusable="False"`. Tags can only be removed by clicking the × button. There is no keyboard shortcut and no way to navigate to the button with Tab. Combined with H-2, a keyboard-only user cannot remove tags at all.

**Recommendation:** Remove `Focusable="False"` from the tag remove button and add `AutomationProperties.Name="Remove tag {Name}"`.

---

**H-7: ExcludeFolder command silently drops current selection anchor**

`ExcludeFolderAsync()` (`MainViewModel.cs` ~line 1204) correctly computes `nextPhoto` to scroll-to after the gallery reloads. However, when `nextPhoto` is `null` (the excluded folder contained the last photos in the gallery), `SelectedMediaFile` is left as the previously selected item — which no longer exists in `MediaFiles` after `LoadAsync()`. The detail panel then shows stale data because the `prevSelectedId` match in `LoadAsync()` (~line 432) fails silently.

**Recommendation:** In `LoadAsync()`, if `prevSelectedId` is set but not found in the refreshed list, explicitly set `SelectedMediaFile = null` and clear the detail panel.

---

### Medium (noticeable but workaroundable)

---

**M-1: Inconsistent color palette across dialog windows**

Four different dark palettes are used across the app:
- `MainWindow`: `#1a1a1a` background, `#0078d4` accent (`Dark.xaml`)
- `ScanDrivesWindow` and `FindDuplicatesWindow`: `#1E1E2E` background, `#89B4FA` accent (Catppuccin Mocha)
- `SettingsWindow`: `#1a1a1a` background, `#0078d4` accent (matches main)
- `PhotoViewerWindow`: `#0D0D12` background, `#89B4FA` accent (Catppuccin Mocha)
- `UpgradePromptWindow`: `#1E1E2E` / `#89B4FA`

Each dialog defines its own local brush resources. The accent blue shifts from `#0078d4` (Windows blue) to `#89B4FA` (Catppuccin lavender) depending on which window is open. This is visually jarring when transitioning between dialogs.

**Recommendation:** Consolidate all brush definitions into `Dark.xaml`. Replace the per-window `SolidColorBrush` resource blocks in `ScanDrivesWindow`, `FindDuplicatesWindow`, `PhotoViewerWindow`, and `UpgradePromptWindow` with `{DynamicResource ...}` references.

---

**M-2: Thumbnail size slider has no label for the current value**

`MainWindow.xaml` references a `ThumbnailSize` binding and `ThumbnailSliderStyle` in the style section, but the status bar and toolbar contain no visible slider. Searching the full file reveals no slider element in the rendered UI — the slider style is defined but appears unused, and `ThumbnailSize` defaults to `180` in the VM (`MainViewModel.cs` line 45). If the slider was removed but the value is still hard-coded, the user has no way to adjust thumbnail density.

**Recommendation:** Either expose the slider (a compact slider in the status bar is conventional for photo apps) or remove the dead `ThumbnailSliderStyle` and `ThumbnailSize` observable property.

---

**M-3: "Tags" and "People" sidebar buttons are non-functional placeholders with no visual indication**

`MainWindow.xaml` (lines 448–451) renders two sidebar buttons — "Tags" and "People" — with `ToolTip="(coming soon)"` but no `Command` binding and no `IsEnabled="False"`. Clicking them does nothing and the cursor changes to `Hand` (inherited from `NavButtonStyle`) suggesting they are interactive. A new user will click these and assume the feature is broken.

**Recommendation:** Either set `IsEnabled="False"` and remove the `Hand` cursor via a trigger, or replace the tooltip text with a more prominent in-panel "Coming soon" message. Do not show a cursor affordance for a non-functional control.

---

**M-4: Album active state is not highlighted per-album in the sidebar**

`MainWindow.xaml` (lines 411–418): the album `DataTrigger` for `IsAlbumActive` is stubbed out with a comment "highlight only the active album - handled in command" but has no setter. All album buttons render identically whether or not they are the active album. Users navigating between albums have no visual anchor for which album is current.

**Recommendation:** Expose `ActiveAlbumId` from the ViewModel and bind it in the DataTrigger: `<DataTrigger Binding="{Binding DataContext.ActiveAlbumId, ...}" Value="{Binding Id}">` to highlight only the selected album row.

---

**M-5: Search box loses focus after clearing via the × button**

`ClearSearchCommand` sets `SearchQuery = ""`. The × button is `Focusable="False"` (line 214), so clicking it never transfers focus to the search box. After clearing, focus returns to whatever previously held it (often nowhere, making the window appear unresponsive to keyboard input). The placeholder text reappears but the user must click the box again to type.

**Recommendation:** In the `ClearSearch()` command or in a code-behind click handler, call `SearchBox.Focus()` after clearing.

---

**M-6: Description edit TextBox has no character limit or counter**

`MainWindow.xaml` (line 1120): the `EditingDescriptionText` TextBox has `AcceptsReturn="True"` and `UpdateSourceTrigger=PropertyChanged` but no `MaxLength`. The caption TextBox (line 941) does set `MaxLength="200"`. A user could paste an arbitrarily large block of text into the description field, which would be saved to the DB without truncation and would break the fixed-height detail panel layout.

**Recommendation:** Add `MaxLength="1000"` (or a project-appropriate value) to the description edit TextBox. Optionally add a character counter below the field.

---

**M-7: SettingsWindow saves immediately on "Save" but provides no cancel/discard path**

`SettingsViewModel.Save()` writes preferences directly on the button click. The window has no Cancel button and `ResizeMode="CanResize"` means the user can close the window via the title bar X, but closing does not revert changes — `Save()` has already been called. This creates a false impression that closing without saving discards changes.

**Recommendation:** Track original preference values in the constructor. Replace the single "Save" button with "Save" + "Cancel" (or "Close"). On window close without Save, leave preferences unchanged. Alternatively, apply all changes only on Save and explicitly show "unsaved changes" if the user closes via X.

---

**M-8: "Re-analyze All" confirmation uses PhotoCount from the current view, not the total library**

`ReanalyzeAllAsync()` (`MainViewModel.cs` line 934) uses `PhotoCount` in the confirmation string, which reflects the currently filtered view (not all photos when a search is active). A user filtered to 5 results who clicks "Re-analyze All" sees "Re-analyze all 5 photos" — but the command runs `Progress.RunReanalyzeAsync(unanalyzedOnly: false)` which re-analyzes the entire library.

**Recommendation:** Query `WithRepo(r => r.CountAsync())` for the true total and use that in the confirmation string. Alternatively, rename the command to "Re-analyze All in View" and scope the operation to match.

---

**M-9: Progress bar height of 5px makes it effectively invisible on high-DPI displays**

`MainWindow.xaml` (line 1329): the status-bar progress bar is `Height="5"`. On a 4K display at 150% scaling, this renders at approximately 3 physical pixels and is nearly invisible, especially against the dark `#333333` background. The SplashWindow progress bar at `Height="3"` has the same problem.

**Recommendation:** Use `Height="6"` minimum for the in-app progress bar. Consider `Height="8"` for the splash screen version which is the sole visual indicator of startup progress.

---

**M-10: AlbumPickerWindow allows double-click confirmation without a selection**

`AlbumPickerWindow.xaml` (line 60): `SelectionChanged` enables the "Add to Album" button. However, there is no `MouseDoubleClick` handler — the user must single-click then click "Add to Album". This is inconsistent with the main gallery (`OnPhotoDoubleClick`) which opens on double-click. More critically, the window is `ResizeMode="NoResize"` at 360×400, which may be too small for libraries with many deeply nested albums.

**Recommendation:** Add a `MouseDoubleClick` handler that confirms selection. Make the window resizable.

---

### Low / Polish

---

**L-1: Window title is "PhotoIQ" (main window) but "PhotoIQ Pro" (splash screen)**

`MainWindow.xaml` line 7: `Title="PhotoIQ"`. The splash screen (`SplashWindow.xaml` line 17) correctly shows "PhotoIQ Pro". The title bar branding is inconsistent.

**Recommendation:** Set `MainWindow` title to "PhotoIQ Pro".

---

**L-2: Emoji icons are used for navigation labels but not for keyboard shortcuts**

Sidebar buttons use emoji ("🖼", "⭐", "📁", "📊") rendered via raw Unicode characters in `TextBlock` content. These emoji render at system font size which varies by Windows version and font fallback. On Windows 11 with Segoe UI Emoji they look fine; on older builds or certain accessibility fonts they can render as boxes or overlap adjacent text.

**Recommendation:** Replace decorative emoji with `Path`-based vector icons or use a symbol font (Segoe Fluent Icons via `FontFamily="Segoe Fluent Icons"`). This also enables proper `AutomationProperties` since `Path` elements are non-semantic.

---

**L-3: The "⚡ Duplicates" toolbar label is misleading**

The lightning bolt ⚡ conventionally means "fast", "power", or "express mode" in photo apps. Using it for the Duplicates finder creates semantic confusion with the Express tier, which also uses ⚡ in `AppSettings.TierExpress`. The Scan Drives button uses 🔍 (appropriate for search/scan). The Duplicates button would be better served by a copy or stacked-papers icon.

**Recommendation:** Change `Content="⚡ Duplicates"` to `Content="⊘ Duplicates"` or a more clearly "duplicate" semantic symbol.

---

**L-4: PrimaryButtonStyle has no hover or pressed state**

`Dark.xaml` (line 16–31): `PrimaryButtonStyle` has no `IsMouseOver` or `IsPressed` trigger. Buttons using this style (Scan Drives, Import Folder, Import Files, Find Duplicates) show no visual feedback on hover or click. The `NavButtonStyle` does have a hover trigger. This inconsistency makes the primary buttons feel unresponsive.

**Recommendation:** Add a hover state (e.g., `#1a8fe0` background) and a pressed state (e.g., `#0060aa`) to `PrimaryButtonStyle` in `Dark.xaml`.

---

**L-5: ExcludeFolderDialog has no "undo" entry point after closing**

`ExcludeFolderAsync()` shows the dialog, but once confirmed, the only reversal path is through `IncludeFolderCommand` (visible in context menus only if a photo from that folder is still selected — which may not be the case after the gallery reloads). The status bar text reads "Folder excluded — N photo(s) removed from library." with no actionable link.

**Recommendation:** Add an "Undo" hyperlink in the status bar text for ~10 seconds after an exclude, backed by a stored `Guid`/path that can be reversed with a single `IncludeFolderAsync` call.

---

**L-6: SelectableText PreviewMouseDown selects-all on every click, making partial text selection impossible**

`MainWindow.xaml.cs` (line 90–98) and `PhotoViewerWindow` both call `tb.SelectAll()` and `e.Handled = true` in `SelectableText_PreviewMouseDown`. This means a user can never position the text cursor in the middle of a long file path to copy part of it. Click-and-drag selection also fails because `e.Handled = true` prevents the mouse-down from beginning a drag selection.

**Recommendation:** Only call `SelectAll()` on first focus (when `!tb.IsFocused`). On subsequent clicks within an already-focused TextBox, allow normal click-cursor-placement behavior by not setting `e.Handled = true`.

---

**L-7: "ALL METADATA" expander loads synchronously on expand, blocking the UI thread briefly**

`AllMetadataExpander_Expanded` (MainWindow.xaml.cs line 178) calls `LoadAllMetadataCommand.Execute(null)`. If `LoadAllMetadataCommand` reads EXIF from disk synchronously, this blocks the UI thread while expanding. For large RAW files with hundreds of EXIF tags, this can cause a visible stutter.

**Recommendation:** Ensure `LoadAllMetadataCommand` is backed by an `async` relay command and wraps the disk read in `Task.Run`.

---

**L-8: SetupWindow spinner is three static ellipses, not an animation**

`SetupWindow.xaml` (lines 64–67): the "checking" state shows three static `Ellipse` elements in gray. They do not animate. The main splash screen uses an indeterminate `ProgressBar`. The setup window gives no sense of activity.

**Recommendation:** Replace the static ellipses with an indeterminate `ProgressBar` matching the splash screen style, or use a `DispatcherTimer`-driven dot animation.

---

**L-9: Offline drive status text is not clickable to surface a resolution**

The status bar shows "N photos on offline drives" (MainWindow.xaml line 1374) with a tooltip. But the text is a `TextBlock` — clicking it does nothing. A user who wants to "reconnect" or understand what to do has to read the tooltip closely.

**Recommendation:** Make the offline status text a `Button` styled as a link that opens a contextual MessageBox listing the offline drive roots and suggesting reconnection steps.

---

**L-10: Caption and description edit modes do not auto-focus the edit TextBox**

When `BeginEditDescription` or `BeginEditCaption` executes, the `IsEditing` flag flips to `True` and the edit `TextBox` becomes visible — but WPF does not auto-focus newly visible elements. The user must click into the TextBox after clicking "Edit". For a detail panel where editing is a common action this adds unnecessary friction.

**Recommendation:** In the `BeginEditDescription` / `BeginEditCaption` code-behind or via a behavior, call `.Focus()` on the edit TextBox immediately after it becomes visible. A `Behavior<TextBox>` that calls `Focus()` on `IsVisible` changing to `true` works cleanly without code-behind coupling.

---

## Strengths

**Taskbar progress integration is well-implemented.** `TaskbarItemInfo` with `ProgressState` and `ProgressValue` (MainWindow.xaml lines 11–14) is correctly driven through `AttachTaskbar`/`ClearTaskbar` in the VM and covers all long operations: import, re-analyze, thumbnail generation.

**Import queue persistence is a standout feature.** The JSON queue file, `ResumeImportQueueAsync`, and the graceful close-during-import dialog (MainWindow.xaml.cs lines 38–61) are rare in indie desktop apps. The close warning message is well-worded and defaults to `No`.

**Description freshness badges are genuinely useful.** The "✓ current" / "⚠ outdated" badges in the detail panel (MainWindow.xaml lines 1090–1102) are one of the most thoughtful UX decisions in the codebase. Exposing prompt version and model name in the tooltip is exactly right for a power user who cares about AI quality.

**Multi-selection implementation is technically solid.** Ctrl+click, Shift+range, Ctrl+A, and the dual-border selection visualization (accent border for the detail-panel selection, dimmer blue for additional multi-selected items) work correctly. The `MultiSelectedVersion` counter trick to force `MultiBinding` re-evaluation is appropriate.

**Offline drive detection via `WM_DEVICECHANGE`** (MainWindow.xaml.cs lines 63–78) is the correct Windows API approach. Most apps poll; this is event-driven.

**The per-scope `DbContext` pattern** (`WithRepo` / `WithLibRepo`) across all VM operations prevents EF Core identity-map corruption on long-running sessions. This is architecture-level correctness that pays off at scale.

**Vision worker priority queue** (normal vs. priority queues, `PrioritizeVision`) enables "analyze selected photo now" semantics while the background batch continues. The semaphore-based cancel-on-navigation in `PhotoViewerViewModel.LoadCurrentAsync` is similarly correct.

**Empty state screens are actionable.** Both the library-empty state and no-search-results state provide clear CTAs with buttons, not just explanatory text.

---

## Quick Wins

These five changes require minimal code and would have the highest visible impact:

1. **Add confirmation to `ExcludeImageAsync` and `RemoveFromLibraryAsync`** (C-1, C-2). Copy the existing `DeletePermanentlyAsync` `MessageBox.Show` pattern. ~15 lines of code each, zero architecture change, eliminates the only two unconfirmed destructive actions.

2. **Set `AutomationProperties.Name` on the 10 most critical controls** (H-1). Search box, favorite button, gallery ListBox, PhotoViewer nav arrows, and the main action buttons. This is purely additive XAML — no logic change required — and unlocks basic screen reader support.

3. **Fix the active album sidebar highlight** (M-4). The DataTrigger stub (line 414–418) needs only a `Setter` targeting `Background` and `Foreground` bound to `DataContext.ActiveAlbumId == node.Id`. ~5 lines of XAML.

4. **Add hover state to `PrimaryButtonStyle`** (L-4). Two additional `Trigger` blocks in `Dark.xaml`. The absence of hover feedback on the primary action buttons is the most immediately noticeable visual flaw to a new user clicking "Scan Drives" for the first time.

5. **Disable the "Tags" and "People" sidebar buttons** (M-3). Set `IsEnabled="False"` on both buttons. Prevents user confusion about whether a feature is broken. ~2 lines of XAML change.
