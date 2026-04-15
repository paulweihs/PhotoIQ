# PhotoIQ — Known Cosmetic Issues

## Search Bar

### Cursor alignment in search TextBox
**Status:** Parked
**Symptom:** The text insertion cursor does not align precisely with the placeholder text in the search bar. The cursor appears slightly offset from where the placeholder "S" in "Search photos..." begins.
**Root cause:** WPF custom ControlTemplate — `VerticalContentAlignment` does not propagate to `PART_ContentHost`. Multiple centering approaches attempted; horizontal alignment between cursor x=0 and PlaceholderText x=2 remains imprecise due to font side-bearing differences between TextBoxView and TextBlock rendering.
**Impact:** Cosmetic only. Search functions correctly. Cursor is close to correct position.
**Fix when revisited:** Use Snoop or WPF Visual Tree debugger to measure actual rendered x positions of PART_ContentHost and PlaceholderText at runtime, then adjust `Margin` on PlaceholderText to match exactly.
