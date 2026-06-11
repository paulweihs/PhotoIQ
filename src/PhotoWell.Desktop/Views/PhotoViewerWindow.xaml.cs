using System.Collections.Specialized;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using PhotoWell.Core.Models;
using PhotoWell.Desktop.ViewModels;

namespace PhotoWell.Desktop.Views;

public partial class PhotoViewerWindow : Window
{
    private double _zoom = 1.0;
    private const double ZoomStep = 0.15;
    private const double MinZoom  = 0.5;
    private const double MaxZoom  = 5.0;

    private Point _panStartPoint;
    private bool _isPanning = false;
    private UIElement? _panCapture;

    private readonly SolidColorBrush _faceKnownBrush   = new(Color.FromArgb(0xFF, 0x89, 0xB4, 0xFA)); // accent blue
    private readonly SolidColorBrush _faceUnknownBrush = new(Color.FromArgb(0xAA, 0xFF, 0xFF, 0xFF)); // dim white
    private readonly SolidColorBrush _labelBgBrush     = new(Color.FromArgb(0xCC, 0x1E, 0x1E, 0x2E));
    private readonly SolidColorBrush _labelFgBrush     = new(Color.FromArgb(0xFF, 0xCD, 0xD6, 0xF4));

    private FaceItemViewModel? _pendingFace;
    private bool _pendingMentionMode;
    private List<Person> _allNamedPersons = [];
    private string? _aiSuggestedName;

    public PhotoViewerWindow(PhotoViewerViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        vm.RequestClose += Close;

        // Reset zoom when the displayed photo changes.
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PhotoViewerViewModel.CurrentImage))
            {
                ResetZoom();
                RedrawFaceOverlay();
                UpdateDebugDisplay();
            }
        };

        // Redraw when face data arrives (loaded async after image).
        vm.Faces.CollectionChanged += (_, e) =>
        {
            if (e.Action is NotifyCollectionChangedAction.Reset
                         or NotifyCollectionChangedAction.Add
                         or NotifyCollectionChangedAction.Remove)
            {
                RedrawFaceOverlay();
                UpdateDebugDisplay();
            }
        };

        // Redraw on resize so boxes stay aligned when the window is resized.
        FaceOverlayCanvas.SizeChanged += (_, _) => RedrawFaceOverlay();

        // Wire up face review event — show dialog with Yes/No/Skip for auto-recognized faces
        vm.RequestFaceReview += async args =>
        {
            var candidates = await vm.GetUnconfirmedFacesForPersonAsync(args.PersonId);
            if (candidates.Count == 0) return;
            var dialog = new FaceReviewDialog(candidates, args.PersonName, args.PersonId, vm) { Owner = this };
            dialog.ShowDialog();
        };
    }

    // ── Face overlay ──────────────────────────────────────────────────────────

    /// <summary>
    /// Clears and redraws face bounding boxes on the overlay canvas.
    /// Coordinates are stored as normalized fractions (0–1) relative to the
    /// source image; we unproject them through the Uniform-stretch letterbox.
    /// </summary>
    private void RedrawFaceOverlay()
    {
        FaceOverlayCanvas.Children.Clear();

        var vm = (PhotoViewerViewModel)DataContext;
        if (vm.Faces.Count == 0 || vm.Current == null) return;

        double canvasW = FaceOverlayCanvas.ActualWidth;
        double canvasH = FaceOverlayCanvas.ActualHeight;
        if (canvasW <= 0 || canvasH <= 0) return;

        // Determine the letterboxed image rect (Stretch=Uniform, centred).
        double imgW = vm.Current.Width  > 0 ? vm.Current.Width  : canvasW;
        double imgH = vm.Current.Height > 0 ? vm.Current.Height : canvasH;

        double scaleX    = canvasW / imgW;
        double scaleY    = canvasH / imgH;
        double scale     = Math.Min(scaleX, scaleY);
        double rendW     = imgW * scale;
        double rendH     = imgH * scale;
        double offsetX   = (canvasW - rendW) / 2.0;
        double offsetY   = (canvasH - rendH) / 2.0;

        foreach (var face in vm.Faces)
        {
            if (face.IsManualMention) continue; // no bounding box to draw

            double left  = offsetX + face.X * rendW;
            double top   = offsetY + face.Y * rendH;
            double w     = face.FaceWidth  * rendW;
            double h     = face.FaceHeight * rendH;

            bool   isNamed     = !string.IsNullOrEmpty(face.MatchedName);
            string label       = isNamed ? face.MatchedName! : "Unknown";
            var    strokeBrush = isNamed ? _faceKnownBrush : _faceUnknownBrush;

            // Bounding box rectangle — all faces are now interactive (click to rename/assign).
            var rect = new Rectangle
            {
                Width           = w,
                Height          = h,
                Stroke          = strokeBrush,
                StrokeThickness = 2,
                Fill            = Brushes.Transparent,
                Opacity         = 0.0,  // hidden until hover
                Cursor          = Cursors.Hand,
                ToolTip         = "Click to name this person",
                IsHitTestVisible = true,
                Tag             = face,
            };
            // MouseDown: handle to prevent panning from starting; MouseUp: open popup on release (standard click)
            rect.MouseLeftButtonDown += FaceRect_PreventPan;
            rect.MouseLeftButtonUp   += FaceRect_AnyClick;

            Canvas.SetLeft(rect, left);
            Canvas.SetTop(rect,  top);
            FaceOverlayCanvas.Children.Add(rect);

            // Name label — pinned just below the box, left-aligned.
            var tb = new TextBlock
            {
                Text       = label,
                FontSize   = 11,
                Foreground = _labelFgBrush,
                Padding    = new Thickness(5, 2, 5, 2),
            };
            var labelBorder = new Border
            {
                Background    = _labelBgBrush,
                CornerRadius  = new CornerRadius(3),
                Child         = tb,
                Opacity       = 0.0,  // hidden until hover
                IsHitTestVisible = true,
                Cursor        = Cursors.Hand,
                Tag           = face,
            };
            labelBorder.MouseLeftButtonDown += FaceRect_PreventPan;
            labelBorder.MouseLeftButtonUp   += FaceRect_AnyClick;

            double labelY = top + h + 3;
            // If label would clip below canvas, show it above the box instead.
            if (labelY + 20 > canvasH) labelY = top - 22;

            Canvas.SetLeft(labelBorder, left);
            Canvas.SetTop(labelBorder,  labelY);
            FaceOverlayCanvas.Children.Add(labelBorder);
        }
    }

    // ── Hover show/hide ───────────────────────────────────────────────────────

    private void FaceOverlay_MouseEnter(object sender, MouseEventArgs e)
    {
        foreach (UIElement child in FaceOverlayCanvas.Children)
            child.Opacity = 1.0;
    }

    private void FaceOverlay_MouseLeave(object sender, MouseEventArgs e)
    {
        if (FaceNamePopup.IsOpen) return;  // keep visible while popup is open
        foreach (UIElement child in FaceOverlayCanvas.Children)
            child.Opacity = 0.0;
    }

    // ── Face click → popup ─────────────────────────────────────────────────────

    private void FaceRect_PreventPan(object sender, MouseButtonEventArgs e)
        => e.Handled = true; // stop the event from reaching the image grid's pan handler

    private async void FaceRect_AnyClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not FaceItemViewModel face) return;
        await OpenFacePopupAsync(face, fe);
        e.Handled = true;
    }

    private async void SidebarFaceThumbnail_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not FaceItemViewModel face) return;
        await OpenFacePopupAsync(face, fe);
        e.Handled = true;
    }

    // ── Tag person by clicking empty image area ───────────────────────────────

    private async void FaceOverlayCanvas_Click(object sender, MouseButtonEventArgs e)
    {
        // Face rect clicks mark e.Handled = true, so this only fires for empty-area clicks.
        // Mouse is captured by the pan handler when zoomed in, so this won't fire during panning.
        var vm = (PhotoViewerViewModel)DataContext;
        if (vm.Current == null) return;

        await OpenMentionPopupAsync();
        e.Handled = true;
    }

    private async Task OpenMentionPopupAsync()
    {
        _pendingFace         = null;
        _pendingMentionMode  = true;
        _aiSuggestedName     = null;
        var vm = (PhotoViewerViewModel)DataContext;
        _allNamedPersons = (await vm.GetAllNamedPersonsAsync()).ToList();

        FacePopupTitle.Text           = "Tag person in photo";
        FaceNameBox.Text              = "";
        FaceNameUnlinkBtn.Visibility  = Visibility.Collapsed;
        AiSuggestionBorder.Visibility = Visibility.Collapsed;

        FaceNamePopup.IsOpen = true;
        FaceNameBox.Focus();
    }

    private async Task OpenFacePopupAsync(FaceItemViewModel face, FrameworkElement anchor)
    {
        _pendingFace      = face;
        _aiSuggestedName  = null;
        var vm = (PhotoViewerViewModel)DataContext;
        _allNamedPersons = (await vm.GetAllNamedPersonsAsync()).ToList();

        FacePopupTitle.Text          = face.IsNamed ? $"Rename \"{face.MatchedName}\"" : "Who is this?";
        FaceNameBox.Text             = face.MatchedName ?? "";
        FaceNameUnlinkBtn.Visibility = face.IsNamed ? Visibility.Visible : Visibility.Collapsed;

        // AI suggestion — only for unnamed faces with a stored embedding
        AiSuggestionBorder.Visibility = Visibility.Collapsed;
        if (!face.IsNamed && face.Embedding != null)
        {
            var suggestion = await vm.GetAiSuggestionAsync(face.Embedding);
            if (suggestion != null)
            {
                var pct = (int)(suggestion.Value.Confidence * 100);
                AiSuggestionText.Text = $"{suggestion.Value.Name}  ({pct}% match)";
                _aiSuggestedName      = suggestion.Value.Name;
                AiSuggestionBorder.Visibility = Visibility.Visible;
            }
        }

        FaceNamePopup.IsOpen = true;
        FaceNameBox.Focus();
        FaceNameBox.SelectAll();
    }

    // ── AI suggestion accept ───────────────────────────────────────────────────

    private async void AiSuggestionAccept_Click(object sender, RoutedEventArgs e)
    {
        if (_aiSuggestedName == null) return;
        FaceNameBox.Text = _aiSuggestedName;
        await SaveFacePopupAsync();
    }

    // ── Type-ahead filtering ───────────────────────────────────────────────────

    private void FaceNameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_allNamedPersons.Count == 0) { SuggestionBorder.Visibility = Visibility.Collapsed; return; }

        var ranked = RankedNames(FaceNameBox.Text, _pendingFace?.Embedding).ToList();
        SuggestionList.ItemsSource  = ranked.Count > 0 ? ranked : null;
        SuggestionBorder.Visibility = ranked.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void FaceNameBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)  { _ = SaveFacePopupAsync(); e.Handled = true; return; }
        if (e.Key == Key.Escape) { FaceNameCancel_Click(sender, e); return; }
        if (e.Key == Key.Tab)
        {
            SuggestionBorder.Visibility = Visibility.Collapsed;
            return; // let Tab propagate to move focus to Save button
        }
        if (e.Key == Key.Down && SuggestionBorder.Visibility == Visibility.Visible)
        {
            SuggestionList.Focus();
            SuggestionList.SelectedIndex = 0;
            e.Handled = true;
        }
    }

    // ── Suggestion list interaction ────────────────────────────────────────────

    private void SuggestionList_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (SuggestionList.SelectedItem is string name)
            ApplySuggestion(name);
    }

    private void SuggestionList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && SuggestionList.SelectedItem is string name)
        {
            ApplySuggestion(name);
            _ = SaveFacePopupAsync();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            SuggestionBorder.Visibility = Visibility.Collapsed;
            FaceNameBox.Focus();
            e.Handled = true;
        }
    }

    private void ApplySuggestion(string name)
    {
        FaceNameBox.Text            = name;
        FaceNameBox.CaretIndex      = name.Length;
        SuggestionBorder.Visibility = Visibility.Collapsed;
        FaceNameBox.Focus();
    }

    // ── Sidebar face-name autocomplete ────────────────────────────────────────

    private TextBox?           _sidebarBox    = null;
    private FaceItemViewModel? _sidebarFace   = null;
    private string?            _sidebarAiName = null;

    private async void SidebarNameBox_GotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox tb) return;
        _sidebarBox  = tb;
        _sidebarFace = tb.DataContext as FaceItemViewModel;
        _sidebarAiName = null;

        var vm = (PhotoViewerViewModel)DataContext;
        if (_allNamedPersons.Count == 0)
            _allNamedPersons = (await vm.GetAllNamedPersonsAsync()).ToList();

        PopulateSidebarDropdown(tb.Text);
        SidebarDropdown.PlacementTarget = tb;
        SidebarDropdown.IsOpen = SidebarNameList.Items.Count > 0;

        // Async: load AI suggestion then push it to top of list
        if (_sidebarFace?.Embedding != null)
        {
            var suggestion = await vm.GetAiSuggestionAsync(_sidebarFace.Embedding);
            if (suggestion != null && _sidebarBox == tb) // still the same box
            {
                _sidebarAiName = suggestion.Value.Name;
                PopulateSidebarDropdown(tb.Text);
                SidebarDropdown.IsOpen = SidebarNameList.Items.Count > 0;
            }
        }
    }

    private void SidebarNameBox_LostFocus(object sender, RoutedEventArgs e)
    {
        // Delay so a click on SidebarNameList registers before we close.
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(() =>
        {
            if (!SidebarNameList.IsKeyboardFocusWithin)
                CloseSidebarDropdown();
        }));
    }

    private void SidebarNameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox tb || tb != _sidebarBox) return;
        PopulateSidebarDropdown(tb.Text);
        SidebarDropdown.IsOpen = SidebarNameList.Items.Count > 0;
    }

    private void SidebarNameBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { CloseSidebarDropdown(); return; }
        if (e.Key == Key.Down && SidebarDropdown.IsOpen && SidebarNameList.Items.Count > 0)
        {
            SidebarNameList.Focus();
            SidebarNameList.SelectedIndex = 0;
            e.Handled = true;
        }
    }

    private void SidebarNameList_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (SidebarNameList.SelectedItem is string name)
            ApplySidebarSuggestion(name);
    }

    private void SidebarNameList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && SidebarNameList.SelectedItem is string name)
        {
            ApplySidebarSuggestion(name);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CloseSidebarDropdown();
            _sidebarBox?.Focus();
            e.Handled = true;
        }
    }

    private void PopulateSidebarDropdown(string filter)
    {
        var matches = RankedNames(filter, _sidebarFace?.Embedding).ToList();

        // AI suggestion is already ranked by embedding; if it arrived async and is at position > 0, pin it first.
        if (_sidebarAiName != null && matches.Remove(_sidebarAiName))
            matches.Insert(0, _sidebarAiName);

        SidebarNameList.ItemsSource = matches.Count > 0 ? matches : null;
    }

    private void ApplySidebarSuggestion(string name)
    {
        if (_sidebarFace != null)
            _sidebarFace.NameInput = name;
        CloseSidebarDropdown();
        _sidebarBox?.Focus();
    }

    private void CloseSidebarDropdown()
    {
        SidebarDropdown.IsOpen = false;
        _sidebarBox   = null;
        _sidebarFace  = null;
        _sidebarAiName = null;
    }

    // ── Save / Cancel ──────────────────────────────────────────────────────────

    private async void FaceNameSave_Click(object sender, RoutedEventArgs e)
        => await SaveFacePopupAsync();

    private async Task SaveFacePopupAsync()
    {
        var name = FaceNameBox.Text.Trim();
        var vm   = (PhotoViewerViewModel)DataContext;

        if (_pendingMentionMode)
        {
            _pendingMentionMode  = false;
            FaceNamePopup.IsOpen = false;
            if (!string.IsNullOrWhiteSpace(name))
                await vm.AddPersonMentionAsync(name);
            return;
        }

        if (_pendingFace == null) return;
        var face = _pendingFace;
        _pendingFace = null;
        FaceNamePopup.IsOpen = false;

        if (!string.IsNullOrWhiteSpace(name))
        {
            await vm.AssignFacePersonAsync(face, name);
            RedrawFaceOverlay();  // refresh box color + label
        }
    }

    private async void FaceNameUnlink_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingFace == null) return;
        var face = _pendingFace;
        _pendingFace = null;
        FaceNamePopup.IsOpen = false;

        var vm = (PhotoViewerViewModel)DataContext;
        await vm.UnlinkFaceAsync(face.FaceId);
        face.ClearAssignment();
        RedrawFaceOverlay();
    }

    private void FaceNameCancel_Click(object sender, RoutedEventArgs e)
    {
        FaceNamePopup.IsOpen = false;
        _pendingFace        = null;
        _pendingMentionMode = false;
    }

    // ── Name ranking ─────────────────────────────────────────────────────────

    /// <summary>
    /// Returns names from _allNamedPersons filtered by <paramref name="filter"/> and sorted by:
    /// favorites first, then cosine similarity to <paramref name="faceEmbedding"/> if available,
    /// then by FaceCount (most tagged) descending.
    /// </summary>
    private IEnumerable<string> RankedNames(string filter, byte[]? faceEmbedding)
    {
        var candidates = _allNamedPersons.Where(p => !string.IsNullOrEmpty(p.Name));

        if (filter.Length > 0)
            candidates = candidates.Where(p => p.Name!.Contains(filter, StringComparison.OrdinalIgnoreCase));

        IOrderedEnumerable<Person> sorted;
        if (faceEmbedding != null)
        {
            sorted = candidates
                .OrderByDescending(p => p.IsFavorite)
                .ThenByDescending(p => p.AverageEmbedding != null
                    ? CosineSimilarity(faceEmbedding, p.AverageEmbedding)
                    : -1f);
        }
        else
        {
            sorted = candidates
                .OrderByDescending(p => p.IsFavorite)
                .ThenByDescending(p => p.FaceCount);
        }

        return sorted.Select(p => p.Name!);
    }

    private static float CosineSimilarity(byte[] a, byte[] b)
    {
        var fa = MemoryMarshal.Cast<byte, float>(a);
        var fb = MemoryMarshal.Cast<byte, float>(b);
        var len = Math.Min(fa.Length, fb.Length);
        float dot = 0f;
        for (int i = 0; i < len; i++) dot += fa[i] * fb[i];
        return dot; // embeddings are L2-normalized, so dot product == cosine similarity
    }

    // ── Zoom ─────────────────────────────────────────────────────────────────

    private void OnImageMouseWheel(object sender, MouseWheelEventArgs e)
    {
        ApplyZoomDelta(e.Delta > 0 ? ZoomStep : -ZoomStep);
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;

        // Ctrl+Plus / Ctrl+= → zoom in
        if (ctrl && (e.Key == Key.OemPlus || e.Key == Key.Add))
        {
            ApplyZoomDelta(ZoomStep);
            e.Handled = true;
            return;
        }
        // Ctrl+Minus / Ctrl+_ → zoom out
        if (ctrl && (e.Key == Key.OemMinus || e.Key == Key.Subtract))
        {
            ApplyZoomDelta(-ZoomStep);
            e.Handled = true;
            return;
        }
        // Ctrl+0 → reset
        if (ctrl && (e.Key == Key.D0 || e.Key == Key.NumPad0))
        {
            ResetZoom();
            e.Handled = true;
            return;
        }

        // Arrow key panning (only when zoomed in, to avoid stealing nav from unzoomed state)
        if (_zoom > 1.0)
        {
            const double ArrowStep = 40;
            double dx = 0, dy = 0;
            switch (e.Key)
            {
                case Key.Left:  dx =  ArrowStep; break;
                case Key.Right: dx = -ArrowStep; break;
                case Key.Up:    dy =  ArrowStep; break;
                case Key.Down:  dy = -ArrowStep; break;
            }
            if (dx != 0 || dy != 0)
            {
                ApplyTranslation(dx, dy);
                e.Handled = true;
                return;
            }
        }

        base.OnKeyDown(e);
    }

    private void ApplyZoomDelta(double delta)
    {
        _zoom = Math.Clamp(_zoom + delta, MinZoom, MaxZoom);
        ImageScale.ScaleX       = _zoom;
        ImageScale.ScaleY       = _zoom;
        FaceOverlayScale.ScaleX = _zoom;
        FaceOverlayScale.ScaleY = _zoom;
        // Re-clamp pan so we don't reveal black after zooming out.
        ClampTranslation();
    }

    private void ResetZoom()
    {
        _zoom                    = 1.0;
        ImageScale.ScaleX        = 1.0;
        ImageScale.ScaleY        = 1.0;
        FaceOverlayScale.ScaleX  = 1.0;
        FaceOverlayScale.ScaleY  = 1.0;
        ImageTranslate.X         = 0.0;
        ImageTranslate.Y         = 0.0;
        FaceOverlayTranslate.X   = 0.0;
        FaceOverlayTranslate.Y   = 0.0;
    }

    // ── Panning ──────────────────────────────────────────────────────────

    private void OnImageMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_zoom > 1.0 && e.ChangedButton == MouseButton.Left && e.ButtonState == MouseButtonState.Pressed)
        {
            _isPanning  = true;
            _panCapture = (UIElement)sender;
            _panStartPoint = e.GetPosition(_panCapture);
            _panCapture.CaptureMouse();
            ((FrameworkElement)_panCapture).Cursor = Cursors.Hand;
            e.Handled = true;
        }
    }

    private void OnImageMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isPanning || _panCapture == null) return;

        Point currentPoint = e.GetPosition(_panCapture);
        double deltaX = currentPoint.X - _panStartPoint.X;
        double deltaY = currentPoint.Y - _panStartPoint.Y;
        ApplyTranslation(deltaX, deltaY);
        _panStartPoint = currentPoint;
    }

    private void OnImageMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_isPanning && _panCapture != null)
        {
            _isPanning = false;
            _panCapture.ReleaseMouseCapture();
            ((FrameworkElement)_panCapture).Cursor = Cursors.Arrow;
            _panCapture = null;
            e.Handled = true;
        }
    }

    /// <summary>
    /// Applies a translation delta and immediately clamps so the image edge
    /// never moves past the frame boundary (no black revealed on any side).
    /// </summary>
    private void ApplyTranslation(double dx, double dy)
    {
        ImageTranslate.X       += dx;
        ImageTranslate.Y       += dy;
        FaceOverlayTranslate.X += dx;
        FaceOverlayTranslate.Y += dy;
        ClampTranslation();
    }

    /// <summary>
    /// Clamps the current translation so that, at the current zoom level,
    /// the image never reveals the black background on any edge.
    ///
    /// The image is rendered at Stretch=Uniform centred in the container.
    /// After scaling by <see cref="_zoom"/> around the centre (RenderTransformOrigin=0.5,0.5)
    /// the image grows symmetrically: each edge moves outward by (zoom-1)/2 × rendered size.
    /// The maximum safe translation on each axis is that overhang — beyond it the edge
    /// would cross the container boundary and show black.
    /// </summary>
    private void ClampTranslation()
    {
        if (_zoom <= 1.0)
        {
            // At or below 1× there is no overhang — lock translation to zero.
            ImageTranslate.X       = 0;
            ImageTranslate.Y       = 0;
            FaceOverlayTranslate.X = 0;
            FaceOverlayTranslate.Y = 0;
            return;
        }

        // Container size (the image-area Grid column).
        double containerW = ViewerImage.ActualWidth;
        double containerH = ViewerImage.ActualHeight;
        if (containerW <= 0 || containerH <= 0) return;

        // Rendered (letterboxed) image size inside the container at zoom=1.
        double imgW = ViewerImage.Source?.Width  ?? containerW;
        double imgH = ViewerImage.Source?.Height ?? containerH;
        double scale    = Math.Min(containerW / imgW, containerH / imgH);
        double rendW    = imgW * scale;
        double rendH    = imgH * scale;

        // Overhang = how far each edge already sticks out past the container at this zoom.
        // At zoom=1 overhang=0; at zoom=2 overhang = rendW/2, etc.
        double maxTx = (_zoom - 1.0) / 2.0 * rendW;
        double maxTy = (_zoom - 1.0) / 2.0 * rendH;

        double clampedX = Math.Clamp(ImageTranslate.X, -maxTx, maxTx);
        double clampedY = Math.Clamp(ImageTranslate.Y, -maxTy, maxTy);

        ImageTranslate.X       = clampedX;
        ImageTranslate.Y       = clampedY;
        FaceOverlayTranslate.X = clampedX;
        FaceOverlayTranslate.Y = clampedY;
    }

    private void PathText_TextChanged(object sender, TextChangedEventArgs e)
        => TextBoxHelpers.ScrollToHome(sender, e);

    private void SelectableText_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        => TextBoxHelpers.SelectAllOnFirstFocus(sender, e);

    private void NameBox_KeyDown(object sender, KeyEventArgs e)
        => PhotoViewerWindow_NameBox_KeyDown(sender, e);

    private void PhotoViewerWindow_NameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox tb || tb.Tag is not FaceItemViewModel card) return;
        var vm = (PhotoViewerViewModel)DataContext;
        if (e.Key == Key.Enter)
        {
            _ = vm.SaveFaceNamesCommand.ExecuteAsync(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            vm.SkipFacePromptOnceCommand.Execute(null);
            e.Handled = true;
        }
    }

    // ── Debug display ─────────────────────────────────────────────────────

    /// <summary>Update debug display with face detection status and counts.</summary>
    private void UpdateDebugDisplay()
    {
        var vm = (PhotoViewerViewModel)DataContext;
        if (vm.Current == null)
        {
            DebugStatus.Text = "No photo loaded";
            DebugFaceCount.Text = "";
            return;
        }

        var status = vm.Current.FaceDetectionStatus;
        DebugStatus.Text = $"Face detection: {status}";

        var faceCount = vm.Faces.Count;
        var linked = vm.Faces.Count(f => f.PersonId.HasValue);
        var unlinked = faceCount - linked;

        if (faceCount == 0)
            DebugFaceCount.Text = "No faces detected";
        else
            DebugFaceCount.Text = $"Faces: {faceCount} ({linked} linked, {unlinked} unknown)";
    }
}
