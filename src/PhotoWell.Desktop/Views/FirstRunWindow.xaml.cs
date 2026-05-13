using System.ComponentModel;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using PhotoWell.Desktop.Models;
using PhotoWell.Desktop.ViewModels;

namespace PhotoWell.Desktop.Views;

public partial class FirstRunWindow : Window
{
    private readonly FirstRunViewModel _vm;

    public FirstRunWindow(FirstRunViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;

        vm.RequestClose += () => Close();
        vm.PropertyChanged += OnVmPropertyChanged;

        Loaded += OnLoaded;
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { vm.SkipCommand.Execute(null); } };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyCard(_vm.CurrentCard, animate: false);
        AnimateProgressBar(_vm.CurrentIndex, animate: false);
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(FirstRunViewModel.CurrentCard)) return;

        Dispatcher.InvokeAsync(async () =>
        {
            // 1. Fade out card body
            await FadeCardBodyAsync(fadeIn: false);

            // 2. Swap content
            ApplyCard(_vm.CurrentCard, animate: false);
            AnimateProgressBar(_vm.CurrentIndex, animate: true);

            // 3. Fade + translate in
            await FadeCardBodyAsync(fadeIn: true);
        });
    }

    // ── Card content ──────────────────────────────────────────────────────────

    private void ApplyCard(CardData card, bool animate)
    {
        // Visual area template
        VisualArea.ContentTemplate = SelectVisualTemplate(card.Visual);
        VisualArea.Content         = card;

        // Eyebrow
        EyebrowText.Text = card.Eyebrow.ToUpperInvariant();

        // Title inline runs
        BuildTitleInlines(card);

        // Body
        BodyText.Text = card.Body;
    }

    private DataTemplate SelectVisualTemplate(CardVisualKey key) =>
        key switch
        {
            CardVisualKey.Welcome         => (DataTemplate)Resources["Welcome"],
            CardVisualKey.Privacy         => (DataTemplate)Resources["Privacy"],
            CardVisualKey.TheAI           => (DataTemplate)Resources["TheAI"],
            CardVisualKey.Search          => (DataTemplate)Resources["Search"],
            CardVisualKey.TwoModes        => (DataTemplate)Resources["TwoModes"],
            CardVisualKey.FavoritesAlbums => (DataTemplate)Resources["FavoritesAlbums"],
            CardVisualKey.FindDuplicates  => (DataTemplate)Resources["FindDuplicates"],
            CardVisualKey.LetsGo          => (DataTemplate)Resources["LetsGo"],
            _                             => (DataTemplate)Resources["Welcome"],
        };

    private void BuildTitleInlines(CardData card)
    {
        TitleText.Inlines.Clear();

        if (!string.IsNullOrEmpty(card.TitlePrefix))
            TitleText.Inlines.Add(new Run(card.TitlePrefix));

        if (!string.IsNullOrEmpty(card.TitleItalic))
            TitleText.Inlines.Add(new Run(card.TitleItalic)
            {
                FontStyle  = FontStyles.Italic,
                Foreground = new SolidColorBrush(Color.FromRgb(0x2B, 0x4A, 0x3F)),
            });

        if (!string.IsNullOrEmpty(card.TitleSuffix))
            TitleText.Inlines.Add(new Run(card.TitleSuffix));
    }

    // ── Progress bar ──────────────────────────────────────────────────────────

    private void AnimateProgressBar(int index, bool animate)
    {
        double totalWidth      = ProgressBarHost.ActualWidth;
        double targetFraction  = (index + 1.0) / _vm.TotalCards;
        double targetWidth     = totalWidth * targetFraction;

        if (animate)
        {
            var anim = new DoubleAnimation(targetWidth,
                new Duration(TimeSpan.FromMilliseconds(250)))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            ProgressFill.BeginAnimation(WidthProperty, anim);
        }
        else
        {
            ProgressFill.BeginAnimation(WidthProperty, null);
            ProgressFill.Width = totalWidth > 0 ? targetWidth : 0;
        }
    }

    // ── Card body fade + slide ────────────────────────────────────────────────

    private Task FadeCardBodyAsync(bool fadeIn)
    {
        var tcs = new TaskCompletionSource();

        if (fadeIn)
        {
            var tt = new TranslateTransform(0, 10);
            CardBodyGrid.RenderTransform = tt;

            var fadeAnim = new DoubleAnimation(0, 1,
                new Duration(TimeSpan.FromMilliseconds(200)))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            var slideAnim = new DoubleAnimation(10, 0,
                new Duration(TimeSpan.FromMilliseconds(200)))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };

            fadeAnim.Completed += (_, _) => tcs.TrySetResult();
            CardBodyGrid.BeginAnimation(OpacityProperty, fadeAnim);
            tt.BeginAnimation(TranslateTransform.YProperty, slideAnim);
        }
        else
        {
            var fadeAnim = new DoubleAnimation(1, 0,
                new Duration(TimeSpan.FromMilliseconds(150)));

            fadeAnim.Completed += (_, _) => tcs.TrySetResult();
            CardBodyGrid.BeginAnimation(OpacityProperty, fadeAnim);
        }

        return tcs.Task;
    }

    // ── Drag to move ──────────────────────────────────────────────────────────

    private void OnHeaderMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
            DragMove();
    }
}
