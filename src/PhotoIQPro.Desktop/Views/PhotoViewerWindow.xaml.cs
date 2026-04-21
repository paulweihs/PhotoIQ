using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using PhotoIQPro.Desktop.ViewModels;

namespace PhotoIQPro.Desktop.Views;

public partial class PhotoViewerWindow : Window
{
    private double _zoom = 1.0;
    private const double ZoomStep = 0.15;
    private const double MinZoom  = 0.5;
    private const double MaxZoom  = 5.0;

    private Point _panStartPoint;
    private bool _isPanning = false;

    private readonly SolidColorBrush _faceKnownBrush   = new(Color.FromArgb(0xFF, 0x89, 0xB4, 0xFA)); // accent blue
    private readonly SolidColorBrush _faceUnknownBrush = new(Color.FromArgb(0xAA, 0xFF, 0xFF, 0xFF)); // dim white
    private readonly SolidColorBrush _labelBgBrush     = new(Color.FromArgb(0xCC, 0x1E, 0x1E, 0x2E));
    private readonly SolidColorBrush _labelFgBrush     = new(Color.FromArgb(0xFF, 0xCD, 0xD6, 0xF4));

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
            }
        };

        // Redraw when face data arrives (loaded async after image).
        vm.Faces.CollectionChanged += (_, e) =>
        {
            if (e.Action is NotifyCollectionChangedAction.Reset
                         or NotifyCollectionChangedAction.Add
                         or NotifyCollectionChangedAction.Remove)
                RedrawFaceOverlay();
        };

        // Redraw on resize so boxes stay aligned when the window is resized.
        FaceOverlayCanvas.SizeChanged += (_, _) => RedrawFaceOverlay();
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
            double left  = offsetX + face.X * rendW;
            double top   = offsetY + face.Y * rendH;
            double w     = face.FaceWidth  * rendW;
            double h     = face.FaceHeight * rendH;

            bool   isNamed     = !string.IsNullOrEmpty(face.MatchedName);
            string label       = isNamed ? face.MatchedName! : "Unknown";
            var    strokeBrush = isNamed ? _faceKnownBrush : _faceUnknownBrush;

            // Bounding box rectangle.
            var rect = new Rectangle
            {
                Width           = w,
                Height          = h,
                Stroke          = strokeBrush,
                StrokeThickness = 2,
                Fill            = Brushes.Transparent,
                Cursor          = isNamed ? Cursors.Hand : Cursors.Arrow,
                ToolTip         = isNamed ? $"Show all photos of {label}" : "Unknown person",
                IsHitTestVisible = true,
                Tag             = face,
            };
            if (isNamed)
                rect.MouseLeftButtonDown += FaceRect_Click;

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
                IsHitTestVisible = isNamed,
                Cursor        = isNamed ? Cursors.Hand : Cursors.Arrow,
                Tag           = face,
            };
            if (isNamed)
                labelBorder.MouseLeftButtonDown += FaceRect_Click;

            double labelY = top + h + 3;
            // If label would clip below canvas, show it above the box instead.
            if (labelY + 20 > canvasH) labelY = top - 22;

            Canvas.SetLeft(labelBorder, left);
            Canvas.SetTop(labelBorder,  labelY);
            FaceOverlayCanvas.Children.Add(labelBorder);
        }
    }

    private void FaceRect_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is FaceItemViewModel face)
        {
            var vm = (PhotoViewerViewModel)DataContext;
            vm.JumpToPersonCommand.Execute(face);
            e.Handled = true;
        }
    }

    // ── Zoom ─────────────────────────────────────────────────────────────────

    private void OnImageMouseWheel(object sender, MouseWheelEventArgs e)
    {
        _zoom = Math.Clamp(_zoom + (e.Delta > 0 ? ZoomStep : -ZoomStep), MinZoom, MaxZoom);
        ImageScale.ScaleX        = _zoom;
        ImageScale.ScaleY        = _zoom;
        FaceOverlayScale.ScaleX  = _zoom;
        FaceOverlayScale.ScaleY  = _zoom;
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.D0 && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            ResetZoom();
            e.Handled = true;
        }
        base.OnKeyDown(e);
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
        if (_zoom > 1.0 && e.ChangedButton == MouseButton.Middle && e.ButtonState == MouseButtonState.Pressed)
        {
            _isPanning = true;
            _panStartPoint = e.GetPosition(ViewerImage);
            ViewerImage.CaptureMouse();
            ViewerImage.Cursor = Cursors.Hand;
            e.Handled = true;
        }
    }

    private void OnImageMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isPanning) return;

        Point currentPoint = e.GetPosition(ViewerImage);
        double deltaX = currentPoint.X - _panStartPoint.X;
        double deltaY = currentPoint.Y - _panStartPoint.Y;

        ImageTranslate.X += deltaX;
        ImageTranslate.Y += deltaY;
        FaceOverlayTranslate.X += deltaX;
        FaceOverlayTranslate.Y += deltaY;

        _panStartPoint = currentPoint;
    }

    private void OnImageMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_isPanning)
        {
            _isPanning = false;
            ViewerImage.ReleaseMouseCapture();
            ViewerImage.Cursor = Cursors.Arrow;
            e.Handled = true;
        }
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
}
