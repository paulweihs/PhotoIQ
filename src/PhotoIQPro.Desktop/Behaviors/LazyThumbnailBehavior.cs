using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using PhotoIQPro.Core.Models;

namespace PhotoIQPro.Desktop.Behaviors;

/// <summary>
/// Attached behavior for demand-driven thumbnail loading in the virtualized photo grid.
///
/// Key design decisions:
/// - Each container element owns a CancellationTokenSource stored as an attached property.
///   When the container is recycled to a new MediaFile, the previous CTS is cancelled so
///   any in-flight disk read for the old item is abandoned immediately.
/// - A static SemaphoreSlim caps concurrent BitmapImage decodes at 8. Items that scroll
///   off while waiting for a slot are cancelled at the semaphore — they never touch disk.
///   This prevents thread-pool saturation during fast scrolling.
/// - LoadedThumbnail is cleared on the outgoing MediaFile when a container is recycled,
///   so memory stays bounded to items that have actually been visible.
/// - If LoadedThumbnail is already set (item revisited after scrolling), loading is skipped.
/// </summary>
public static class LazyThumbnailBehavior
{
    // ── Attached properties ───────────────────────────────────────────────────

    public static readonly DependencyProperty EnabledProperty =
        DependencyProperty.RegisterAttached(
            "Enabled",
            typeof(bool),
            typeof(LazyThumbnailBehavior),
            new PropertyMetadata(false, OnEnabledChanged));

    private static readonly DependencyProperty LoadCtsProperty =
        DependencyProperty.RegisterAttached(
            "LoadCts",
            typeof(CancellationTokenSource),
            typeof(LazyThumbnailBehavior),
            new PropertyMetadata(null));

    public static void SetEnabled(FrameworkElement element, bool value)
        => element.SetValue(EnabledProperty, value);
    public static bool GetEnabled(FrameworkElement element)
        => (bool)element.GetValue(EnabledProperty);

    // ── Throttle: max 8 concurrent BitmapImage decodes ────────────────────────
    private static readonly SemaphoreSlim _throttle = new(8, 8);

    // ── Event wiring ──────────────────────────────────────────────────────────

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is FrameworkElement fe && (bool)e.NewValue)
        {
            fe.DataContextChanged += OnDataContextChanged;
            // Loaded fires after the element enters the visual tree. Catches the case
            // where DataContext is inherited before we subscribe to DataContextChanged
            // (common on initial virtualizing panel population).
            fe.Loaded += OnLoaded;
        }
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not MediaFile mf) return;
        StartLoad(fe, mf);
    }

    private static void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;

        // Cancel and replace the CTS for this container.
        var oldCts = (CancellationTokenSource?)fe.GetValue(LoadCtsProperty);
        oldCts?.Cancel();

        // Clear the outgoing item's bitmap so GC can reclaim it.
        if (e.OldValue is MediaFile old)
            old.LoadedThumbnail = null;

        if (e.NewValue is not MediaFile mf) return;
        StartLoad(fe, mf);
    }

    private static void StartLoad(FrameworkElement fe, MediaFile mf)
    {
        var cts = new CancellationTokenSource();
        fe.SetValue(LoadCtsProperty, cts);
        _ = LoadAsync(fe, mf, cts.Token);
    }

    // ── Core load logic ───────────────────────────────────────────────────────

    private static async Task LoadAsync(FrameworkElement fe, MediaFile mf, CancellationToken ct)
    {
        if (mf.LoadedThumbnail != null) return;   // already cached from a previous visit

        var path = mf.ThumbnailMedium ?? mf.ThumbnailSmall;
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

        // Wait for a decode slot. If cancelled while queued (item scrolled off),
        // bail out without ever touching disk — this is the key to fast-scroll recovery.
        try { await _throttle.WaitAsync(ct); }
        catch (OperationCanceledException) { return; }

        try
        {
            if (ct.IsCancellationRequested) return;

            var bmp = await Task.Run(() =>
            {
                if (ct.IsCancellationRequested) return null;
                try
                {
                    var b = new BitmapImage();
                    b.BeginInit();
                    b.UriSource        = new Uri(path);
                    b.DecodePixelWidth = 512;
                    b.CacheOption      = BitmapCacheOption.OnLoad;
                    b.EndInit();
                    b.Freeze();
                    return (object?)b;
                }
                catch { return null; }
            }, ct);

            // Guard: DataContext may have changed while we were decoding.
            if (!ct.IsCancellationRequested && fe.DataContext == mf && bmp != null)
                mf.LoadedThumbnail = bmp;
        }
        catch (OperationCanceledException) { }
        finally { _throttle.Release(); }
    }
}
